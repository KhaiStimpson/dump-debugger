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
    bool IsWait,
    string? SourcePath = null);

public enum SymbolState
{
    /// <summary>A matching portable PDB was found: frames in this module show file and line.</summary>
    Loaded,

    /// <summary>No PDB next to the module, in the PDB path recorded at build time, or in any symbol folder.</summary>
    NotFound,

    /// <summary>A PDB with the right name was found, but it belongs to a different build of the module.</summary>
    Mismatched,

    /// <summary>Only a Windows (native-format) PDB was found; portable PDBs are the ones that can be read.</summary>
    Unsupported,
}

/// <summary>One application module in the dump and whether its PDB could be read.</summary>
public sealed record SymbolModule(string Module, string? ModulePath, SymbolState State, string? PdbPath, string Detail);

/// <summary><see cref="Path"/> is a frame's <see cref="StackFrame.SourcePath"/>.</summary>
public sealed record SourceQuery(string Path, int Line, int Context = 6);

public enum SourceOrigin
{
    None,

    /// <summary>Embedded in the PDB by the build (<c>EmbedAllSources</c>).</summary>
    Embedded,

    /// <summary>The file at the path recorded in the PDB.</summary>
    Disk,

    /// <summary>The file found under one of the configured source folders.</summary>
    SourceFolder,
}

public sealed record SourceLine(int Number, string Text);

/// <summary>
/// A few lines of source around a frame. <see cref="Modified"/> is true when the file on disk
/// no longer matches the checksum the build recorded, so the highlighted line may be off.
/// </summary>
public sealed record SourceSnippet(
    string Path,
    string? ResolvedPath,
    SourceOrigin Origin,
    int Line,
    IReadOnlyList<SourceLine> Lines,
    bool Modified,
    string? Unavailable);

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
