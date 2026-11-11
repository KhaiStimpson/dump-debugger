namespace DumpDebugger.Core.Contracts;

/// <summary><c>!dumpheap -stat</c> row: one type's live instance count and total size.</summary>
public sealed record TypeStat(string TypeName, long Count, long TotalSize);

public sealed record GenerationSize(string Name, long Bytes);

/// <summary>Heap totals, computed from segment ranges (cheap) and refined by the heap walk.</summary>
public sealed record HeapOverview(
    long TotalBytes,
    long ObjectCount,
    int TypeCount,
    IReadOnlyList<GenerationSize> Generations,
    bool Walked);

/// <summary>One page of a long list. Workers page and aggregate; the page asks only for what's on screen.</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, int Total, int Offset);

public enum HeapGrouping
{
    Type,
    Namespace,
    Generation,
}

public enum HeapSort
{
    Size,
    Count,
    Name,
}

public sealed record HeapTypesQuery(HeapGrouping GroupBy, string? Filter, int Offset, int Limit, HeapSort Sort);

/// <summary>A row of the type table (or a namespace / generation bucket).</summary>
public sealed record TypeRow(string Name, long Count, long Size, long LohSize, double Share, bool IsApp);

/// <summary>Pre-bucketed treemap data: the top types, plus one "other" cell, so the page never receives the full type list just to draw it.</summary>
public sealed record TreemapCell(string Name, string Label, long Size, double Share, bool IsOther, int OtherCount);

public sealed record HeapSummary(HeapOverview Overview, IReadOnlyList<TreemapCell> Treemap, long FreeBytes, long LohFreeBytes);

public sealed record LargeObject(
    string Uri,
    ulong Address,
    string TypeName,
    long Size,
    string Generation,
    string Identity,
    string? Detail,
    bool Pinned);

public sealed record InstancesQuery(string TypeName, int Offset, int Limit);

public sealed record InstanceRow(string Uri, ulong Address, long Size, string Generation, string Identity);

public enum ValueKind
{
    Null,
    String,
    Number,
    Bool,
    Char,
    DateTime,
    Reference,
    Struct,
    Pointer,
    Other,
}

/// <summary>A field (or array element) with its value rendered for display. References carry the target address so the UI can navigate.</summary>
public sealed record FieldValue(
    string Name,
    string TypeName,
    string Display,
    ValueKind Kind,
    ulong? Address,
    string? Note,
    IReadOnlyList<FieldValue>? Children);

public sealed record ObjectQuery(ulong Address, int ElementOffset = 0, int ElementLimit = 100);

public sealed record ObjectView(
    string Uri,
    ulong Address,
    string TypeName,
    long Size,
    string Generation,
    string Identity,
    string? StringValue,
    int? Length,
    string? Preview,
    IReadOnlyList<FieldValue> Fields,
    IReadOnlyList<FieldValue>? Elements,
    int ElementOffset,
    bool IsFree);

public sealed record ReferrersQuery(ulong Address, int Offset, int Limit);

public sealed record Referrer(EntityRef Object, string TypeName, string Via);

public enum RootPathMode
{
    Shortest,
    All,
}

public sealed record RootsQuery(ulong Address, RootPathMode Mode, int MaxPaths = 10);

public enum RootKind
{
    Static,
    ThreadStatic,
    Stack,
    StrongHandle,
    PinnedHandle,
    AsyncPinned,
    RefCounted,
    FinalizerQueue,
    Other,
}

/// <summary>What keeps a path alive: "static ReportCache.s_entries", "local in CheckoutService.Commit on #47"…</summary>
public sealed record RootInfo(RootKind Kind, string Label, string Detail, int? ThreadId);

public sealed record RootPathStep(EntityRef Object, string TypeName, string? Via, string? Note);

/// <summary>Root first, target last. <see cref="RootPathStep.Via"/> is the field on that step pointing at the next one.</summary>
public sealed record RootPath(RootInfo Root, IReadOnlyList<RootPathStep> Steps);

public sealed record RootPathsResult(
    IReadOnlyList<RootPath> Paths,
    bool Truncated,
    bool Unrooted,
    double Seconds,
    string Explanation);
