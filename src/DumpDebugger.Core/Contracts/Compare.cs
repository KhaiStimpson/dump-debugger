namespace DumpDebugger.Core.Contracts;

/// <summary>How sampled instances of one type are rooted: "static SessionStore.s_snapshots" × 20.</summary>
public sealed record RootShare(string Label, RootKind Kind, int Count);

/// <summary><see cref="AverageDepth"/>: mean root-path length; the holder of a leak sits closer to the root than what it holds.</summary>
public sealed record TypeRootSample(string TypeName, int Sampled, IReadOnlyList<RootShare> Roots, IReadOnlyList<ulong> SampleAddresses, double AverageDepth);

public sealed record SnapshotThreadGroup(string Where, string? Request, string StateLabel, int Count);

public sealed record SnapshotFinding(string Id, string Analyzer, Severity Severity, string Title);

/// <summary>
/// Everything Compare needs from one dump, cached in its workspace (<c>snapshot.json</c>) so a
/// baseline never has to be reopened: type statistics, thread groups, findings, and for the
/// biggest types a sample of their GC roots.
/// </summary>
public sealed record HeapSnapshot(
    int SchemaVersion,
    DumpSummary Dump,
    DateTimeOffset? CapturedAt,
    long TotalBytes,
    long ObjectCount,
    int ThreadCount,
    int BlockedThreads,
    IReadOnlyList<TypeStat> Types,
    IReadOnlyList<SnapshotThreadGroup> ThreadGroups,
    IReadOnlyList<SnapshotFinding> Findings,
    IReadOnlyList<TypeRootSample> RootSamples)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record CompareCandidate(string Path, string FileName, string Folder, bool HasSnapshot, bool Exists, DateTimeOffset OpenedAt, string? Runtime, Severity? VerdictSeverity, string? VerdictLabel);

/// <summary>Baseline A and current B. Omit B to compare against the open dump.</summary>
public sealed record CompareParams(string BaselinePath, string? CurrentPath);

public sealed record CompareSide(string Path, string FileName, DateTimeOffset? CapturedAt, long HeapBytes, long Objects, int Threads, int BlockedThreads, int Critical, string Runtime, bool IsOpen);

public sealed record TypeDelta(string TypeName, long CountA, long CountB, long SizeA, long SizeB, long SizeDelta, long CountDelta, double? Growth);

public sealed record ThreadGroupDelta(string Where, string? Request, string StateLabel, int CountA, int CountB);

public enum FindingChange
{
    New,
    Resolved,
    Persisting,
}

public sealed record FindingDiff(FindingChange Change, Severity Severity, string Title, string Analyzer);

/// <summary>A type that grew a lot between A and B and is mostly held by one root in B: the likely leak.</summary>
public sealed record LeakSuspect(
    string TypeName,
    long CountA,
    long CountB,
    long SizeDelta,
    double? Growth,
    string? DominantRoot,
    double? RootShare,
    IReadOnlyList<ulong> SampleAddressesInB,
    string Explanation);

public sealed record CompareResult(
    CompareSide A,
    CompareSide B,
    IReadOnlyList<Vital> Summary,
    IReadOnlyList<TypeDelta> Types,
    IReadOnlyList<ThreadGroupDelta> ThreadGroups,
    IReadOnlyList<FindingDiff> Findings,
    IReadOnlyList<LeakSuspect> LeakSuspects);

/// <summary>Built from the diagnosis the host already has (it may have come from the workspace cache).</summary>
public sealed record SnapshotParams(Diagnosis Diagnosis);
