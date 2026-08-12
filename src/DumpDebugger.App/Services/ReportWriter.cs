using System.Text;
using DumpDebugger.Core.Findings;

namespace DumpDebugger_App.Services;

/// <summary>PLAN.md §3.2 "Report": export the investigation's findings to Markdown.</summary>
public static class ReportWriter
{
    public static string ToMarkdown(FindingsDocument document)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Dump Debugger Report");
        sb.AppendLine();
        sb.AppendLine($"- **Dump:** `{document.Dump.Path}`");
        sb.AppendLine($"- **Runtime:** {document.Dump.Runtime}");
        sb.AppendLine($"- **Architecture:** {document.Dump.Architecture}");
        sb.AppendLine($"- **Crash dump:** {document.Dump.IsCrash}");
        sb.AppendLine();

        if (document.Findings.Count == 0)
        {
            sb.AppendLine("No findings.");
            return sb.ToString();
        }

        sb.AppendLine($"## Findings ({document.Findings.Count})");
        sb.AppendLine();

        foreach (var finding in document.Findings)
        {
            sb.AppendLine($"### [{finding.Severity}] {finding.Title}");
            sb.AppendLine();
            sb.AppendLine($"*Analyzer: {finding.Analyzer} · Confidence: {finding.Confidence}*");
            sb.AppendLine();
            sb.AppendLine(finding.Summary);
            sb.AppendLine();

            if (finding.Evidence.Count > 0)
            {
                sb.AppendLine("**Evidence:**");
                foreach (var evidence in finding.Evidence)
                {
                    var link = evidence.Location is { } loc
                        ? $" ([{loc.RelativePath}:{loc.Line}]({loc.ToGitHubBlobUrl()}))"
                        : "";
                    sb.AppendLine($"- `{evidence.Kind}` {evidence.Ref}: {evidence.Detail}{link}");
                }

                sb.AppendLine();
            }
        }

        return sb.ToString();
    }
}
