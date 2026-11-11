namespace DumpDebugger.Core.Contracts;

public enum ThreadState
{
    Running,
    Blocked,
    Waiting,
    Idle,
}

public enum ThreadRole
{
    Request,
    PoolWorker,
    Main,
    Finalizer,
    Gc,
    Timer,
    Io,
    Background,
}

/// <summary>App = your code; Framework = BCL/ASP.NET/etc.; Runtime = CLR helper frames like <c>InlinedCallFrame</c>.</summary>
public enum FrameKind
{
    App,
    Framework,
    Runtime,
}

/// <summary>One stack frame. <see cref="Display"/> is the short form (<c>InventoryCache.TryReserve(Sku, int)</c>).</summary>
public sealed record StackFrame(
    string Display,
    string? FullName,
    FrameKind Kind,
    string? Module,
    int? IlOffset,
    string? SourceFile,
    int? Line,
    bool IsWait);

public sealed record ThreadSummary(
    int Id,
    int OsId,
    ThreadState State,
    string StateLabel,
    ThreadRole Role,
    string? Where,
    string? Request,
    string? RequestPath,
    string GroupKey,
    string? WaitingOnLock,
    int? WaitingOnThread,
    bool InDeadlock,
    string? Exception);

/// <summary>Threads collapsed by where they are (first application frame), request, or not at all.</summary>
public sealed record ThreadGroup(
    string Key,
    int Count,
    string Where,
    bool WhereIsApp,
    string? Request,
    ThreadState State,
    string StateLabel,
    IReadOnlyList<int> ThreadIds,
    bool InDeadlock);

public enum ThreadGrouping
{
    Stack,
    Request,
    Flat,
}

public enum ThreadStateFilter
{
    All,
    Blocked,
    Waiting,
    Running,
    Idle,
}

public sealed record ThreadGroupsQuery(ThreadGrouping GroupBy, ThreadStateFilter State, string? Search);

public sealed record ThreadStateCount(ThreadStateFilter State, int Count);

public sealed record ThreadGroupsResult(IReadOnlyList<ThreadGroup> Groups, int TotalThreads, IReadOnlyList<ThreadStateCount> Counts);

public sealed record ThreadQuery(int Id);

public sealed record ThreadDetail(
    ThreadSummary Thread,
    IReadOnlyList<StackFrame> Frames,
    IReadOnlyList<EntityRef> Holds,
    EntityRef? WaitingFor,
    EntityRef? WaitingForOwner,
    IReadOnlyList<EntityRef> StackObjects,
    IReadOnlyList<int> SameStack);

/// <summary>Pending async operations with the same logical stack (innermost method first) and the same thing awaited.</summary>
public sealed record AsyncStackGroup(int Count, IReadOnlyList<string> Frames, string Awaiting, string? FirstAppFrame, IReadOnlyList<ulong> Samples);

public sealed record AsyncStacksResult(IReadOnlyList<AsyncStackGroup> Groups, int Pending);
