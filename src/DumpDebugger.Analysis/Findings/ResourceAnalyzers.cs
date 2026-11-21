using DumpDebugger.Analysis.Frames;
using DumpDebugger.Analysis.Heap;
using DumpDebugger.Core.Contracts;
using Microsoft.Diagnostics.Runtime;
using static DumpDebugger.Analysis.Findings.Text;

namespace DumpDebugger.Analysis.Findings;

/// <summary>
/// Duplicate strings: the same text held in thousands of separate string objects, which is
/// what a data reader or deserializer produces when a column or property repeats across rows.
/// Strings are grouped by size and a hash of their first characters; on very large heaps a
/// regular sample is read and the totals are scaled, and the finding says so.
/// </summary>
public sealed class DuplicateStringsAnalyzer : IFindingAnalyzer
{
    private const long MinWastedBytes = 16L * 1024 * 1024;
    private const double MinShare = 0.10;
    private const int MaxSampled = 400_000;
    private const int CompareChars = 256;

    public string Name => "duplicate-strings";

    public AnalyzerStage Stage => AnalyzerStage.Heap;

    public IEnumerable<AnalyzerResult> Analyze(AnalysisContext context)
    {
        if (context.Heap is not { } heap || context.Explorer is not { } explorer)
        {
            yield break;
        }

        // Cheap exit: strings can't waste more than they occupy.
        var strings = heap.Types.FirstOrDefault(t => t.Name == "System.String");
        if (strings is null || strings.Size < MinWastedBytes)
        {
            yield break;
        }

        var index = explorer.Index;
        var stringType = index.Types.FindIndex(t => t.IsString);
        if (stringType < 0)
        {
            yield break;
        }

        var clr = context.Runtime.Heap;
        var stride = (int)Math.Max(1, strings.Count / MaxSampled);
        var groups = new Dictionary<(long Size, ulong Hash), (int Count, ulong Sample)>();
        var seen = 0L;
        for (var i = 0; i < index.Count; i++)
        {
            if (index.TypeIds[i] != stringType || seen++ % stride != 0)
            {
                continue;
            }

            var text = clr.GetObject(index.Addresses[i], index.Types[stringType]).AsString(CompareChars);
            if (text is null)
            {
                continue;
            }

            // The sample moves on at each power of two, so it ends up a typical copy from deep in
            // the run rather than the first one, which is often the original (a literal or a constant).
            var key = (index.Sizes[i], Fnv(text));
            groups[key] = groups.TryGetValue(key, out var g)
                ? (g.Count + 1, System.Numerics.BitOperations.IsPow2(g.Count + 1) ? index.Addresses[i] : g.Sample)
                : (1, index.Addresses[i]);
        }

        long wasted = 0;
        var repeated = 0;
        ((long Size, ulong Hash) Key, (int Count, ulong Sample) Value)? top = null;
        long topWaste = 0;
        foreach (var (key, value) in groups)
        {
            if (value.Count < 2)
            {
                continue;
            }

            // One copy is needed; the rest are waste. A sampled count stands for `stride` times as many.
            var waste = (((long)value.Count * stride) - 1) * key.Size;
            wasted += waste;
            repeated++;
            if (waste > topWaste)
            {
                topWaste = waste;
                top = (key, value);
            }
        }

        var share = heap.Overview.TotalBytes == 0 ? 0 : (double)wasted / heap.Overview.TotalBytes;
        if (top is not { } worst || wasted < MinWastedBytes || share < MinShare)
        {
            yield break;
        }

        var copies = (long)worst.Value.Count * stride;
        var sample = clr.GetObject(worst.Value.Sample).AsString(CompareChars) ?? "";
        var holder = FieldHolding(explorer, worst.Value.Sample);
        var severity = share >= 0.25 || wasted >= 256L * 1024 * 1024 ? Severity.Warning : Severity.Info;
        var confidence = stride > 1 ? Confidence.Medium : Confidence.High;
        var approx = stride > 1 ? "about " : "";
        yield return new AnalyzerResult(new Finding(
            "strings.duplicates", Name, severity, confidence,
            $"{approx}{Bytes(wasted)} of duplicate strings ({Percent(share)} of the heap)" + (holder is null ? "" : $", mostly in {Code(holder)}"),
            $"{Count(copies)} copies of \"{Clip(sample, 60)}\" · {Count(repeated)} distinct values repeated"
                + (stride > 1 ? $" · estimated from 1 string in {stride}" : ""),
            "The same text is stored over and over as separate string objects. Readers and deserializers create a new string for every value they read, so a column or property with few distinct values (a status, a category, a description) costs memory per row instead of once.",
            [
                holder is null
                    ? "Share one instance per distinct value where the strings are created: look them up in a small `Dictionary<string, string>` pool as they are read."
                    : $"Share one instance per distinct value where {Code(holder)} is filled: look each value up in a small `Dictionary<string, string>` pool as it is read.",
                "For a fixed set of values, store an enum or an ID instead of the text. `string.Intern` also works, but interned strings are never freed.",
                $"Open {Chip(EntityUri.Object(worst.Value.Sample))} and check its path to a GC root to see which collection holds the copies.",
            ],
            [Symptom.HighMemory],
            [
                EntityUri.ObjectRef(worst.Value.Sample, $"\"{Clip(sample, 40)}\"", $"one of {Count(copies)} copies · {Bytes(worst.Key.Size)} each"),
                EntityUri.TypeRef("System.String", "String", $"{Bytes(strings.Size)} · {Count(strings.Count)} instances"),
            ],
            Scores.For(severity, confidence, share),
            FindingVisual.Heap));
    }

