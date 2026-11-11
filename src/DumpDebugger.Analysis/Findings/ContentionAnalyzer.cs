using DumpDebugger.Core.Contracts;
using static DumpDebugger.Analysis.Findings.Text;

namespace DumpDebugger.Analysis.Findings;

/// <summary>
/// A lock with a queue of waiters that isn't part of a deadlock: a convoy. The interesting
/// part is what the owner is doing while everyone waits, so that's what the text leads with.
/// </summary>
public sealed class ContentionAnalyzer : IFindingAnalyzer
{
    private const int MinWaiters = 3;

    public string Name => "lock-contention";

    public AnalyzerStage Stage => AnalyzerStage.Threads;

    public IEnumerable<AnalyzerResult> Analyze(AnalysisContext context)
    {
        foreach (var l in context.Locks.Locks.Values.Where(l => !l.InCycle && l.Waiters.Count >= MinWaiters).OrderByDescending(l => l.Waiters.Count))
        {
            if (l.Owner is not { } ownerId || !context.ThreadsById.TryGetValue(ownerId, out var owner) || owner.InDeadlock)
            {
                continue; // the deadlock finding already explains this queue
            }

            var severity = l.Waiters.Count >= 8 ? Severity.Warning : Severity.Info;
            var doing = owner.WaitingOnLock is { } other && context.Locks.Locks.TryGetValue(other, out var otherLock)
                ? $"is itself waiting for {Chip(otherLock.Ref.Uri)}"
                : owner.State == ThreadState.Running
                    ? $"is running in {Code(owner.WhereName ?? owner.Where ?? "?")}"
                    : $"is {owner.StateLabel.ToLowerInvariant()} in {Code(owner.WhereName ?? owner.Where ?? "?")}";

            var evidence = new List<EntityRef> { l.Ref, context.ThreadRef(owner) };
            evidence.AddRange(l.Waiters.Take(6).Select(w => context.ThreadRef(context.ThreadsById[w])));

            yield return new AnalyzerResult(new Finding(
                Id: $"lock.convoy.0x{l.Address:x}",
                Analyzer: Name,
                Severity: severity,
                Confidence: Confidence.High,
                Title: $"{Plural(l.Waiters.Count, "thread")} queued on {Code(l.Name)}",
                Why: $"Held by #{owner.Id}" + (l.AcquiredIn is { } at ? $" since {Code(at)}" : ""),
                WhatItMeans: $"Every thread that needs {Chip(l.Ref.Uri)} waits until {Chip(EntityUri.Thread(owner.Id))} lets go, and {Chip(EntityUri.Thread(owner.Id))} {doing}. Requests that touch this lock are only as fast as that one thread.",
                NextSteps:
                [
                    $"Find out why {Chip(EntityUri.Thread(owner.Id))} holds the lock so long: open its stack.",
                    "Keep the critical section small: don't do I/O, call other services or take other locks while holding it.",
                    "If reads dominate, consider a reader/writer lock or an immutable snapshot instead of a single monitor.",
                ],
                Symptoms: [Symptom.Hang, Symptom.SlowCpu],
                Evidence: evidence,
                Score: Scores.For(severity, Confidence.High, l.Waiters.Count / 50.0),
                Visual: FindingVisual.WaitGraph));
        }
    }
}
