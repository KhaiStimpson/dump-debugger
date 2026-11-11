using DumpDebugger.Analysis.Threads;
using DumpDebugger.Core.Contracts;
using static DumpDebugger.Analysis.Findings.Text;

namespace DumpDebugger.Analysis.Findings;

/// <summary>
/// Thread-pool starvation: every pool worker is busy (and almost all of them waiting, not
/// working) while work items queue up. Requests that have nothing to do with the stuck code
/// stall too, because they can't get a thread.
/// </summary>
public sealed class ThreadPoolStarvationAnalyzer : IFindingAnalyzer
{
    public string Name => "threadpool-starvation";

    public AnalyzerStage Stage => AnalyzerStage.Heap;

    public IEnumerable<AnalyzerResult> Analyze(AnalysisContext context)
    {
        var workers = context.Threads.Where(t => t.Role is ThreadRole.PoolWorker or ThreadRole.Request).ToList();
        if (workers.Count < 4)
        {
            yield break;
        }

        var busy = workers.Where(t => t.State != ThreadState.Idle).ToList();
        var waiting = busy.Count(t => t.State is ThreadState.Blocked or ThreadState.Waiting);
        var queued = context.Pool?.QueuedWorkItems ?? 0;
        var max = context.Pool?.MaxThreads;
        var atMax = max is { } m && workers.Count >= m;
        if (busy.Count < workers.Count || waiting < busy.Count * 0.75 || (queued == 0 && !atMax))
        {
            yield break;
        }

        // Where are they stuck? The biggest group of busy workers, by app frame and wait.
        var top = busy.GroupBy(t => (t.WhereName ?? t.Where, t.StateLabel)).OrderByDescending(g => g.Count()).First();
        var topRequest = top.Select(t => t.Request).FirstOrDefault(r => r is not null);
        var severity = queued >= 100 || top.Any(t => t.InDeadlock) ? Severity.Critical : Severity.Warning;

        var title = $"Thread pool starved: all {workers.Count} workers busy" + (queued > 0 ? $", {Plural(queued, "work item")} queued" : "");
        var why = $"{top.Count()} blocked in {Code(top.Key.Item1 ?? "?")} ({top.Key.StateLabel})"
                  + (atMax ? $" · pool at its maximum of {max}" : "");
        var meaning = "Every thread-pool thread is stuck waiting, so new work (including requests that have nothing to do with the stuck code) can't start until one frees up. "
                      + (atMax
                          ? $"The pool is also at its configured maximum ({max}), so it can't add threads at all."
                          : "The pool adds threads only gradually once it notices, which is why the app feels hung rather than slow.");

        var steps = new List<string>
        {
            $"Find what the workers wait on: {Code(top.Key.Item1 ?? "?")} is waiting on {Code(top.Key.StateLabel)}" + (topRequest is null ? "." : $" while serving {Chip(EntityUri.Request(topRequest))}."),
            "Don't block pool threads: make the waiting call async (`await` the I/O, `await semaphore.WaitAsync()`), so the thread returns to the pool while it waits.",
            atMax
                ? $"Check why the pool is capped at {max} (`ThreadPool.SetMaxThreads` or `System.Threading.ThreadPool.MaxThreads` in runtimeconfig)."
                : "As a stopgap, `ThreadPool.SetMinThreads` lets the pool start with more threads. It treats the symptom, not the cause.",
        };

        var evidence = new List<EntityRef>();
        if (topRequest is not null)
        {
            evidence.Add(EntityUri.RequestRef(topRequest, $"{top.Count()} threads"));
        }

        evidence.AddRange(top.Take(4).Select(context.ThreadRef));
        yield return new AnalyzerResult(new Finding(
            $"threadpool.starved", Name, severity, Confidence.High, title, why, meaning, steps,
            [Symptom.Hang, Symptom.SlowCpu], evidence,
            Scores.For(severity, Confidence.High, Math.Min(1, (queued + workers.Count) / 300.0))),
            VerdictHeadline: $"The app is unresponsive: all {workers.Count} thread-pool workers are blocked" + (queued > 0 ? $" and {Plural(queued, "work item")} {(queued == 1 ? "is" : "are")} waiting for a thread." : "."),
            VerdictBody: $"{top.Count()} of them are waiting in {Code(top.Key.Item1 ?? "?")} ({top.Key.StateLabel}). Until they're released, nothing else that needs a pool thread can run.");
    }
}

