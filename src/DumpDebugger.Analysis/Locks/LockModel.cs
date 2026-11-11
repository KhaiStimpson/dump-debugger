using DumpDebugger.Analysis.Frames;
using DumpDebugger.Analysis.Threads;
using DumpDebugger.Core.Contracts;
using Microsoft.Diagnostics.Runtime;

namespace DumpDebugger.Analysis.Locks;

public sealed class LockRecord
{
    public required ulong Address { get; init; }
    public required string TypeName { get; init; }
    public required int? Owner { get; init; }
    public required int Recursion { get; init; }
    public string Name { get; set; } = "";
    public bool Named { get; set; }
    public string? AcquiredAt { get; set; }

    /// <summary>The method that took the lock, without parameters (<c>CheckoutService.Commit</c>).</summary>
    public string? AcquiredIn { get; set; }
    public List<int> Waiters { get; } = [];
    public bool InCycle { get; set; }

    public EntityRef Ref => EntityUri.LockRef(Address, Name, $"0x{Address:x}");
}

public sealed record CycleRecord(IReadOnlyList<int> Threads, IReadOnlyList<ulong> Locks);

public sealed class LockModel
{
    public required IReadOnlyDictionary<ulong, LockRecord> Locks { get; init; }
    public required IReadOnlyList<CycleRecord> Cycles { get; init; }
}

/// <summary>
/// Builds the wait-for graph with exact edges. Held monitors come from sync blocks (the
/// <c>!syncblk</c> view); the lock a blocked thread wants is the object its
/// <c>Monitor.Enter</c> frame keeps alive on the stack (confirmed on real dumps: the wanted
/// object is a stack root of the Enter frame), owned by some other thread. Sync blocks that
/// have "waiters" but no owner are <c>Monitor.Wait</c> condition variables, not contention, and
/// are ignored. Locks are named after the field that references them (<c>OrderLedger._gate</c>),
/// found among the involved threads' stack objects or in static fields.
/// </summary>
public static class LockModelBuilder
{
    private static readonly HashSet<string> EnterMethods = ["Enter", "Enter_Slowpath", "ReliableEnter", "TryEnter", "TryEnter_Slowpath"];

    public static LockModel Build(ClrRuntime runtime, IReadOnlyList<ThreadRecord> threads, CancellationToken ct)
    {
        var heap = runtime.Heap;
        var byClrAddress = threads.ToDictionary(t => t.ClrAddress);
        var byId = threads.ToDictionary(t => t.Id);
        var locks = new Dictionary<ulong, LockRecord>();

        foreach (var sb in heap.EnumerateSyncBlocks())
        {
            ct.ThrowIfCancellationRequested();
            if (!sb.IsMonitorHeld || sb.HoldingThreadAddress == 0 || !byClrAddress.TryGetValue(sb.HoldingThreadAddress, out var owner))
            {
                continue;
            }

            var obj = heap.GetObject(sb.Object);
            locks[sb.Object] = new LockRecord
            {
                Address = sb.Object,
                TypeName = obj.Type?.Name ?? "System.Object",
                Owner = owner.Id,
                Recursion = sb.RecursionCount,
            };
            owner.Holds.Add(sb.Object);
        }

        // Which held lock is each blocked thread waiting for?
        foreach (var t in threads.Where(t => t.Wait == WaitKind.Lock))
        {
            var enterFrames = t.Methods.Select((m, i) => (m, i))
                .Where(x => x.m?.Type?.Name is "System.Threading.Monitor" or "System.Threading.Lock" && EnterMethods.Contains(x.m.Name ?? ""))
                .Select(x => x.i).ToHashSet();
            var maxEnter = enterFrames.Count > 0 ? enterFrames.Max() : 0;

            var candidates = t.Roots
                .Where(r => locks.TryGetValue(r.Address, out var l) && l.Owner != t.Id)
                .OrderBy(r => enterFrames.Contains(r.FrameIndex) ? 0 : r.FrameIndex <= maxEnter + 1 ? 1 : 2)
                .ThenBy(r => r.FrameIndex)
                .ToList();
            if (candidates.Count == 0)
            {
                continue;
            }

            var wanted = locks[candidates[0].Address];
            t.WaitingOnLock = wanted.Address;
            t.WaitingOnThread = wanted.Owner;
            wanted.Waiters.Add(t.Id);
        }

        // Where was each held lock taken? The deepest application frame that keeps it on the stack.
        foreach (var l in locks.Values)
        {
            if (l.Owner is not { } ownerId || !byId.TryGetValue(ownerId, out var owner))
            {
                continue;
            }

            var frame = owner.Roots.Where(r => r.Address == l.Address && r.FrameIndex >= 0 && owner.Frames[r.FrameIndex].Kind == FrameKind.App)
                .Select(r => r.FrameIndex).DefaultIfEmpty(-1).Max();
            if (frame >= 0)
            {
                var f = owner.Frames[frame];
                l.AcquiredAt = f.SourceFile is not null ? $"{f.Display} · {f.SourceFile}:{f.Line}" : f.Display;
                l.AcquiredIn = owner.Methods[frame] is { } m ? FrameClassifier.DisplayName(m) : f.Display;
            }
        }

        NameLocks(runtime, threads, locks);
        var cycles = FindCycles(threads, locks);

        foreach (var t in threads.Where(t => t.Wait == WaitKind.Lock))
        {
            if (t.InDeadlock)
            {
                t.StateLabel = "Deadlocked";
            }
            else if (t.WaitingOnThread is { } o)
            {
                t.StateLabel = $"Waiting on #{o}";
            }
        }

        return new LockModel { Locks = locks, Cycles = cycles };
    }