    /// <summary>"ProductRow.Description": the type and field of the object that references <paramref name="address"/> on its shortest root path.</summary>
    internal static string? FieldHolding(ObjectExplorer explorer, ulong address)
    {
        var path = explorer.FindRoots(new RootsQuery(address, RootPathMode.Shortest), CancellationToken.None).Paths.FirstOrDefault();
        if (path is null)
        {
            return null;
        }

        if (path.Steps.Count >= 2 && path.Steps[^2] is { } owner && FrameClassifier.IsAppType(owner.TypeName, null))
        {
            var type = FrameClassifier.ShortTypeName(owner.TypeName);
            return owner.Via is { Length: > 0 } via && !via.StartsWith('[') ? $"{type}.{Locks.LockModelBuilder.CleanFieldName(via)}" : type;
        }

        return path.Root.Kind == RootKind.Static ? path.Root.Label.Replace("static ", "") : null;
    }

    private static ulong Fnv(string s)
    {
        var hash = 14695981039346656037UL;
        foreach (var c in s)
        {
            hash = (hash ^ c) * 1099511628211UL;
        }

        return hash;
    }

    internal static string Clip(string s, int n)
    {
        s = s.ReplaceLineEndings(" ");
        return s.Length > n ? s[..(n - 1)] + "…" : s;
    }
}

/// <summary>
/// Event-handler leaks: one event (a multicast delegate) with hundreds or thousands of
/// subscribers. The event keeps every subscriber alive, so objects that subscribe and never
/// unsubscribe pile up for as long as the publisher lives.
/// </summary>
public sealed class EventHandlerLeakAnalyzer : IFindingAnalyzer
{
    private const int MinHandlers = 500;
    private const int WarnHandlers = 2000;
    private const int SampledHandlers = 256;

    public string Name => "event-handlers";

    public AnalyzerStage Stage => AnalyzerStage.Heap;

