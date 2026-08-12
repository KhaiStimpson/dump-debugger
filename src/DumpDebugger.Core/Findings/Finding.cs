using DumpDebugger.Core.Source;

namespace DumpDebugger.Core.Findings;

public enum Severity
{
    Info,
    Warning,
    Error,
    Critical,
}

public enum Confidence
{
    Low,
    Medium,
    High,
}

/// <param name="Location">Where in the repository this evidence points, when Source Link
/// resolved a source location for it (see DumpDebugger.Analysis.SourceLink). Optional: most
/// evidence kinds (object addresses, sync blocks) have no single associated source line.</param>
public sealed record EvidenceItem(
    string Kind,
    string Ref,
    string Detail,
    SourceLocation? Location = null);

public sealed record FindingLink(
    string Label,
    string Target);

public sealed record Finding(
    string Id,
    string Analyzer,
    Severity Severity,
    Confidence Confidence,
    string Title,
    string Summary,
    IReadOnlyList<EvidenceItem> Evidence,
    IReadOnlyList<FindingLink> Links);
