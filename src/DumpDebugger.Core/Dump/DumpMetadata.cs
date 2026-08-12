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
    string? DacPath,
    DacResolutionTier DacTier,
    bool IsManagedAnalysisAvailable);

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
    int ModuleCount);
