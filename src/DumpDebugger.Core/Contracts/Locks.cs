namespace DumpDebugger.Core.Contracts;

/// <summary>A contended or held monitor (<c>lock (obj)</c>), named after the field that holds it when that can be found.</summary>
public sealed record LockInfo(
    string Uri,
    ulong Address,
    string Name,
    string TypeName,
    int? OwnerThread,
    string? AcquiredAt,
    IReadOnlyList<int> Waiters,
    int Recursion,
    bool InCycle);

public enum WaitEdgeKind
{
    Owns,
    WaitsFor,
    QueuedBehind,
}

/// <summary>A node is a thread, a lock, or a cluster of threads with identical waits (<see cref="Count"/> &gt; 1).</summary>
public sealed record WaitGraphNode(
    string Id,
    EntityKind Kind,
    string Label,
    string? Sub,
    string? Detail,
    bool InCycle,
    int Count,
    IReadOnlyList<int> ThreadIds);

public sealed record WaitGraphEdge(string From, string To, WaitEdgeKind Kind, bool InCycle);

public sealed record LockCycle(
    string Id,
    IReadOnlyList<int> Threads,
    IReadOnlyList<string> Locks,
    string Explanation,
    IReadOnlyList<string> AcquiredAt);

public sealed record WaitGraph(
    IReadOnlyList<WaitGraphNode> Nodes,
    IReadOnlyList<WaitGraphEdge> Edges,
    IReadOnlyList<LockCycle> Cycles,
    IReadOnlyList<LockInfo> Locks);

public sealed record WaitGraphQuery(bool CyclesOnly);
