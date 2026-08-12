using DumpDebugger.Core.Dump;

namespace DumpDebugger.Core.Findings;

public sealed record FindingsDumpSummary(
    string Path,
    string Runtime,
    string Architecture,
    DateTimeOffset? CapturedAt,
    bool IsCrash);

public sealed record FindingsDocument(
    int SchemaVersion,
    FindingsDumpSummary Dump,
    IReadOnlyList<Finding> Findings)
{
    public const int CurrentSchemaVersion = 1;

    public static FindingsDocument FromMetadata(DumpMetadata metadata, IReadOnlyList<Finding> findings)
    {
        var primaryRuntime = metadata.Runtimes.Count > 0
            ? $"{metadata.Runtimes[0].Family} {metadata.Runtimes[0].Version}"
            : "unknown";

        return new FindingsDocument(
            CurrentSchemaVersion,
            new FindingsDumpSummary(
                metadata.DumpPath,
                primaryRuntime,
                metadata.Architecture.ToString(),
                metadata.CapturedAt,
                metadata.IsCrashDump),
            findings);
    }
}
