using DumpDebugger.Analysis.Frames;
using DumpDebugger.Analysis.Heap;
using DumpDebugger.Analysis.Threads;
using DumpDebugger.Core.Contracts;
using Microsoft.Diagnostics.Runtime;
using static DumpDebugger.Analysis.Findings.Text;

namespace DumpDebugger.Analysis.Findings;

/// <summary>
/// Exceptions: the crash itself (from the minidump's exception stream), exceptions in flight on
/// threads (thrown and still being handled, e.g. stuck in a catch block), and exception storms
/// (thousands of exception objects alive, usually kept by a log or list).
/// </summary>
public sealed class ExceptionsAnalyzer : IFindingAnalyzer
{
    // The runtime preallocates one of each of these at startup; they're not the app's.
    private static readonly HashSet<string> Preallocated = ["System.OutOfMemoryException", "System.StackOverflowException", "System.ExecutionEngineException", "System.Threading.ThreadAbortException"];

    public string Name => "exceptions";

    public AnalyzerStage Stage => AnalyzerStage.Heap;

    public IEnumerable<AnalyzerResult> Analyze(AnalysisContext context)
    {
        // A minidump exception stream names the crashing thread exactly. Dumps written by the
        // runtime's own crash dumper (createdump) don't carry one; there the crashing thread is
        // the one caught mid-dispatch with no handler run yet, which is strong but not proof.
        var crashOs = context.Metadata.CrashThreadOsId;
        var crashThread = crashOs is { } os ? context.Threads.FirstOrDefault(t => t.OsId == os) : null;
        var confidence = Confidence.High;
        if (crashThread is null && context.Threads.Count(t => t.ExceptionDispatching) == 1)
        {
            crashThread = context.Threads.Single(t => t.ExceptionDispatching);
            confidence = Confidence.Medium;
        }

        if (crashThread?.Exception is { } crashType)
        {
            var msg = crashThread.ExceptionMessage;
            var crashed = confidence == Confidence.High ? "The process crashed" : "The process was crashing";
            yield return new AnalyzerResult(new Finding(
                "exceptions.crash", Name, Severity.Critical, confidence,
                $"{crashed}: unhandled {Code(FrameClassifier.ShortTypeName(crashType))}" + (crashThread.ExceptionThrownIn is { } at ? $" in {Code(at)}" : ""),
                msg is null ? $"On thread #{crashThread.Id}" : $"\"{Clip(msg, 140)}\" · thread #{crashThread.Id}",
                "An exception reached the top of a thread without being caught, so the runtime terminated the process. This dump was written as it went down; the thread below is where it happened.",
                [
                    $"Open {Chip(EntityUri.Thread(crashThread.Id))} and read the stack: the exception was thrown at the first application frame.",
                    "Handle the failure where it's thrown, or catch it at the thread's entry point (background threads and fire-and-forget tasks need their own try/catch).",
                ],
                [Symptom.Crash],
                [context.ThreadRef(crashThread)],
                Scores.For(Severity.Critical, confidence, 1)),
                VerdictHeadline: $"{crashed} with an unhandled {FrameClassifier.ShortTypeName(crashType)}" + (crashThread.ExceptionThrownIn is { } where ? $" thrown in {where}." : "."),
                VerdictBody: msg is null ? $"It was thrown on {Chip(EntityUri.Thread(crashThread.Id))}." : $"\"{Clip(msg, 200)}\" on {Chip(EntityUri.Thread(crashThread.Id))}.");
        }

        foreach (var group in context.Threads.Where(t => t.Exception is not null && t != crashThread)
                     .GroupBy(t => (t.Exception, t.WhereName)).OrderByDescending(g => g.Count()))
        {
            var n = group.Count();
            var first = group.First();
            var stuck = group.All(t => t.State is ThreadState.Blocked or ThreadState.Waiting);
            var severity = stuck && n >= 3 ? Severity.Warning : Severity.Info;
            var steps = new List<string>();
            if (stuck)
            {
                steps.Add($"These threads are waiting inside the handler ({Code(first.WhereName ?? "?")}): bound the retry or back-off (attempts and total time) and fail the request instead of waiting forever.");
            }

            if (first.ExceptionThrownIn is { } thrower)
            {
                steps.Add($"Fix the cause: {Code(FrameClassifier.ShortTypeName(first.Exception))} is thrown in {Code(thrower)}.");
            }

            steps.Add("Open a thread to see the full stack and the exception's message.");
            yield return new AnalyzerResult(new Finding(
                $"exceptions.inflight.{first.Exception}.{first.WhereName}", Name, severity, Confidence.High,
                $"{Code(FrameClassifier.ShortTypeName(first.Exception))} in flight on {Plural(n, "thread")}" + (first.WhereName is { } w ? $", in {Code(w)}" : ""),
                (first.ExceptionMessage is { } m ? $"\"{Clip(m, 120)}\"" : "No message") + (stuck ? $" · {first.StateLabel.ToLowerInvariant()}" : ""),
                stuck
                    ? "Each of these threads caught an exception and is now waiting inside the handling code. Whatever it's waiting for keeps the request open; retry or back-off logic that never gives up looks exactly like this."
                    : "These threads are in the middle of handling an exception. That's normal on its own; it matters if the same exception keeps recurring or the handler is slow.",
                steps,
                [Symptom.Crash, Symptom.Hang],
                group.Take(5).Select(context.ThreadRef).ToList(),
                Scores.For(severity, Confidence.High, Math.Min(1, n / 20.0))));
        }

        // Exception storm: many exception objects alive on the heap.
        var heap = context.Heap;
        if (heap is null || context.Explorer is null)
        {
            yield break;
        }

        var exceptionTypes = heap.Types.Where(t => !Preallocated.Contains(t.Name) && IsExceptionType(context.Runtime, t)).ToList();
        var total = exceptionTypes.Sum(t => t.Count);
        if (total < 500)
        {
            yield break;
        }

        var top = exceptionTypes.OrderByDescending(t => t.Count).First();
        var sample = SampleOf(context.Explorer.Index, top.Name);
        string? message = null;
        string? holder = null;
        if (sample >= 0)
        {
            var obj = context.Runtime.Heap.GetObject(context.Explorer.Index.Addresses[sample]);
            message = obj.ReadStringField("_message");
            holder = context.Explorer.FindRoots(new RootsQuery(obj.Address, RootPathMode.Shortest), CancellationToken.None).Paths.FirstOrDefault()?.Root.Label;
        }

        yield return new AnalyzerResult(new Finding(
            "exceptions.storm", Name, Severity.Info, Confidence.High,
            $"{Count(total)} exception objects are alive, mostly {Code(FrameClassifier.ShortTypeName(top.Name))}" + (holder is null ? "" : $" kept by {Code(holder.Replace("static ", ""))}"),
            (message is null ? "" : $"e.g. \"{Clip(message, 100)}\" · ") + $"{Count(top.Count)} × {FrameClassifier.ShortTypeName(top.Name)}",
            "Exceptions are expensive to throw (stack capture) and, kept in a list or log, they keep their stack traces and everything they reference alive. Thousands of them usually mean an exception used for control flow, or an error log with no cap.",
            [
                holder is null ? "Find what keeps them alive: open one and check its path to a GC root." : $"Cap or flush {Code(holder.Replace("static ", ""))}: keep the last N, or log and drop the object.",
                "If the same exception is thrown on a hot path, check the condition up front instead of throwing.",
            ],
            [Symptom.HighMemory, Symptom.SlowCpu],
            [EntityUri.TypeRef(top.Name, FrameClassifier.ShortTypeName(top.Name), $"{Count(top.Count)} instances")],
            Scores.For(Severity.Info, Confidence.High, Math.Min(1, total / 10_000.0))));
    }

