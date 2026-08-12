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

public sealed record EvidenceItem(
    string Kind,
    string Ref,
    string Detail);

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
