using DumpDebugger.Analysis.Locks;
using DumpDebugger.Analysis.Threads;
using DumpDebugger.Core.Contracts;
using static DumpDebugger.Analysis.Findings.Text;

namespace DumpDebugger.Analysis.Findings;

/// <summary>
/// A cycle in the wait-for graph: every thread in it holds a lock the next one wants. Edges are
/// read exactly from sync-block owners and the objects on each blocked thread's
/// <c>Monitor.Enter</c> frame, so this is reported at high confidence.
/// </summary>
public sealed class DeadlockAnalyzer : IFindingAnalyzer
{
    public string Name => "deadlock";

    public AnalyzerStage Stage => AnalyzerStage.Threads;

    public IEnumerable<AnalyzerResult> Analyze(AnalysisContext context)
    {
        var locks = context.Locks;
        var byId = context.ThreadsById;
        var index = 0;
        foreach (var cycle in locks.Cycles)
        {
            index++;
            var members = cycle.Threads.Select(id => byId[id]).ToList();
            var cycleLocks = cycle.Locks.Select(a => locks.Locks[a]).ToList();
            var behind = ThreadsBehind(context, cycle).ToList();

            // Name each side by the method that took its first lock ("CheckoutService.Commit").
            var acquirers = members.Select(t => HeldCycleLock(t, locks)?.AcquiredIn ?? t.WhereName ?? $"thread #{t.Id}").ToList();
            var requests = members.Select(t => t.Request).ToList();
            var allRequests = requests.All(r => r is not null);

            var title = members.Count == 2
                ? $"Deadlock between {Code(acquirers[0])} and {Code(acquirers[1])}"
                : $"Deadlock among {members.Count} threads: {JoinAnd(acquirers.Select(Code).ToList())}";
            var why = $"Lock-order inversion on {Plural(cycleLocks.Count, "lock")} · {Plural(members.Count, "thread")} in cycle"
                      + (behind.Count > 0 ? $" · {Plural(behind.Count, "thread")} waiting behind" : "");

            var meaning = members.Count == 2
                ? "Two pieces of code take the same two locks in opposite order. Under load they eventually interleave: each grabs its first lock, then waits forever for the other's. Anything else that needs either lock queues behind them. The process won't recover without a restart."
                : $"{members.Count} threads each hold a lock the next one needs, in a closed loop. None of them can proceed, and anything else that needs those locks queues behind them. The process won't recover without a restart.";

            var steps = new List<string>
            {
                "Restart the process (or recycle the app pool) to recover now. A deadlocked process doesn't heal on its own.",
            };

            var first = members[0];
            var firstHeld = HeldCycleLock(first, locks);
            var firstWanted = first.WaitingOnLock is { } fw ? locks.Locks[fw] : null;
            if (firstHeld is not null && firstWanted is not null)
            {
                var others = members.Skip(1).Select(t => HeldCycleLock(t, locks)?.AcquiredIn).Where(n => n is not null).Select(n => Code(n!)).ToList();
                steps.Add($"Take the locks in one order everywhere: {Chip(firstHeld.Ref.Uri)} before {Chip(firstWanted.Ref.Uri)}, as {Code(acquirers[0])} does. "
                          + (others.Count > 0 ? $"{JoinAnd(others)} take{(others.Count == 1 ? "s" : "")} them the other way round." : ""));

                if (first.WhereName is { } inner && inner != firstHeld.AcquiredIn)
                {
                    steps.Add($"Or don't call out while holding a lock: {Code(acquirers[0])} calls {Code(inner)} while it holds {Chip(firstHeld.Ref.Uri)}.");
                }
            }

            var evidence = new List<EntityRef>();
            evidence.AddRange(members.Select(context.ThreadRef));
            evidence.AddRange(cycleLocks.Select(l => l.Ref));
            if (allRequests)
            {
                evidence.AddRange(requests.Distinct().Select(r => EntityUri.RequestRef(r!)));
            }

            var finding = new Finding(
                Id: $"deadlock.cycle.{index}",
                Analyzer: Name,
                Severity: Severity.Critical,
                Confidence: Confidence.High,
                Title: title,
                Why: why,
                WhatItMeans: meaning,
                NextSteps: steps,
                Symptoms: [Symptom.Hang, Symptom.SlowCpu],
                Evidence: evidence,
                Score: Scores.For(Severity.Critical, Confidence.High, Math.Min(1, behind.Count / 30.0)),
                Visual: FindingVisual.WaitGraph);

            // Verdict: lead with what the user experiences (requests), then the mechanism.
            var stuckRequests = allRequests ? members.Count : 0;
            var headline = stuckRequests > 0
                ? $"The app is hung: {Plural(stuckRequests, "request")} {(stuckRequests == 1 ? "is" : "are")} deadlocked"
                  + (behind.Count > 0 ? $", and {Plural(behind.Count, "more thread")} {(behind.Count == 1 ? "is" : "are")} queued behind them." : ".")
                : $"{Plural(members.Count, "thread")} {(members.Count == 1 ? "is" : "are")} deadlocked"
                  + (behind.Count > 0 ? $", and {Plural(behind.Count, "more thread")} {(behind.Count == 1 ? "is" : "are")} queued behind them." : ".");

            var body = LockModelBuilder.ExplainCycle(cycle, locks, byId)
                       + " Neither can proceed, so everything that needs "
                       + (cycleLocks.Count == 2 ? "either lock" : "these locks") + " is stuck.";

            yield return new AnalyzerResult(finding, headline, body);
        }
    }

    private static LockRecord? HeldCycleLock(ThreadRecord t, LockModel locks) =>
        t.Holds.Select(h => locks.Locks.GetValueOrDefault(h)).FirstOrDefault(l => l is { InCycle: true });

    /// <summary>Threads whose wait chain ends in this cycle, excluding the cycle itself.</summary>
    private static IEnumerable<ThreadRecord> ThreadsBehind(AnalysisContext context, CycleRecord cycle)
    {
        var members = cycle.Threads.ToHashSet();
        foreach (var t in context.Threads)
        {
            if (members.Contains(t.Id))
            {
                continue;
            }

            var seen = new HashSet<int>();
            var current = t;
            while (current.WaitingOnThread is { } next && seen.Add(current.Id))
            {
                if (members.Contains(next))
                {
                    yield return t;
                    break;
                }

                if (!context.ThreadsById.TryGetValue(next, out current!))
                {
                    break;
                }
            }
        }
    }
}
