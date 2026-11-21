using DumpDebugger.Analysis.Frames;
using DumpDebugger.Analysis.Heap;
using DumpDebugger.Analysis.Threads;
using DumpDebugger.Core.Contracts;
using DumpDebugger.Core.Dump;
using Microsoft.Diagnostics.Runtime;
using static DumpDebugger.Analysis.Findings.Text;

namespace DumpDebugger.Analysis.Findings;

/// <summary>
/// Hangs that no thread shows, because an awaiting method doesn't hold one: async operations
/// queued on a <c>SemaphoreSlim</c> that has no free slot (including the method that holds it
/// and awaits it again), and operations awaiting a <c>TaskCompletionSource</c> task whose
/// source nothing references any more, so nothing can ever complete it.
/// </summary>
public sealed class AsyncLockAnalyzer : IFindingAnalyzer
{
    private const int MaxWaiters = 10_000;
    private const int WarnWaiters = 3;
    private const int MaxChain = 40;
    private const int MaxOrphanGroups = 20;

    public string Name => "async-locks";

    public AnalyzerStage Stage => AnalyzerStage.Heap;

    public IEnumerable<AnalyzerResult> Analyze(AnalysisContext context)
    {
        if (context.Explorer is not { } explorer)
        {
            yield break;
        }

        foreach (var result in Semaphores(context, explorer).OrderByDescending(r => r.Finding.Score).Take(3))
        {
            yield return result;
        }

        foreach (var result in OrphanedTasks(context, explorer).OrderByDescending(r => r.Finding.Score).Take(3))
        {
            yield return result;
        }
    }

