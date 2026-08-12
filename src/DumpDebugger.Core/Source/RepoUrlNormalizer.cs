using System.Text.RegularExpressions;

namespace DumpDebugger.Core.Source;

/// <summary>
/// Normalizes a repo URL — whether it came from `git remote get-url origin` ("git@github.com:
/// org/repo.git", "https://github.com/org/repo.git") or was derived from a resolved Source Link
/// URL (see DumpDebugger.Analysis.SourceLink.SourceLocationUrlParser) — to a single canonical
/// "https://{host}/{owner}/{repo}" shape. Both call sites route through this so a RepoContext's
/// detected RepoUrl and a SourceLocation's resolved RepoUrl compare equal for the same repo even
/// though they start from differently-shaped inputs — the mechanism multi-repo matching (see
/// RepoContextMatcher) depends on.
/// </summary>
public static partial class RepoUrlNormalizer
{
    public static string Normalize(string rawUrl)
    {
        var url = rawUrl.Trim();

        var sshMatch = SshRemotePattern().Match(url);
        if (sshMatch.Success)
        {
            url = $"https://{sshMatch.Groups["host"].Value}/{sshMatch.Groups["path"].Value}";
        }

        url = url.TrimEnd('/');
        if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            url = url[..^4];
        }

        return url;
    }

    // git@host:owner/repo(.git) -> host + path groups (the scp-like syntax git remotes commonly use).
    [GeneratedRegex(@"^[\w.-]+@(?<host>[\w.-]+):(?<path>.+)$")]
    private static partial Regex SshRemotePattern();
}