    private static bool IsExceptionType(ClrRuntime runtime, TypeAggregate t) =>
        runtime.Heap.GetTypeByMethodTable(t.MethodTable)?.IsException == true;

    internal static int SampleOf(HeapIndex index, string typeName)
    {
        for (var i = 0; i < index.Count; i++)
        {
            if (index.TypeOf(i).Name == typeName)
            {
                return i;
            }
        }

        return -1;
    }

    private static string Clip(string s, int n) => s.Length > n ? s[..(n - 1)] + "…" : s;
}

/// <summary>
/// Finalizer backlog: objects queued for finalization (f-reachable) pile up, usually because
/// the single finalizer thread is blocked inside a finalizer.
/// </summary>
public sealed class FinalizerAnalyzer : IFindingAnalyzer
{
    public string Name => "finalizer";

    public AnalyzerStage Stage => AnalyzerStage.Heap;

    public IEnumerable<AnalyzerResult> Analyze(AnalysisContext context)
    {
        var queue = new Dictionary<string, int>();
        var total = 0;
        foreach (var root in context.Runtime.Heap.EnumerateFinalizerRoots())
        {
            var name = root.Object.Type?.Name ?? "?";
            queue[name] = queue.GetValueOrDefault(name) + 1;
            total++;
        }

        var finalizer = context.Threads.FirstOrDefault(t => t.Role == ThreadRole.Finalizer);
        var blocked = finalizer is { State: ThreadState.Blocked or ThreadState.Waiting } && finalizer.WhereIsApp;
        if (!(blocked && total > 0) && total < 10_000)
        {
            yield break;
        }

        var top = queue.OrderByDescending(kv => kv.Value).First();
        var lockRef = finalizer?.WaitingOnLock is { } l && context.Locks.Locks.TryGetValue(l, out var lk) ? lk.Ref : null;
        var why = blocked
            ? $"Finalizer thread stuck in {Code(finalizer!.WhereName ?? "?")}" + (lockRef is not null ? $" waiting for {Chip(lockRef.Uri)}" + (finalizer.WaitingOnThread is { } o ? $" held by {Chip(EntityUri.Thread(o))}" : "") : $" ({finalizer.StateLabel.ToLowerInvariant()})")
            : "The finalizer thread is running but can't keep up";
        var evidence = new List<EntityRef>();
        if (finalizer is not null)
        {
            evidence.Add(context.ThreadRef(finalizer));
        }

        if (lockRef is not null)
        {
            evidence.Add(lockRef);
        }

        if (finalizer?.WaitingOnThread is { } owner && context.ThreadsById.TryGetValue(owner, out var ot))
        {
            evidence.Add(context.ThreadRef(ot));
        }

        evidence.Add(EntityUri.TypeRef(top.Key, FrameClassifier.ShortTypeName(top.Key), $"{Count(top.Value)} waiting"));
        var severity = blocked ? Severity.Critical : Severity.Warning;
        yield return new AnalyzerResult(new Finding(
            "finalizer.backlog", Name, severity, Confidence.High,
            blocked
                ? $"Finalizer thread is blocked; {Count(total)} objects are waiting to be finalized"
                : $"{Count(total)} objects are waiting to be finalized",
            why + $" · mostly {Code(FrameClassifier.ShortTypeName(top.Key))} ({Count(top.Value)})",
            "One thread runs every finalizer, one at a time. While it's stuck, nothing that needs finalizing is released, so memory and any native handles those objects own keep growing until the process runs out.",
            [
                blocked ? $"Never block in a finalizer: remove the lock or I/O from {Code(finalizer!.WhereName ?? "the finalizer")}; wrap native resources in a {Code("SafeHandle")} instead." : "Make finalizers trivial and fast, or remove them.",
                $"Dispose {Code(FrameClassifier.ShortTypeName(top.Key))} deterministically ({Code("using")}) and call {Code("GC.SuppressFinalize")}, so the finalizer has nothing to do.",
                finalizer?.WaitingOnThread is { } holder ? $"See why {Chip(EntityUri.Thread(holder))} holds the lock and never releases it." : "Check what the finalizer thread's stack is doing.",
            ],
            [Symptom.HighMemory, Symptom.Hang],
            evidence,
            Scores.For(severity, Confidence.High, Math.Min(1, total / 50_000.0)),
            FindingVisual.WaitGraph));
    }
}

