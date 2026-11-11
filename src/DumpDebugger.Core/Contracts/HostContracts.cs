namespace DumpDebugger.Core.Contracts;

/// <summary>Marker for methods that take no parameters.</summary>
public sealed record NoParams;

/// <summary>Marker result for methods that only confirm completion.</summary>
public sealed record Ack(bool Ok = true);

public sealed record AppInfo(string Version, string HostArchitecture, string DataRoot, string? StartupDumpPath);

/// <summary>A rectangle in CSS pixels, relative to the window's client area.</summary>
public sealed record Rect(double X, double Y, double Width, double Height);

/// <summary>Titlebar hit-testing: the strip is draggable except over these interactive rects.</summary>
public sealed record TitlebarRegions(IReadOnlyList<Rect> Passthrough);

public sealed record OpenDumpParams(string Path);

public sealed record ProgressNotification(string Stage, double FractionComplete, string? Detail);

public enum WorkerState
{
    Starting,
    Ready,
    Exited,
}

public sealed record WorkerStatus(WorkerState State, string Architecture, int? ProcessId, string? Message);

public sealed record PickedFile(string? Path);

public sealed record DumpOpened(Dump.DumpMetadata Metadata, string WorkspacePath, bool CachedAnalysis);

/// <summary>The latest diagnosis for the open dump, or null before analysis has produced one.</summary>
public sealed record AnalysisState(Diagnosis? Diagnosis, bool Running);

public sealed record AnalysisRunParams(bool IncludeHeap = true);

/// <summary>A row on Home: a recently opened dump and the badge from its last verdict.</summary>
public sealed record RecentDump(
    string Path,
    string FileName,
    string Folder,
    DateTimeOffset OpenedAt,
    string? Runtime,
    string? Architecture,
    long SizeBytes,
    Severity? VerdictSeverity,
    string? VerdictLabel,
    bool Exists,
    bool IsSample);

public sealed record PathParams(string Path);

public sealed record SampleScenario(string Id, string Title, string Description);

public sealed record GenerateSampleParams(string Scenario);

public sealed record GeneratedSample(string Path);

/// <summary>A pinned entity and its note, stored in the dump's workspace sidecar.</summary>
public sealed record Pin(string Uri, EntityKind Kind, string Label, string Note, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record PinUpsert(string Uri, EntityKind Kind, string Label, string? Note);

public sealed record UriParams(string Uri);
