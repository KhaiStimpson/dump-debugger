using DumpDebugger.Core.Dump;
using DumpDebugger.Core.Findings;
using Microsoft.Diagnostics.Runtime;

namespace DumpDebugger.Analysis;

/// <summary>
/// Phase 2 (PLAN.md §3.2/§4.2): sync-block enumeration ("the flat sync-block table for
/// people who want the `!syncblk` view") plus a best-effort deadlock detector.
///
/// ClrMD's SyncBlock exposes the owner thread and a waiter *count*, but not the waiters'
/// identities or which object a blocked thread is waiting on — a blocked thread's stack
/// frequently bottoms out in the native wait syscall with no distinguishing managed frame
/// left to pattern-match (confirmed against a real 2-thread/2-lock deadlock fixture: the
/// blocked threads' captured stacks show `Thread.Sleep`/state-machine frames, not `Monitor`).
///
/// So DetectDeadlocks uses the signal that *is* reliable: multiple sync blocks held by
/// different threads, each with waiters, at the same instant. A single contended lock is
/// normal (a convoy); two or more *simultaneously* contended locks with disjoint owners is
/// the hallmark of a cross-lock cycle — each owner is, by construction, occupying a lock
/// something else wants while that owner itself sits somewhere in the wait state. This can't
/// prove the exact wait-for edges (which owner waits on which specific object), so findings
/// are reported at Medium confidence per §4.4/§12 rather than claiming a proven cycle.
/// </summary>
public static class LockAnalyzer
{
    public static IReadOnlyList<SyncBlockInfo> EnumerateSyncBlocks(ClrRuntime runtime)
    {
        var threadsByAddress = runtime.Threads.ToDictionary(t => t.Address);
        var result = new List<SyncBlockInfo>();

        foreach (var syncBlock in runtime.Heap.EnumerateSyncBlocks())
        {
            if (!syncBlock.IsMonitorHeld && syncBlock.WaitingThreadCount == 0)
            {
                continue;
            }

            var obj = runtime.Heap.GetObject(syncBlock.Object);
            int? ownerOsThreadId = syncBlock.HoldingThreadAddress != 0
                && threadsByAddress.TryGetValue(syncBlock.HoldingThreadAddress, out var owner)
                ? (int)owner.OSThreadId
                : null;

            result.Add(new SyncBlockInfo(
                syncBlock.Object,
                obj.Type?.Name,
                ownerOsThreadId,
                syncBlock.WaitingThreadCount,
                syncBlock.RecursionCount));
        }

        return result;
    }

    public static IReadOnlyList<Finding> DetectDeadlocks(IReadOnlyList<SyncBlockInfo> syncBlocks)
    {
        var contendedByOwner = syncBlocks
            .Where(s => s.WaitingThreadCount > 0 && s.OwnerOSThreadId.HasValue)
            .GroupBy(s => s.OwnerOSThreadId!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        if (contendedByOwner.Count < 2)
        {
            return [];
        }

        var owners = contendedByOwner.Values.OrderBy(c => c.OwnerOSThreadId).ToList();
        var evidence = owners.Select(c => new EvidenceItem(
            "thread",
            c.OwnerOSThreadId!.Value.ToString(),
            $"holds 0x{c.ObjectAddress:x} ({c.ObjectTypeName}), contended by {c.WaitingThreadCount} waiter(s)")).ToList();

        var threadIds = owners.Select(c => c.OwnerOSThreadId!.Value).ToList();
        var finding = new Finding(
            Id: $"deadlock.contended-lock-set.{string.Join('-', threadIds)}",
            Analyzer: nameof(LockAnalyzer),
            Severity: Severity.Critical,
            Confidence: Confidence.Medium,
            Title: $"Possible deadlock among {threadIds.Count} threads holding contended locks",
            Summary: $"Threads {string.Join(", ", threadIds)} each hold a lock that other threads are " +
                      "currently waiting on, at the same instant. This is the signature of a cross-lock " +
                      "cycle, though the exact wait-for edges between these threads could not be read " +
                      "directly from the dump (see class remarks).",
            Evidence: evidence,
            Links: []);

        return [finding];
    }
}