    public IEnumerable<AnalyzerResult> Analyze(AnalysisContext context)
    {
        if (context.Explorer is not { } explorer)
        {
            yield break;
        }

        var index = explorer.Index;
        var isDelegate = index.Types.Select(IsDelegateType).ToArray();
        if (!isDelegate.Any(d => d))
        {
            yield break;
        }

        var clr = context.Runtime.Heap;
        var events = new List<(ulong Address, int Handlers, ClrObject List)>();
        for (var i = 0; i < index.Count; i++)
        {
            if (!isDelegate[index.TypeIds[i]])
            {
                continue;
            }

            // _invocationCount means other things on single-target delegates (it can hold a
            // pointer), so only trust it next to an invocation list at least that long.
            var obj = clr.GetObject(index.Addresses[i], index.Types[index.TypeIds[i]]);
            if (!obj.TryReadField<nint>("_invocationCount", out var count) || count < MinHandlers || count > int.MaxValue
                || !obj.TryReadObjectField("_invocationList", out var list) || !list.IsValid || !list.IsArray || list.AsArray().Length < count)
            {
                continue;
            }

            events.Add((obj.Address, (int)count, list));
        }

        foreach (var (address, handlers, list) in events.OrderByDescending(e => e.Handlers).Take(3))
        {
            var methods = new Dictionary<string, int>();
            var targetTypes = new Dictionary<string, int>();
            ulong sampleTarget = 0;
            var array = list.AsArray();
            for (var i = 0; i < Math.Min(handlers, SampledHandlers); i++)
            {
                var handler = array.GetObjectValue(i);
                if (!handler.IsValid || !handler.IsDelegate)
                {
                    continue;
                }

                if (handler.AsDelegate().GetDelegateTarget()?.Method is { } method)
                {
                    var name = FrameClassifier.DisplayName(method);
                    methods[name] = methods.GetValueOrDefault(name) + 1;
                }

                if (handler.TryReadObjectField("_target", out var target) && target.IsValid && target.Type?.Name is { } typeName && !target.IsDelegate)
                {
                    targetTypes[typeName] = targetTypes.GetValueOrDefault(typeName) + 1;
                    if (sampleTarget == 0)
                    {
                        sampleTarget = target.Address;
                    }
                }
            }

            var topMethod = methods.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault();
            var topTarget = targetTypes.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault();
            var owner = DuplicateStringsAnalyzer.FieldHolding(explorer, address);
            var what = owner is null ? "one event" : Code(owner);
            var severity = handlers >= WarnHandlers ? Severity.Warning : Severity.Info;
            var evidence = new List<EntityRef> { EntityUri.ObjectRef(address, owner ?? "event delegate", $"{Count(handlers)} handlers") };
            if (sampleTarget != 0 && topTarget is not null)
            {
                evidence.Add(EntityUri.ObjectRef(sampleTarget, FrameClassifier.ShortTypeName(topTarget), "one of the subscribers"));
                evidence.Add(EntityUri.TypeRef(topTarget, FrameClassifier.ShortTypeName(topTarget), "subscriber type"));
            }

            yield return new AnalyzerResult(new Finding(
                $"events.subscribers.{owner ?? address.ToString("x")}", Name, severity, Confidence.High,
                $"{Count(handlers)} handlers are subscribed to {what}" + (topMethod is null ? "" : $", mostly {Code(topMethod)}"),
                (topTarget is null ? "" : $"Subscribers are {Code(FrameClassifier.ShortTypeName(topTarget))} objects · ") + "each one is kept alive by the event",
                "An event holds a reference to every subscriber, so a subscriber can't be collected until it unsubscribes or the publisher goes away. Objects that subscribe in their constructor and never unsubscribe pile up, and every time the event is raised all of them run, one after another.",
                [
                    topTarget is null
                        ? "Unsubscribe (`-=`) when a subscriber is finished, typically in `Dispose`."
                        : $"Unsubscribe (`-=`) when a {Code(FrameClassifier.ShortTypeName(topTarget))} is finished, typically in `Dispose`, and make sure it is disposed.",
                    "If subscribers can't know when they are done, use a weak-event pattern, or have one long-lived object subscribe and route to the current ones.",
                    sampleTarget == 0 ? "Open the event delegate and look at its invocation list." : $"Open {Chip(EntityUri.Object(sampleTarget))} to see what each subscriber keeps alive.",
                ],
                [Symptom.HighMemory, Symptom.SlowCpu],
                evidence,
                Scores.For(severity, Confidence.High, Math.Min(1, handlers / 20_000.0))));
        }
    }