/// <summary>
/// Sync-over-async: threads blocked on <c>.Result</c>, <c>.Wait()</c> or
/// <c>GetAwaiter().GetResult()</c> of a task, from application code. Each one holds a pool
/// thread hostage while the async work it waits for may itself need a pool thread.
/// </summary>
public sealed class SyncOverAsyncAnalyzer : IFindingAnalyzer
{
    public string Name => "sync-over-async";

    public AnalyzerStage Stage => AnalyzerStage.Threads;

    public IEnumerable<AnalyzerResult> Analyze(AnalysisContext context)
    {
        // A console app's Main blocking on its own top-level task is normal; don't flag it.
        var blocked = context.Threads.Where(t => t.Wait == WaitKind.Task && t.Role != ThreadRole.Main && t.WhereIsApp).ToList();
        foreach (var group in blocked.GroupBy(t => t.WhereName).OrderByDescending(g => g.Count()))
        {
            var n = group.Count();
            var where = group.Key ?? "?";
            var how = group.First().StateLabel;
            var request = group.Select(t => t.Request).FirstOrDefault(r => r is not null);
            var severity = n >= 3 ? Severity.Warning : Severity.Info;
            var evidence = new List<EntityRef>();
            if (request is not null)
            {
                evidence.Add(EntityUri.RequestRef(request, $"{n} threads"));
            }

            evidence.AddRange(group.Take(4).Select(context.ThreadRef));
            yield return new AnalyzerResult(new Finding(
                $"sync-over-async.{where}",
                Name,
                severity,
                Confidence.High,
                $"Sync-over-async: {Plural(n, "thread")} block on {Code(how)} in {Code(where)}",
                (request is null ? "" : $"Serving {Code(request)} · ") + $"each holds a thread-pool thread while it waits",
                "Each thread is parked waiting for an async operation to finish. That operation's continuation usually needs a pool thread too, so under load the waiting threads starve the pool, and with a synchronization context it can deadlock outright.",
                [
                    $"Make the call chain async end to end: {Code("await")} the task in {Code(where)} instead of {Code(how)}, and make its callers async too.",
                    "See what the awaited work is itself waiting for: Threads › Async stacks.",
                    "If it can't be async yet, at least bound how many requests can block like this at once.",
                ],
                [Symptom.Hang, Symptom.SlowCpu],
                evidence,
                Scores.For(severity, Confidence.High, Math.Min(1, n / 100.0))));
        }
    }
}

/// <summary>Many async operations suspended at the same application method, waiting on the same thing.</summary>
public sealed class AsyncStacksAnalyzer : IFindingAnalyzer
{
    private const int MinCount = 20;

    public string Name => "async-stacks";

    public AnalyzerStage Stage => AnalyzerStage.Heap;

    public IEnumerable<AnalyzerResult> Analyze(AnalysisContext context)
    {
        var groups = context.AsyncStacks?.Groups ?? [];
        foreach (var g in groups.Where(g => g.FirstAppFrame is not null && g.Count >= MinCount).Take(3))
        {
            yield return new AnalyzerResult(new Finding(
                $"async.suspended.{g.FirstAppFrame}",
                Name,
                Severity.Info,
                Confidence.Medium,
                $"{Plural(g.Count, "async operation")} are suspended in {Code(g.FirstAppFrame!)}, awaiting {g.Awaiting}",
                $"Logical stack: {string.Join(" ← ", g.Frames.Take(4).Select(Code))}{(g.Frames.Count > 4 ? " ← …" : "")}",
                "These operations aren't on any thread: they're awaiting something and will resume when it completes. Many of them stopped at the same place means whatever they await is slow or never finishes.",
                [
                    $"Find out why {g.Awaiting} doesn't complete: a downstream that doesn't answer, a missing timeout, or a TaskCompletionSource nobody sets.",
                    "Give outbound calls a timeout or CancellationToken so they fail fast instead of piling up.",
                ],
                [Symptom.Hang, Symptom.SlowCpu],
                g.Samples.Take(1).Select(a => EntityUri.ObjectRef(a, g.Frames[0], "innermost async state machine")).ToList(),
                Scores.For(Severity.Info, Confidence.Medium, Math.Min(1, g.Count / 200.0))));
        }
    }
}