/// <summary>LOH fragmentation: lots of free space between live large objects that the GC won't compact by default.</summary>
public sealed class LohFragmentationAnalyzer : IFindingAnalyzer
{
    private const long MinLohBytes = 64L * 1024 * 1024;

    public string Name => "loh-fragmentation";

    public AnalyzerStage Stage => AnalyzerStage.Heap;

    public IEnumerable<AnalyzerResult> Analyze(AnalysisContext context)
    {
        if (context.Heap is not { } heap)
        {
            yield break;
        }

        var total = heap.LohBytes + heap.LohFreeBytes;
        var share = total == 0 ? 0 : (double)heap.LohFreeBytes / total;
        if (total < MinLohBytes || share < 0.25)
        {
            yield break;
        }

        var severity = share >= 0.4 ? Severity.Warning : Severity.Info;
        var topTypes = heap.Types.Where(t => t.LohSize > 0).OrderByDescending(t => t.LohSize).Take(3).ToList();
        yield return new AnalyzerResult(new Finding(
            "loh.fragmentation", Name, severity, Confidence.High,
            $"Large Object Heap is {Percent(share)} free space (fragmented)",
            $"{Bytes(heap.LohFreeBytes)} free between {Bytes(heap.LohBytes)} of live large objects",
            "Objects of 85 KB or more go on the Large Object Heap, which the GC doesn't compact by default. When large buffers are allocated and dropped in mixed order, the gaps they leave are often too small or badly placed for the next allocation, so the process holds memory it isn't using.",
            [
                "Reuse large buffers instead of allocating new ones: `ArrayPool<byte>.Shared`, or `RecyclableMemoryStream` for streams.",
                "Keep buffers under 85 KB where you can (chunk large payloads).",
                "As a stopgap, set `GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce` before a full GC to compact it once.",
            ],
            [Symptom.HighMemory],
            topTypes.Select(t => EntityUri.TypeRef(t.Name, FrameClassifier.ShortTypeName(t.Name), $"{Bytes(t.LohSize)} on the LOH")).ToList(),
            Scores.For(severity, Confidence.High, share),
            FindingVisual.Heap));
    }
}