    private static bool IsDelegateType(ClrType type)
    {
        for (var t = type.BaseType; t is not null; t = t.BaseType)
        {
            if (t.Name == "System.MulticastDelegate")
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// GC handles: thousands of pinned objects (which the GC can't move, so the heap fragments
/// around them) or a strong-handle count that only grows (<c>GCHandle.Alloc</c> without
/// <c>Free</c>).
/// </summary>
public sealed class GcHandleAnalyzer : IFindingAnalyzer
{
    private const int MinPinned = 500;
    private const int WarnPinned = 1000;
    private const int MinStrong = 10_000;

    public string Name => "gc-handles";

    public AnalyzerStage Stage => AnalyzerStage.Heap;

    public IEnumerable<AnalyzerResult> Analyze(AnalysisContext context)
    {
        var index = context.Explorer?.Index;
        var pinned = new Dictionary<string, (int Count, long Bytes, ulong Sample)>();
        var strong = new Dictionary<string, (int Count, ulong Sample)>();
        var pinnedTotal = 0;
        long pinnedBytes = 0;
        var pinnedMovable = 0;
        var strongTotal = 0;
        foreach (var handle in context.Runtime.EnumerateHandles())
        {
            var obj = handle.Object;
            if (!obj.IsValid || obj.Type?.Name is not { } typeName)
            {
                continue;
            }

            if (handle.HandleKind is ClrHandleKind.Pinned or ClrHandleKind.AsyncPinned)
            {
                // The runtime pins a few object[] of its own (statics, the handle table): not the app's.
                if (typeName == "System.Object[]")
                {
                    continue;
                }

                var size = (long)obj.Size;
                pinnedTotal++;
                pinnedBytes += size;
                var current = pinned.GetValueOrDefault(typeName, (0, 0L, obj.Address));
                pinned[typeName] = (current.Item1 + 1, current.Item2 + size, current.Item3);

                // Pinning only hurts where the GC compacts: generations 0-2, not the large or pinned object heaps.
                if (index is not null && index.IndexOf(obj.Address) is var i and >= 0 && index.Generations[i] <= 2)
                {
                    pinnedMovable++;
                }
            }
            else if (handle.HandleKind == ClrHandleKind.Strong)
            {
                strongTotal++;
                var current = strong.GetValueOrDefault(typeName, (0, obj.Address));
                strong[typeName] = (current.Item1 + 1, current.Item2);
            }
        }

        if (pinnedTotal >= MinPinned)
        {
            var top = pinned.OrderByDescending(kv => kv.Value.Count).First();
            var holder = context.Explorer is { } explorer ? StaticHolder(explorer, top.Value.Sample) : null;
            var severity = pinnedTotal >= WarnPinned ? Severity.Warning : Severity.Info;
            var shortName = FrameClassifier.ShortTypeName(top.Key);
            yield return new AnalyzerResult(new Finding(
                "gc.pinned", Name, severity, Confidence.High,
                $"{Count(pinnedTotal)} objects are pinned, mostly {Code(shortName)}" + (holder is null ? "" : $" held by {Code(holder)}"),
                $"{Bytes(pinnedBytes)} pinned in total" + (index is null ? "" : $" · {Count(pinnedMovable)} of them in the compacting generations (0-2)"),
                "A pinned object can't be moved by the garbage collector. Thousands of them scattered through the ordinary heap stop the GC compacting around them, so free space is stranded between them and the process grows even though little is live. A pinning handle that is never freed also keeps its object alive forever.",
                [
                    holder is null
                        ? "Free every `GCHandle` you allocate (`handle.Free()` in a `finally` or in `Dispose`)."
                        : $"Free the handles created for {Code(holder)}: call `handle.Free()` in a `finally` or in `Dispose` when a buffer is returned.",
                    "For buffers that must stay pinned for a long time, allocate them on the Pinned Object Heap (`GC.AllocateArray<byte>(size, pinned: true)`), where pinning costs nothing.",
                    "For a short native call, pin with `fixed` or pass a `Span<T>` instead of holding a handle.",
                ],
                [Symptom.HighMemory],
                [
                    EntityUri.ObjectRef(top.Value.Sample, shortName, $"one of {Count(top.Value.Count)} pinned"),
                    EntityUri.TypeRef(top.Key, shortName, $"{Count(top.Value.Count)} pinned · {Bytes(top.Value.Bytes)}"),
                ],
                Scores.For(severity, Confidence.High, Math.Min(1, pinnedTotal / 10_000.0)),
                FindingVisual.Heap));
        }

        if (strongTotal >= MinStrong)
        {
            var top = strong.OrderByDescending(kv => kv.Value.Count).First();
            var shortName = FrameClassifier.ShortTypeName(top.Key);
            yield return new AnalyzerResult(new Finding(
                "gc.strong-handles", Name, Severity.Warning, Confidence.Medium,
                $"{Count(strongTotal)} strong GC handles are open, mostly on {Code(shortName)}",
                $"{Count(top.Value.Count)} × {shortName}",
                "A strong GC handle keeps its object alive until the handle is freed, whatever else stops referencing it. A count this high usually means `GCHandle.Alloc` (often for a callback or state passed to native code) without a matching `Free`.",
                [
                    "Find where the handles are allocated and free each one when the native side is done with it.",
                    $"Open {Chip(EntityUri.Object(top.Value.Sample))}: its GC-root path shows the handle as the root.",
                    "Prefer `SafeHandle`, or a single long-lived handle to a dispatcher object, over one handle per item.",
                ],
                [Symptom.HighMemory],
                [
                    EntityUri.ObjectRef(top.Value.Sample, shortName, $"one of {Count(top.Value.Count)}"),
                    EntityUri.TypeRef(top.Key, shortName, $"{Count(top.Value.Count)} strong handles"),
                ],
                Scores.For(Severity.Warning, Confidence.Medium, Math.Min(1, strongTotal / 100_000.0))));
        }
    }

    /// <summary>The static field one of the object's root paths starts from (the handle itself is always a root, so look past it).</summary>
    private static string? StaticHolder(ObjectExplorer explorer, ulong address) =>
        explorer.FindRoots(new RootsQuery(address, RootPathMode.All, 8), CancellationToken.None).Paths
            .FirstOrDefault(p => p.Root.Kind == RootKind.Static)?.Root.Label.Replace("static ", "");
}

/// <summary>
/// Too many dedicated threads: dozens or hundreds of non-pool threads parked at the same
/// application method, which is what a thread per connection, device or job looks like.
/// </summary>
public sealed class ThreadCountAnalyzer : IFindingAnalyzer
{
    private const int MinThreads = 50;
    private const int WarnThreads = 100;

    public string Name => "thread-count";

    public AnalyzerStage Stage => AnalyzerStage.Threads;

    public IEnumerable<AnalyzerResult> Analyze(AnalysisContext context)
    {
        var dedicated = context.Threads.Where(t => t.Role == ThreadRole.Background && t.WhereIsApp).ToList();
        foreach (var group in dedicated.GroupBy(t => t.WhereName ?? t.Where ?? "?").Where(g => g.Count() >= MinThreads).OrderByDescending(g => g.Count()).Take(2))
        {
            var n = group.Count();
            var where = group.Key;
            var state = group.GroupBy(t => t.StateLabel).OrderByDescending(g => g.Count()).First().Key;
            var parked = group.Count(t => t.State is ThreadState.Blocked or ThreadState.Waiting);
            var severity = n >= WarnThreads ? Severity.Warning : Severity.Info;
            yield return new AnalyzerResult(new Finding(
                $"threads.dedicated.{where}", Name, severity, Confidence.High,
                $"{Plural(n, "dedicated thread")} are in {Code(where)}" + (parked >= n * 0.75 ? $", almost all waiting ({state})" : ""),
                $"{Plural(context.Threads.Count, "thread")} in the process · {n} started by the app for the same job",
                "Every thread reserves its own stack (1 MB by default) and has to be scheduled by the operating system, whether it's working or waiting. Hundreds of threads parked at the same line usually mean a thread per connection, device or job: it works at small scale, then memory and context switching climb with every one added.",
                [
                    $"Make the wait in {Code(where)} asynchronous (`await channel.Reader.ReadAsync()`, `await semaphore.WaitAsync()`, async I/O), so waiting doesn't hold a thread.",
                    "If the work is CPU-bound, use a small fixed set of workers reading from a shared queue (`Channel<T>`, `Parallel.ForEachAsync`) instead of a thread per item.",
                    "Open one of the threads and read the bottom of its stack to find where they are started.",
                ],
                [Symptom.SlowCpu, Symptom.HighMemory],
                group.Take(4).Select(context.ThreadRef).ToList(),
                Scores.For(severity, Confidence.High, Math.Min(1, n / 500.0))));
        }
    }
}
