using DumpDebugger.Core.Findings;

namespace DumpDebugger_App.ViewModels;

public sealed record FindingRow(
    string Severity,
    string Title,
    string Summary,
    string EvidenceText)
{
    public static FindingRow From(Finding finding) => new(
        finding.Severity.ToString(),
        finding.Title,
        finding.Summary,
        string.Join(Environment.NewLine, finding.Evidence.Select(e =>
            $"  - {e.Kind} {e.Ref}: {e.Detail}" + (e.Location is { } loc ? $" ({loc.RelativePath}:{loc.Line})" : ""))));
}
