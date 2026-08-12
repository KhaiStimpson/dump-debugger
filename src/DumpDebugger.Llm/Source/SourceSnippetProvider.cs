using System.Text;
using DumpDebugger.Core.Findings;
using DumpDebugger.Core.Source;

namespace DumpDebugger.Llm;

/// <summary>
/// Turns the SourceLocations already resolved onto finding evidence (via Source Link — see
/// DumpDebugger.Analysis.SourceLink) into actual code text, by reading each file's content at
/// the exact commit the dump's build came from out of the user's local clone. This is what
/// lets the narrative cite real code instead of guessing from a bare type/method name — the
/// "LLM narrative enrichment" half of the feature; the location resolution itself needs no
/// RepoContext at all (see SourceLocationResolver).
/// </summary>
public static class SourceSnippetProvider
{
    private const int ContextLines = 6;
    private const int MaxSnippets = 8;

    public static async Task<IReadOnlyList<SourceSnippet>> BuildAsync(
        FindingsDocument findings, RepoContext repo, CancellationToken ct)
    {
        var locations = findings.Findings
            .SelectMany(f => f.Evidence)
            .Select(e => e.Location)
            .OfType<SourceLocation>()
            .DistinctBy(loc => (loc.RelativePath, loc.CommitSha, loc.Line))
            .Take(MaxSnippets)
            .ToList();

        var snippets = new List<SourceSnippet>();
        foreach (var location in locations)
        {
            ct.ThrowIfCancellationRequested();

            var content = await GitRepoReader
                .TryReadFileAtCommitAsync(repo.LocalPath, location.CommitSha, location.RelativePath, ct)
                .ConfigureAwait(false);
            if (content is null)
            {
                continue; // Commit not local, path renamed, wrong repo associated — skip silently.
            }

            var window = ExtractWindow(content, location.Line);
            if (window is null)
            {
                continue;
            }

            var blame = await GitRepoReader
                .TryGetBlameAsync(repo.LocalPath, location.CommitSha, location.RelativePath, location.Line, ct)
                .ConfigureAwait(false);

            snippets.Add(new SourceSnippet(location.RelativePath, location.CommitSha, location.Line, Redactor.Redact(window), blame));
        }

        return snippets;
    }

    /// <summary>Formats snippets as a block to append to the (already-redacted) findings JSON
    /// payload sent to the model. Redacted again here too — cheap, and matches the existing
    /// "redact right before it leaves the process" defense-in-depth posture.</summary>
    public static string FormatForPrompt(IReadOnlyList<SourceSnippet> snippets)
    {
        if (snippets.Count == 0)
        {
            return "";
        }

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine("Source context (read from the associated repository at each finding's build commit):");
        foreach (var snippet in snippets)
        {
            sb.AppendLine();
            sb.AppendLine($"### {snippet.RepoRelativePath} @ {snippet.CommitSha[..Math.Min(8, snippet.CommitSha.Length)]} (line {snippet.Line})");
            if (snippet.Blame is { } blame)
            {
                sb.AppendLine($"(last changed by {blame.Author} on {blame.When:yyyy-MM-dd}: \"{blame.Summary}\")");
            }

            sb.Append(Redactor.Redact(snippet.Code));
        }

        return sb.ToString();
    }

    private static string? ExtractWindow(string content, int line)
    {
        var lines = content.Split('\n');
        if (line < 1 || line > lines.Length)
        {
            return null;
        }

        var start = Math.Max(1, line - ContextLines);
        var end = Math.Min(lines.Length, line + ContextLines);

        var sb = new StringBuilder();
        for (var i = start; i <= end; i++)
        {
            var marker = i == line ? ">" : " ";
            sb.AppendLine($"{marker} {i,5}: {lines[i - 1].TrimEnd('\r')}");
        }

        return sb.ToString();
    }
}
