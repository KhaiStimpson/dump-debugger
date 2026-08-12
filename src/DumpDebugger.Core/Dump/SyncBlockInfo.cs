namespace DumpDebugger.Core.Dump;

/// <summary>Flat `!syncblk`-equivalent row (PLAN.md §3.2 "Locks & Deadlocks").</summary>
public sealed record SyncBlockInfo(
    ulong ObjectAddress,
    string? ObjectTypeName,
    int? OwnerOSThreadId,
    int WaitingThreadCount,
    int RecursionCount);