/// <summary>HttpClient/socket exhaustion: a new HttpClient (handler, connection pool, sockets) per call instead of a shared one.</summary>
public sealed class HttpClientAnalyzer : IFindingAnalyzer
{
    public string Name => "http-sockets";

    public AnalyzerStage Stage => AnalyzerStage.Heap;

    public IEnumerable<AnalyzerResult> Analyze(AnalysisContext context)
    {
        if (context.Heap is not { } heap)
        {
            yield break;
        }

        long CountOf(params string[] names) => heap.Types.Where(t => names.Contains(t.Name)).Sum(t => t.Count);
        var clients = CountOf("System.Net.Http.HttpClient");
        var handlers = CountOf("System.Net.Http.HttpClientHandler", "System.Net.Http.SocketsHttpHandler");
        var sockets = CountOf("System.Net.Sockets.Socket");
        // Sockets alone aren't a finding: a busy server has plenty. Many clients is the bug.
        if (clients < 50)
        {
            yield break;
        }

        var creator = context.Explorer is { } ex ? Creator(context.Runtime, ex, "System.Net.Http.HttpClient") : null;
        var severity = clients >= 50 ? Severity.Warning : Severity.Info;
        yield return new AnalyzerResult(new Finding(
            "http.client-per-call", Name, severity, Confidence.Medium,
            clients >= 50
                ? $"{Count(clients)} {Code("HttpClient")} instances and {Count(sockets)} sockets: a new client per call" + (creator is null ? "" : $" in {Code(creator)}")
                : $"{Count(sockets)} sockets are open",
            $"{Count(handlers)} handlers, each with its own connection pool",
            "Each HttpClient gets its own handler and connection pool, so connections aren't shared or reused. Every call pays for a new TCP (and TLS) handshake, and under load the machine runs out of outbound ports (\"Only one usage of each socket address is normally permitted\").",
            [
                "Create one `HttpClient` and reuse it for the life of the app, or use `IHttpClientFactory`.",
                "If you need DNS changes to be picked up, set `SocketsHttpHandler.PooledConnectionLifetime` on the shared handler instead of recreating clients.",
                creator is null ? "Find where clients are created: open an HttpClient instance and check what references it." : $"Start in {Code(creator)}, which creates them.",
            ],
            [Symptom.SlowCpu, Symptom.Hang],
            [EntityUri.TypeRef("System.Net.Http.HttpClient", "HttpClient", $"{Count(clients)} instances"), EntityUri.TypeRef("System.Net.Sockets.Socket", "Socket", $"{Count(sockets)} instances")],
            Scores.For(severity, Confidence.Medium, Math.Min(1, clients / 300.0))));
    }

    /// <summary>The app method most instances are held by: an async method's state machine or an app object's field.</summary>
    internal static string? Creator(ClrRuntime runtime, ObjectExplorer explorer, string typeName)
    {
        var index = explorer.Index;
        var votes = new Dictionary<string, int>();
        var seen = 0;
        for (var i = 0; i < index.Count && seen < 40; i++)
        {
            if (index.TypeOf(i).Name != typeName)
            {
                continue;
            }

            seen++;
            foreach (var s in index.ReferrersOf(i))
            {
                var t = index.TypeOf(s);
                string? name = null;
                if (t.Name?.Contains("StateMachineBox<", StringComparison.Ordinal) == true)
                {
                    var box = runtime.Heap.GetObject(index.Addresses[s]);
                    var sm = AsyncStackWalker.StateMachineType(box);
                    if (sm is not null && FrameClassifier.IsAppType(sm.Name, sm.Module))
                    {
                        name = AsyncStackWalker.MethodOf(box);
                    }
                }
                else if (FrameClassifier.IsAppType(t.Name, t.Module))
                {
                    // A Debug-build async state machine is a class nested in its method's type.
                    name = System.Text.RegularExpressions.Regex.IsMatch(t.Name ?? "", @"\+<.+>d(__\d+)?$")
                        ? FrameClassifier.AsyncMethodName(t.Name!)
                        : FrameClassifier.ShortTypeName(t.Name);
                }

                if (name is not null)
                {
                    votes[name] = votes.GetValueOrDefault(name) + 1;
                }
            }
        }

        return votes.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault();
    }
}