    private static List<CycleRecord> FindCycles(IReadOnlyList<ThreadRecord> threads, Dictionary<ulong, LockRecord> locks)
    {
        var byId = threads.ToDictionary(t => t.Id);
        var cycles = new List<CycleRecord>();
        var done = new HashSet<int>();
        foreach (var start in threads)
        {
            var path = new List<int>();
            var onPath = new Dictionary<int, int>();
            var current = start;
            while (current is not null && !done.Contains(current.Id))
            {
                if (onPath.TryGetValue(current.Id, out var at))
                {
                    var members = path.Skip(at).ToList();

                    // Start the cycle at a canonical member so its explanation reads the same on every capture.
                    var first = members.IndexOf(members.OrderBy(id => byId[id].WhereName ?? "", StringComparer.Ordinal).ThenBy(id => id).First());
                    members = members.Skip(first).Concat(members.Take(first)).ToList();
                    var cycleLocks = members.Select(id => byId[id].WaitingOnLock!.Value).ToList();
                    cycles.Add(new CycleRecord(members, cycleLocks));
                    foreach (var id in members)
                    {
                        byId[id].InDeadlock = true;
                    }

                    foreach (var a in cycleLocks)
                    {
                        locks[a].InCycle = true;
                    }

                    break;
                }

                onPath[current.Id] = path.Count;
                path.Add(current.Id);
                current = current.WaitingOnThread is { } next && byId.TryGetValue(next, out var n) ? n : null;
            }

            foreach (var id in path)
            {
                done.Add(id);
            }
        }

        return cycles;
    }

    /// <summary>Names locks after a field that points at them: an instance field on an object on an involved thread's stack, or a static field of an involved type.</summary>
    private static void NameLocks(ClrRuntime runtime, IReadOnlyList<ThreadRecord> threads, Dictionary<ulong, LockRecord> locks)
    {
        var heap = runtime.Heap;
        var byId = threads.ToDictionary(t => t.Id);
        foreach (var l in locks.Values)
        {
            var involved = new List<ThreadRecord>();
            if (l.Owner is { } o && byId.TryGetValue(o, out var ot))
            {
                involved.Add(ot);
            }

            involved.AddRange(l.Waiters.Select(w => byId[w]));

            foreach (var root in involved.SelectMany(t => t.Roots).Where(r => r.IsAppType).DistinctBy(r => r.Address))
            {
                var obj = heap.GetObject(root.Address);
                var field = obj.Type?.Fields.FirstOrDefault(f => f.IsObjectReference && SafeRead(f, obj.Address) == l.Address);
                if (field is not null)
                {
                    l.Name = $"{FrameClassifier.ShortTypeName(obj.Type!.Name)}.{CleanFieldName(field.Name)}";
                    l.Named = true;
                    break;
                }
            }

            if (!l.Named)
            {
                foreach (var type in involved.SelectMany(t => t.Methods).Where(m => m?.Type is not null).Select(m => m!.Type!).Distinct())
                {
                    foreach (var sf in type.StaticFields.Where(f => f.IsObjectReference))
                    {
                        foreach (var domain in runtime.AppDomains)
                        {
                            try
                            {
                                if (sf.ReadObject(domain).Address == l.Address)
                                {
                                    l.Name = $"{FrameClassifier.ShortTypeName(type.Name)}.{CleanFieldName(sf.Name)}";
                                    l.Named = true;
                                }
                            }
                            catch
                            {
                            }
                        }
                    }
                }
            }

            if (!l.Named)
            {
                l.Name = $"{FrameClassifier.ShortTypeName(l.TypeName)} 0x{l.Address:x}";
            }
        }
    }

    /// <summary>Called by the heap walk with the referrers it found for still-unnamed locks.</summary>
    public static void ApplyHeapNames(LockModel model, IReadOnlyDictionary<ulong, string> names)
    {
        foreach (var (address, name) in names)
        {
            if (model.Locks.TryGetValue(address, out var l) && !l.Named)
            {
                l.Name = name;
                l.Named = true;
            }
        }
    }

    public static string CleanFieldName(string? name)
    {
        if (name is null)
        {
            return "?";
        }

        // <Gate>k__BackingField → Gate
        return name.StartsWith('<') && name.Contains(">k__BackingField", StringComparison.Ordinal) ? name[1..name.IndexOf('>')] : name;
    }

