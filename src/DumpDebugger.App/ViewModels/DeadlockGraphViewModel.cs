using DumpDebugger.Core.Dump;
using DumpDebugger.Core.Ipc;

namespace DumpDebugger_App.ViewModels;

/// <summary>
/// Drives the wait-for graph canvas (Variant A mockup): two thread nodes each holding a
/// contended lock, dashed edges pointing at the lock the *other* thread holds — the same
/// two-node case LockAnalyzer's contended-lock-set heuristic detects. Graphs with more than
/// two contended owners aren't laid out (rare in practice); the canvas falls back to a text
/// note in that case.
/// </summary>
public sealed record DeadlockGraphViewModel(
    string Thread1Id,
    string Thread2Id,
    string Lock1Address,
    string Lock1TypeName,
    int Lock1Waiters,
    string Lock2Address,
    string Lock2TypeName,
    int Lock2Waiters)
{
    public static DeadlockGraphViewModel? From(GetLocksResponse locks)
    {
        var contended = locks.SyncBlocks
            .Where(s => s.WaitingThreadCount > 0 && s.OwnerOSThreadId.HasValue)
            .OrderBy(s => s.OwnerOSThreadId)
            .ToList();

        if (contended.Count < 2)
        {
            return null;
        }

        var a = contended[0];
        var b = contended[1];

        return new DeadlockGraphViewModel(
            a.OwnerOSThreadId!.Value.ToString(),
            b.OwnerOSThreadId!.Value.ToString(),
            $"0x{a.ObjectAddress:x}",
            a.ObjectTypeName ?? "object",
            a.WaitingThreadCount,
            $"0x{b.ObjectAddress:x}",
            b.ObjectTypeName ?? "object",
            b.WaitingThreadCount);
    }
}
