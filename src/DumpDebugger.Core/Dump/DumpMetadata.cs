namespace DumpDebugger.Core.Dump;

public enum DumpArchitecture
{
    Unknown,
    X86,
    X64,
    Arm64,
}

public enum RuntimeFamily
{
    Unknown,
    NetFramework,
    NetCore,
}

public enum DacResolutionTier
{
    None,
    EmbeddedInDump,
    LocalMachine,
    AppCache,
    SymbolServer,
}

public sealed record RuntimeInfo(
    RuntimeFamily Family,
    string Version,
    string ProductVersion,
    string? DacPath,
    DacResolutionTier DacTier,
    bool IsManagedAnalysisAvailable,
    string? DacError = null,
    int? ManagedThreadCount = null,
    bool IsPrimary = false);

public sealed record DumpMetadata(
    string DumpPath,
    long FileSizeBytes,
    DumpArchitecture Architecture,
    bool IsFullMemoryDump,
    bool IsCrashDump,
    DateTimeOffset? CapturedAt,
    string? ProcessName,
    int ProcessId,
    IReadOnlyList<RuntimeInfo> Runtimes,
    int ThreadCount,
    int ModuleCount,
    int? CrashThreadOsId = null,
    uint? CrashExceptionCode = null);

public static class DumpMetadataExtensions
{
    /// <summary>The runtime analysis runs against: the loaded runtime with the most managed threads.</summary>
    public static RuntimeInfo? PrimaryRuntime(this DumpMetadata metadata) =>
        metadata.Runtimes.FirstOrDefault(r => r.IsPrimary) ?? metadata.Runtimes.FirstOrDefault();

    /// <summary>
    /// Which runtimes were loadable and which one analysis used. A cached diagnosis is only valid
    /// for the identity it was computed under: a newly downloaded DAC must trigger re-analysis.
    /// </summary>
    public static string RuntimeIdentity(this DumpMetadata metadata) => string.Join(";", metadata.Runtimes
        .Select(r => $"{r.Family}/{r.Version}/{(r.IsManagedAnalysisAvailable ? "loaded" : "missing")}{(r.IsPrimary ? "/primary" : "")}")
        .Order(StringComparer.Ordinal));
}
