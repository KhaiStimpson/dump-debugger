using System.Windows.Input;
using DumpDebugger.Core.Findings;

namespace DumpDebugger_App.ViewModels;

public sealed record FindingRow(
    string Severity,
    string Title,
    string Summary,
    IReadOnlyList<EvidenceRow> Evidence)
{
    public static FindingRow From(Finding finding, ICommand openCommand, ICommand blameCommand) => new(
        finding.Severity.ToString(),
        finding.Title,
        finding.Summary,
        finding.Evidence.Select(e => new EvidenceRow(e.Kind, e.Ref, e.Detail, e.Location, openCommand, blameCommand)).ToList());
}
