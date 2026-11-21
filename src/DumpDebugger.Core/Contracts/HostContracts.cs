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

/// <summary>The page's resolved colour theme, so the host can match its caption buttons and dialogs.</summary>
public enum AppTheme
{
    Dark,
    Light,
}

public sealed record ThemeParams(AppTheme Theme);

public sealed record OpenDumpParams(string Path, bool AllowSymbolServer = false);

/// <summary>
/// What the host sends the worker for <c>dump.open</c>: the page's <see cref="OpenDumpParams"/>
/// plus the folders from the saved <see cref="SymbolSettings"/>.
/// </summary>
public sealed record WorkerOpenParams(
    string Path,
    bool AllowSymbolServer = false,
    IReadOnlyList<string>? SymbolPaths = null,
    IReadOnlyList<string>? SourcePaths = null);

/// <summary>
/// Where to look for the app's own debugging files. <see cref="SymbolPaths"/> are folders
/// searched for portable PDBs (directly, in symbol-store layout, and in subfolders);
/// <see cref="SourcePaths"/> are checkouts searched for a source file when the path recorded
/// at build time doesn't exist on this computer.
/// </summary>
public sealed record SymbolSettings(IReadOnlyList<string> SymbolPaths, IReadOnlyList<string> SourcePaths);

public sealed record PickedFolder(string? Path);

public sealed record AnalysisRestartParams(bool Force = false);

public sealed record ProgressNotification(string Stage, double FractionComplete, string? Detail);

/// <summary>Small diagnostic events only: never dump contents or heap/query payloads.</summary>
public sealed record DebugNotification(string Source, string Stage, string Detail, bool Error = false);

public sealed record AnalysisFailure(string Message);

public enum WorkerState
{
    Starting,
    Ready,
    Exited,
}

public sealed record WorkerStatus(WorkerState State, string Architecture, int? ProcessId, string? Message);

public sealed record PickedFile(string? Path);

public sealed record DumpOpened(Dump.DumpMetadata Metadata, string WorkspacePath, bool CachedAnalysis, string? AnalysisError = null);

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

public enum ReportFormat
{
    Markdown,
    Html,
}

public sealed record SaveReportParams(ReportFormat Format, string FileName, string Content);

/// <summary>Where the report was written, or null if the user cancelled the save dialog.</summary>
public sealed record SavedReport(string? Path);

/// <summary>
/// The app-hosted MCP server, as shown in the status bar panel. <see cref="Url"/> and
/// <see cref="Token"/> are what an MCP client is configured with; the server only listens on
/// 127.0.0.1 and only while <see cref="Running"/>.
/// </summary>
public sealed record McpStatus(
    bool Available,
    bool Enabled,
    bool Running,
    int Port,
    string Url,
    string Token,
    bool AllowDacDownload,
    string? Error);

/// <summary>Only the members that are set change. <see cref="AllowDacDownload"/> covers dumps an agent opens itself.</summary>
public sealed record McpSettingsParams(bool? Enabled = null, int? Port = null, bool? AllowDacDownload = null);