/// <summary>Timer leaks: thousands of System.Threading timers registered, grouped by the method they'll call.</summary>
public sealed class TimerLeakAnalyzer : IFindingAnalyzer
{
    private const int MinTimers = 1000;

    public string Name => "timers";

    public AnalyzerStage Stage => AnalyzerStage.Heap;

    public IEnumerable<AnalyzerResult> Analyze(AnalysisContext context)
    {
        if (context.Explorer is not { } explorer)
        {
            yield break;
        }

        var index = explorer.Index;
        var timerType = index.Types.FindIndex(t => t.Name == "System.Threading.TimerQueueTimer");
        if (timerType < 0)
        {
            yield break;
        }

        var byCallback = new Dictionary<string, (int Count, ulong Sample, long DueMs)>();
        var total = 0;
        for (var i = 0; i < index.Count; i++)
        {
            if (index.TypeIds[i] != timerType)
            {
                continue;
            }

            var timer = context.Runtime.Heap.GetObject(index.Addresses[i]);
            if (timer.TryReadField<bool>("_canceled", out var canceled) && canceled)
            {
                continue;
            }

            total++;
            var callback = "?";
            if (timer.TryReadObjectField("_timerCallback", out var cb) && cb.IsDelegate)
            {
                var method = cb.AsDelegate().GetDelegateTarget()?.Method;
                if (method is not null)
                {
                    callback = FrameClassifier.DisplayName(method);
                }
            }

            var due = timer.TryReadField<uint>("_dueTime", out var d) ? d : 0;
            var current = byCallback.GetValueOrDefault(callback, (0, timer.Address, due));
            byCallback[callback] = (current.Count + 1, current.Sample, current.DueMs);
        }

        if (total < MinTimers)
        {
            yield break;
        }

        var top = byCallback.OrderByDescending(kv => kv.Value.Count).First();
        var holder = explorer.FindRoots(new RootsQuery(top.Value.Sample, RootPathMode.Shortest), CancellationToken.None).Paths.FirstOrDefault();
        var owner = holder?.Steps.Select(s => s.TypeName).FirstOrDefault(n => FrameClassifier.IsAppType(n, null) && !n.Contains("<>c", StringComparison.Ordinal));
        yield return new AnalyzerResult(new Finding(
            "timers.leak", Name, Severity.Warning, Confidence.High,
            $"{Count(top.Value.Count)} timers waiting to call {Code(top.Key)}" + (owner is null ? "" : $", kept by {Code(FrameClassifier.ShortTypeName(owner))}"),
            $"{Count(total)} timers registered in total" + (top.Value.DueMs > 0 && top.Value.DueMs != uint.MaxValue ? $" · next due in {TimeSpan.FromMilliseconds(top.Value.DueMs):g}" : ""),
            "Each `System.Threading.Timer` stays registered, and keeps its callback, its state and everything they reference alive, until it's disposed. A timer created per session or per request and stored somewhere piles up, and so does everything it captures.",
            [
                owner is null ? "Dispose timers you no longer need." : $"Dispose (and remove) the timer when its entry goes away in {Code(FrameClassifier.ShortTypeName(owner))}.",
                "Use one timer that sweeps all entries instead of one timer per entry.",
                "For cache expiry, `MemoryCache` with an expiration does this without a timer per item.",
            ],
            [Symptom.HighMemory, Symptom.SlowCpu],
            [EntityUri.ObjectRef(top.Value.Sample, "TimerQueueTimer", $"one of {Count(top.Value.Count)}"), EntityUri.TypeRef("System.Threading.TimerQueueTimer", "TimerQueueTimer", $"{Count(total)} instances")],
            Scores.For(Severity.Warning, Confidence.High, Math.Min(1, total / 20_000.0))));
    }
}
