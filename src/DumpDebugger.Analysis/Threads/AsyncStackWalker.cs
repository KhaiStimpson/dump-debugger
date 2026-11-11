using DumpDebugger.Analysis.Frames;
using DumpDebugger.Analysis.Heap;
using DumpDebugger.Core.Contracts;
using Microsoft.Diagnostics.Runtime;

namespace DumpDebugger.Analysis.Threads;

/// <summary>
/// Async "logical stacks" (like <c>dumpasync</c>): an awaiting method has no thread, so its
/// stack lives on the heap as a chain of async state-machine boxes. Each pending box's
/// <c>m_continuationObject</c> points (directly, via a MoveNext delegate, or via a task
/// continuation) at the box that awaits it. Chains start at boxes nothing pending awaits on
/// (the innermost operations) and are grouped by method sequence and what they're waiting for.
/// </summary>
public static class AsyncStackWalker
{
    private const int CompletedMask = 0x1000000 | 0x200000 | 0x400000; // RAN_TO_COMPLETION | FAULTED | CANCELED
    private const int MaxDepth = 40;

    public static AsyncStacksResult Walk(ClrRuntime runtime, HeapIndex index, CancellationToken ct)
    {
        var heap = runtime.Heap;
        var boxTypes = new HashSet<int>();
        for (var t = 0; t < index.Types.Count; t++)
        {
            if (index.Types[t].Name?.Contains("StateMachineBox<", StringComparison.Ordinal) == true)
            {
                boxTypes.Add(t);
            }
        }

        var pending = new Dictionary<ulong, ClrObject>();
        for (var i = 0; i < index.Count; i++)
        {
            if (!boxTypes.Contains(index.TypeIds[i]))
            {
                continue;
            }

            if ((i & 0xfff) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            var box = heap.GetObject(index.Addresses[i]);
            if (box.TryReadField<int>("m_stateFlags", out var flags) && (flags & CompletedMask) == 0)
            {
                pending[box.Address] = box;
            }
        }

        var parent = new Dictionary<ulong, ulong>();
        foreach (var box in pending.Values)
        {
            foreach (var p in Continuations(box))
            {
                if (pending.ContainsKey(p) && p != box.Address)
                {
                    parent[box.Address] = p;
                    break;
                }
            }
        }

        var awaited = parent.Values.ToHashSet();
        var groups = new Dictionary<string, (List<string> Frames, string? FirstApp, string Awaiting, List<ulong> Samples, int Count)>();
        foreach (var leaf in pending.Keys.Where(b => !awaited.Contains(b)))
        {
            var frames = new List<string>();
            string? firstApp = null;
            var seen = new HashSet<ulong>();
            for (var cur = leaf; seen.Add(cur) && frames.Count < MaxDepth; )
            {
                var method = MethodOf(pending[cur]);
                frames.Add(method);
                if (firstApp is null && IsApp(pending[cur]))
                {
                    firstApp = method;
                }
                if (!parent.TryGetValue(cur, out cur))
                {
                    break;
                }
            }

            var awaiting = Awaiting(pending[leaf]);
            var key = string.Join('|', frames) + "#" + awaiting;
            if (!groups.TryGetValue(key, out var g))
            {
                g = (frames, firstApp, awaiting, [], 0);
            }

            if (g.Samples.Count < 5)
            {
                g.Samples.Add(leaf);
            }

            groups[key] = (g.Frames, g.FirstApp, g.Awaiting, g.Samples, g.Count + 1);
        }

        var result = groups.Values
            .Select(g => new AsyncStackGroup(
                g.Count,
                g.Frames,
                g.Awaiting,
                g.FirstApp,
                g.Samples))
            .OrderByDescending(g => g.FirstAppFrame is not null)
            .ThenByDescending(g => g.Count)
            .ToList();
        return new AsyncStacksResult(result, pending.Count);
    }

    private static IEnumerable<ulong> Continuations(ClrObject box)
    {
        if (!box.TryReadObjectField("m_continuationObject", out var cont) || cont.IsNull || !cont.IsValid)
        {
            yield break;
        }

        foreach (var target in Resolve(cont, 0))
        {
            yield return target;
        }
    }

    private static IEnumerable<ulong> Resolve(ClrObject cont, int depth)
    {
        if (cont.IsNull || !cont.IsValid || cont.Type is null || depth > 3)
        {
            yield break;
        }

        var name = cont.Type.Name ?? "";
        if (name.Contains("StateMachineBox<", StringComparison.Ordinal))
        {
            yield return cont.Address;
            yield break;
        }

        if (cont.IsDelegate)
        {
            var target = cont.AsDelegate().GetDelegateTarget()?.TargetObject;
            if (target is { IsNull: false } t)
            {
                foreach (var r in Resolve(t, depth + 1))
                {
                    yield return r;
                }
            }

            yield break;
        }

        if (name.Contains("Continuation", StringComparison.Ordinal))
        {
            foreach (var field in new[] { "m_action", "m_task", "_stateMachine" })
            {
                if (cont.TryReadObjectField(field, out var inner) && !inner.IsNull)
                {
                    foreach (var r in Resolve(inner, depth + 1))
                    {
                        yield return r;
                    }
                }
            }

            yield break;
        }

        if (name.StartsWith("System.Collections.Generic.List<", StringComparison.Ordinal) && cont.TryReadObjectField("_items", out var items) && items.IsArray)
        {
            foreach (var item in items.EnumerateReferences(carefully: false, considerDependantHandles: false))
            {
                foreach (var r in Resolve(item, depth + 1))
                {
                    yield return r;
                }
            }
        }
    }

    private static bool IsApp(ClrObject box)
    {
        var smType = StateMachineType(box);
        return smType is not null && FrameClassifier.IsAppType(smType.Name, smType.Module);
    }

    public static string MethodOf(ClrObject box) => StateMachineType(box) is { } t ? FrameClassifier.AsyncMethodName(t.Name ?? "?") : "?";

    /// <summary>
    /// The state machine's real type. Release builds embed it as a struct (the field's type);
    /// Debug builds use a class, and shared generic boxes declare the field as <c>__Canon</c>, so
    /// the object it points at has to be read.
    /// </summary>
    public static ClrType? StateMachineType(ClrObject box)
    {
        var field = box.Type?.GetFieldByName("StateMachine");
        if (field is null)
        {
            return null;
        }

        if (field.ElementType == ClrElementType.Struct)
        {
            return field.Type;
        }

        var sm = field.ReadObject(box.Address, false);
        return sm.IsNull ? field.Type : sm.Type;
    }

    /// <summary>What the innermost box is awaiting, from the awaiter fields of its state machine.</summary>
    private static string Awaiting(ClrObject box)
    {
        try
        {
            var smField = box.Type?.GetFieldByName("StateMachine");
            var smType = StateMachineType(box);
            if (smField is null || smType is null)
            {
                return "something";
            }

            var isStruct = smField.ElementType == ClrElementType.Struct;
            ulong smAddress;
            bool interior;
            if (isStruct)
            {
                smAddress = smField.ReadStruct(box.Address, false).Address;
                interior = true;
            }
            else
            {
                smAddress = smField.ReadObject(box.Address, false).Address;
                interior = false;
            }

            foreach (var f in smType.Fields.Where(f => f.Name?.StartsWith("<>u__", StringComparison.Ordinal) == true))
            {
                var task = FindObject(box.Type!.Heap, f, smAddress, interior, 0);
                if (task is { } t)
                {
                    return Describe(t);
                }
            }
        }
        catch
        {
        }

        return "something";
    }

    private static ClrObject? FindObject(ClrHeap heap, ClrInstanceField f, ulong address, bool interior, int depth)
    {
        if (f.IsObjectReference)
        {
            var o = f.ReadObject(address, interior);
            return o.IsNull ? null : o;
        }

        if (f.ElementType == ClrElementType.Struct && depth < 3 && f.Type is not null)
        {
            var vt = f.ReadStruct(address, interior);
            foreach (var inner in f.Type.Fields)
            {
                if (FindObject(heap, inner, vt.Address, true, depth + 1) is { } o)
                {
                    return o;
                }
            }
        }

        return null;
    }

    private static string Describe(ClrObject awaited)
    {
        var name = awaited.Type?.Name ?? "";
        if (name.Contains("DelayPromise", StringComparison.Ordinal))
        {
            return "Task.Delay";
        }

        if (name.Contains("Socket", StringComparison.Ordinal))
        {
            return "a socket read or write";
        }

        if (name.Contains("SemaphoreSlim", StringComparison.Ordinal))
        {
            return "SemaphoreSlim.WaitAsync";
        }

        if (name.Contains("StateMachineBox<", StringComparison.Ordinal))
        {
            return MethodOf(awaited);
        }

        if (name.StartsWith("System.Threading.Tasks.Task", StringComparison.Ordinal))
        {
            return "a Task nobody has completed (e.g. a TaskCompletionSource)";
        }

        return FrameClassifier.ShortTypeName(name);
    }
}