    private IEnumerable<AnalyzerResult> Semaphores(AnalysisContext context, ObjectExplorer explorer)
    {
        var index = explorer.Index;
        var semaphoreType = index.Types.FindIndex(t => t.Name == "System.Threading.SemaphoreSlim");
        if (semaphoreType < 0)
        {
            yield break;
        }

        var heap = context.Runtime.Heap;
        for (var i = 0; i < index.Count; i++)
        {
            if (index.TypeIds[i] != semaphoreType)
            {
                continue;
            }

            var semaphore = heap.GetObject(index.Addresses[i], index.Types[semaphoreType]);
            if (!semaphore.TryReadField<int>("m_currentCount", out var free) || free != 0
                || !semaphore.TryReadObjectField("m_asyncHead", out var head) || head.IsNull || !head.IsValid)
            {
                continue;
            }

            // Each async waiter is a task in the semaphore's queue; what awaits that task is the waiting method.
            var waiters = new List<IReadOnlyList<string>>();
            var total = 0;
            ulong sample = 0;
            string? sampleMethod = null;
            var seen = new HashSet<ulong>();
            for (var node = head; !node.IsNull && node.IsValid && seen.Add(node.Address) && total < MaxWaiters;)
            {
                if (AsyncStackWalker.IsPending(node))
                {
                    total++;
                    var (stack, innermost) = AppStack(node);
                    waiters.Add(stack);
                    if (sample == 0 && stack.Count > 0)
                    {
                        sample = innermost;
                        sampleMethod = stack[0];
                    }
                }

                // Into a separate variable: the call reads from `node` after clearing its out argument.
                if (!node.TryReadObjectField("Next", out var next))
                {
                    break;
                }

                node = next;
            }

            if (total == 0)
            {
                continue;
            }

            // A semaphore nothing roots is garbage along with its waiters: nobody is stuck on it.
            var roots = explorer.FindRoots(new RootsQuery(semaphore.Address, RootPathMode.Shortest), CancellationToken.None);
            if (roots.Unrooted)
            {
                continue;
            }

            var owner = DuplicateStringsAnalyzer.FieldHolding(explorer, semaphore.Address);
            var named = waiters.Where(w => w.Count > 0).ToList();
            if (owner is null && named.Count == 0)
            {
                continue; // framework plumbing with no application code waiting on it
            }

            var blockedThreads = semaphore.TryReadField<int>("m_waitCount", out var waitCount) ? Math.Max(0, waitCount) : 0;
            var limit = semaphore.TryReadField<int>("m_maxCount", out var max) && max is > 0 and < int.MaxValue ? max : (int?)null;
            var what = owner is null ? "a `SemaphoreSlim`" : Code(owner);
            var top = named.GroupBy(w => w[0]).OrderByDescending(g => g.Count()).FirstOrDefault();
            var why = (limit is { } l ? $"`SemaphoreSlim` with 0 of {l} {(l == 1 ? "slot" : "slots")} free" : "`SemaphoreSlim` with no free slot")
                      + $" · {Plural(total, "async waiter")}" + (blockedThreads > 0 ? $" · {Plural(blockedThreads, "blocked thread")}" : " · no thread is blocked on it");
            var evidence = new List<EntityRef> { EntityUri.ObjectRef(semaphore.Address, owner ?? "SemaphoreSlim", $"{Count(total)} waiting") };
            if (sample != 0)
            {
                evidence.Add(EntityUri.ObjectRef(sample, sampleMethod ?? "waiter", "one of the waiting operations"));
            }

            // Re-entrancy: a waiter that is itself awaited by a method the other waiters queue in.
            // That outer method got in (it is past the wait the others are stuck at) and is now
            // waiting for the same semaphore again, further down its own call chain.
            var entries = named.Select(w => w[0]).ToHashSet();
            var reentrant = named.FirstOrDefault(w => w.Skip(1).Any(m => m != w[0] && entries.Contains(m)));
            if (reentrant is not null)
            {
                var holder = reentrant.Skip(1).First(m => m != reentrant[0] && entries.Contains(m));
                var inner = reentrant[0];
                var behind = total - 1;
                yield return new AnalyzerResult(new Finding(
                    $"async-lock.reentrant.{owner ?? semaphore.Address.ToString("x")}", Name, Severity.Critical, Confidence.High,
                    $"Async deadlock on {what}: {Code(holder)} holds it and waits for it again in {Code(inner)}",
                    why,
                    "`SemaphoreSlim` isn't re-entrant: it doesn't know who holds it, so code that holds it and then awaits it again is waiting for itself. No thread shows the problem, because an awaiting method doesn't use one. The operation never finishes, every later caller queues behind it, and the process doesn't recover without a restart.",
                    [
                        $"Don't take the semaphore again inside the section it protects. Split {Code(inner)} into a public method that takes it and a private one that assumes it is held, and call the private one from {Code(holder)}.",
                        "Restart the process to recover now: the semaphore will never be released.",
                        "Pass a timeout or `CancellationToken` to `WaitAsync`, so a mistake like this fails with an exception instead of hanging silently.",
                    ],
                    [Symptom.Hang],
                    evidence,
                    Scores.For(Severity.Critical, Confidence.High, Math.Min(1, total / 100.0))),
                    VerdictHeadline: $"The app is hung on an async lock: {holder} holds it and is waiting for it again"
                                     + (behind > 0 ? $", with {Plural(behind, "more operation")} queued behind." : "."),
                    VerdictBody: $"{Sentence(what)} has no free slot. {Code(holder)} took it and then called {Code(inner)}, which awaits the same semaphore, so it waits for itself. No thread is blocked: the waiters are suspended async operations, which is why the process looks idle.");
                continue;
            }

            var severity = total >= WarnWaiters ? Severity.Warning : Severity.Info;
            yield return new AnalyzerResult(new Finding(
                $"async-lock.queued.{owner ?? semaphore.Address.ToString("x")}", Name, severity, Confidence.Medium,
                $"{Plural(total, "async operation")} {(total == 1 ? "is" : "are")} waiting for {what}" + (top is null ? "" : $", mostly in {Code(top.Key)}"),
                why,
                "A `SemaphoreSlim` used as an async lock or throttle has no free slot, and these operations are queued on it. They hold no thread while they wait, so nothing looks blocked: the work simply doesn't finish. If whoever holds the slots is only slow, this is a queue. If a path forgot to call `Release()` (an early return, or an exception outside `try`/`finally`), the slot is gone for good and the waiters never resume.",
                [
                    "Check that every path that takes the semaphore releases it: `await gate.WaitAsync(); try { … } finally { gate.Release(); }`.",
                    "Find the holder: Threads › Async stacks shows the operations that are past the wait and what they are awaiting now.",
                    "Pass a timeout or `CancellationToken` to `WaitAsync`, so a lost slot fails with an exception instead of hanging silently.",
                ],
                [Symptom.Hang, Symptom.SlowCpu],
                evidence,
                Scores.For(severity, Confidence.Medium, Math.Min(1, total / 100.0))),
                VerdictHeadline: $"{Sentence(Plural(total, "async operation"))} {(total == 1 ? "is" : "are")} queued on {(owner is null ? "a SemaphoreSlim" : owner)}, which has no free slot.",
                VerdictBody: "No thread is blocked: the waiters are suspended async operations, so the process looks idle while requests don't finish. They resume only when the current holder calls `Release()`.");
        }
    }