    private static ulong SafeRead(ClrInstanceField f, ulong address)
    {
        try
        {
            return f.ReadObject(address, false).Address;
        }
        catch
        {
            return 0;
        }
    }

    public static WaitGraph ToGraph(LockModel model, IReadOnlyList<ThreadRecord> threads, bool cyclesOnly)
    {
        var byId = threads.ToDictionary(t => t.Id);
        var nodes = new List<WaitGraphNode>();
        var edges = new List<WaitGraphEdge>();
        var lockSet = cyclesOnly ? model.Locks.Values.Where(l => l.InCycle).ToList() : model.Locks.Values.Where(l => l.Waiters.Count > 0 || l.InCycle).ToList();
        var lockIds = lockSet.Select(l => l.Address).ToHashSet();
        var shownThreads = new HashSet<int>();

        foreach (var l in lockSet)
        {
            nodes.Add(new WaitGraphNode(EntityUri.Lock(l.Address), EntityKind.Lock, l.Name, $"0x{l.Address:x} · {l.Waiters.Count} waiter{(l.Waiters.Count == 1 ? "" : "s")}", l.AcquiredAt, l.InCycle, 1, []));
        }

        void AddThread(ThreadRecord t)
        {
            if (!shownThreads.Add(t.Id))
            {
                return;
            }

            nodes.Add(new WaitGraphNode(EntityUri.Thread(t.Id), EntityKind.Thread, $"Thread #{t.Id}", t.Request, t.WhereName ?? t.Where, t.InDeadlock, 1, [t.Id]));
        }

        // Owners and cycle members as individual nodes.
        foreach (var l in lockSet)
        {
            if (l.Owner is { } o && byId.TryGetValue(o, out var owner))
            {
                AddThread(owner);
                edges.Add(new WaitGraphEdge(EntityUri.Thread(o), EntityUri.Lock(l.Address), WaitEdgeKind.Owns, l.InCycle && owner.InDeadlock));
            }
        }

        foreach (var t in threads.Where(t => t.WaitingOnLock is { } w && lockIds.Contains(w) && (t.InDeadlock || shownThreads.Contains(t.Id))))
        {
            AddThread(t);
            edges.Add(new WaitGraphEdge(EntityUri.Thread(t.Id), EntityUri.Lock(t.WaitingOnLock!.Value), WaitEdgeKind.WaitsFor, t.InDeadlock));
        }

        // Everyone else waiting on a shown lock collapses into "N threads · where" clusters.
        foreach (var group in threads.Where(t => t.WaitingOnLock is { } w && lockIds.Contains(w) && !shownThreads.Contains(t.Id))
                     .GroupBy(t => (t.WaitingOnLock!.Value, t.WhereName ?? t.Where)))
        {
            var ids = group.Select(t => t.Id).ToList();
            var id = $"cluster/{group.Key.Value:x}/{ids.Min()}";
            nodes.Add(new WaitGraphNode(id, EntityKind.Thread, $"{ids.Count} thread{(ids.Count == 1 ? "" : "s")}", group.Key.Item2, group.First().Request, false, ids.Count, ids));
            edges.Add(new WaitGraphEdge(id, EntityUri.Lock(group.Key.Value), WaitEdgeKind.QueuedBehind, false));
        }

        var cycles = model.Cycles.Select((c, i) => new LockCycle(
            $"cycle/{i + 1}",
            c.Threads,
            c.Locks.Select(EntityUri.Lock).ToList(),
            ExplainCycle(c, model, byId),
            c.Locks.Select(a => model.Locks[a].AcquiredAt ?? "").ToList())).ToList();

        var lockInfos = model.Locks.Values
            .OrderByDescending(l => l.InCycle).ThenByDescending(l => l.Waiters.Count)
            .Select(l => new LockInfo(EntityUri.Lock(l.Address), l.Address, l.Name, l.TypeName, l.Owner, l.AcquiredAt, l.Waiters, l.Recursion, l.InCycle))
            .ToList();

        return new WaitGraph(nodes, edges, cycles, lockInfos);
    }

    /// <summary>"#6 took _gate → _sync. #7 took _sync → _gate." Plain text with {{uri}} chips.</summary>
    public static string ExplainCycle(CycleRecord cycle, LockModel model, IReadOnlyDictionary<int, ThreadRecord> byId)
    {
        var parts = new List<string>();
        foreach (var id in cycle.Threads)
        {
            var t = byId[id];
            var held = t.Holds.Where(h => model.Locks.TryGetValue(h, out var l) && l.InCycle).Select(h => model.Locks[h]).FirstOrDefault();
            var wanted = t.WaitingOnLock is { } w ? model.Locks[w] : null;
            if (held is not null && wanted is not null)
            {
                parts.Add($"{{{{{EntityUri.Thread(id)}}}}} holds {{{{{EntityUri.Lock(held.Address)}}}}} and wants {{{{{EntityUri.Lock(wanted.Address)}}}}}.");
            }
        }

        return string.Join(' ', parts);
    }
}
