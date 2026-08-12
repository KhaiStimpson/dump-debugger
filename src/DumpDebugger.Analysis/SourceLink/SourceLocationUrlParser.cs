using System.Text.RegularExpressions;
using DumpDebugger.Core.Source;

namespace DumpDebugger.Analysis.SourceLink;

/// <summary>
/// Decomposes a resolved Source Link URL into (repo URL, commit SHA, path within the repo at
/// that commit). Deliberately provider-agnostic: rather than hardcoding GitHub/GitLab/Azure
/// DevOps URL shapes, it looks for the one thing every provider's raw-content URL has in
/// common — a bare 40-character commit SHA path segment — and splits on that. GitHub raw URLs
/// get one extra normalization (raw.githubusercontent.com -> github.com) so RepoUrl is a link a
/// human can actually open, not just a stable identifier — then routed through the same
/// RepoUrlNormalizer a RepoContext's `git remote` URL is normalized with, so the two compare
/// equal for RepoContextMatcher.
/// </summary>
public static partial class SourceLocationUrlParser
{
    public static bool TryParse(string url, out string repoUrl, out string commitSha, out string relativePath)
    {
        var match = CommitShaSegment().Match(url);
        if (!match.Success)
        {
            repoUrl = "";
            commitSha = "";
            relativePath = "";
            return false;
        }

        commitSha = match.Groups["sha"].Value;
        relativePath = url[(match.Index + match.Length)..].TrimStart('/');

        var prefix = url[..match.Index];
        repoUrl = RepoUrlNormalizer.Normalize(
            prefix.Replace("raw.githubusercontent.com", "github.com", StringComparison.OrdinalIgnoreCase));

        return !string.IsNullOrEmpty(relativePath);
    }

    [GeneratedRegex(@"/(?<sha>[0-9a-fA-F]{40})/")]
    private static partial Regex CommitShaSegment();
}