    private IEnumerable<AnalyzerResult> OrphanedTasks(AnalysisContext context, ObjectExplorer explorer)
    {
        // Only modern .NET: on .NET Framework an async method's own task is a bare Task too.
        if (context.AsyncStacks is not { } stacks || context.Metadata.PrimaryRuntime()?.Family == RuntimeFamily.NetFramework)
        {
            yield break;
        }

        var heap = context.Runtime.Heap;
        var index = explorer.Index;
        var onStacks = context.Threads.SelectMany(t => t.Roots).Select(r => r.Address).ToHashSet();
        foreach (var group in stacks.Groups.Where(g => g.Awaiting == AsyncStackWalker.UncompletedTask && g.Samples.Count > 0).Take(MaxOrphanGroups))
        {
            var leaf = heap.GetObject(group.Samples[0]);
            if (AsyncStackWalker.AwaitedObject(leaf) is not { } task || !IsBarePromise(task))
            {
                continue;
            }

            var taskIndex = index.IndexOf(task.Address);
            if (taskIndex < 0)
            {
                continue;
            }

            // Everything on the waiting side: the chain of state machines that resume when the task completes.
            var waiting = new HashSet<ulong>();
            for (var box = leaf; waiting.Count < MaxChain && waiting.Add(box.Address);)
            {
                if (box.Type?.GetFieldByName("StateMachine") is { IsObjectReference: true } field && field.ReadObject(box.Address, false) is { IsNull: false } machine)
                {
                    waiting.Add(machine.Address);
                }

                if (AsyncStackWalker.Awaiters(box).FirstOrDefault() is not { IsNull: false } next)
                {
                    break;
                }

                box = next;
            }

            // Only its TaskCompletionSource can complete the task. Is one still reachable by
            // anything other than the code that is waiting for it?
            ulong source = 0;
            var completable = false;
            foreach (var referrer in index.ReferrersOf(taskIndex))
            {
                if (index.TypeOf(referrer).Name?.StartsWith("System.Threading.Tasks.TaskCompletionSource", StringComparison.Ordinal) != true)
                {
                    continue;
                }

                source = index.Addresses[referrer];
                if (onStacks.Contains(source)
                    || explorer.FindRoots(new RootsQuery(source, RootPathMode.All, 8), CancellationToken.None).Paths
                        .Any(p => !p.Steps.Any(s => EntityUri.TryGetAddress(s.Object.Uri, out var a) && waiting.Contains(a))))
                {
                    completable = true;
                    break;
                }
            }

            // If nothing roots the waiters either, the whole lot is garbage and nobody is stuck.
            if (completable || explorer.FindRoots(new RootsQuery(leaf.Address, RootPathMode.Shortest), CancellationToken.None).Unrooted)
            {
                continue;
            }

            var where = group.FirstAppFrame ?? group.Frames[0];
            var result = FrameClassifier.ShortTypeName(task.Type?.Name);
            var evidence = new List<EntityRef> { EntityUri.ObjectRef(leaf.Address, group.Frames[0], "one of the waiting operations") };
            evidence.Add(EntityUri.ObjectRef(task.Address, result, "the task that can't complete"));
            var n = group.Count;
            yield return new AnalyzerResult(new Finding(
                $"async-lock.orphaned.{where}", Name, Severity.Warning, Confidence.High,
                $"{Plural(n, "async operation")} in {Code(where)} {(n == 1 ? "awaits" : "await")} a task that nothing can complete",
                $"Awaiting a {Code(result)} from a `TaskCompletionSource` · "
                    + (source == 0 ? "the source has already been collected" : "nothing but the waiting code still references the source"),
                "A task handed out by a `TaskCompletionSource` finishes only when someone calls `SetResult`, `SetException` or `SetCanceled` on that source. Nothing that could make that call holds the source any more: the callback or handler that was meant to complete it was never stored, or has since been dropped. These operations will wait forever, and so will everything awaiting them.",
                [
                    $"Find where {Code(where)} (or what it calls) creates the `TaskCompletionSource`, and follow how it is supposed to be completed: a callback that is never registered, a registration that is removed, or an error path that returns without calling `SetException`.",
                    "Complete the source on every path, including failure and cancellation (`token.Register(() => tcs.TrySetCanceled())`).",
                    "Bound the wait so a lost completion surfaces as an error: `await task.WaitAsync(TimeSpan.FromSeconds(30))`.",
                ],
                [Symptom.Hang],
                evidence,
                Scores.For(Severity.Warning, Confidence.High, Math.Min(1, n / 100.0))),
                VerdictHeadline: $"{Sentence(Plural(n, "async operation"))} in {where} {(n == 1 ? "is" : "are")} waiting for a task that nothing can complete.",
                VerdictBody: $"The task comes from a `TaskCompletionSource` that nothing but the waiting code references, so no code can ever finish it. No thread is blocked; the operations just never resume.");
        }
    }

    /// <summary>A plain <c>Task</c>/<c>Task&lt;T&gt;</c> with no delegate to run: the kind a TaskCompletionSource hands out.</summary>
    private static bool IsBarePromise(ClrObject task)
    {
        var name = task.Type?.Name ?? "";
        return (name == "System.Threading.Tasks.Task" || (name.StartsWith("System.Threading.Tasks.Task<", StringComparison.Ordinal) && !name.Contains('+')))
               && AsyncStackWalker.IsPending(task)
               && (!task.TryReadObjectField("m_action", out var action) || action.IsNull);
    }

    /// <summary>
    /// The application methods waiting on <paramref name="task"/>, innermost first, found by
    /// following who awaits whom; and the innermost state-machine box (for evidence).
    /// </summary>
    private static (IReadOnlyList<string> Methods, ulong Innermost) AppStack(ClrObject task)
    {
        var methods = new List<string>();
        ulong innermost = 0;
        var seen = new HashSet<ulong>();
        var current = task;
        while (seen.Count < MaxChain && seen.Add(current.Address))
        {
            if (AsyncStackWalker.Awaiters(current).FirstOrDefault() is not { IsNull: false } box)
            {
                break;
            }

            if (AsyncStackWalker.IsApp(box))
            {
                methods.Add(AsyncStackWalker.MethodOf(box));
                if (innermost == 0)
                {
                    innermost = box.Address;
                }
            }

            current = box;
        }

        return (methods, innermost);
    }
}
