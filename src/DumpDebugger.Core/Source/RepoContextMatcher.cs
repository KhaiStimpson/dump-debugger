namespace DumpDebugger.Core.Source;

/// <summary>
/// Picks which associated RepoContext a resolved SourceLocation belongs to, for workspaces with
/// more than one repo associated (e.g. the app plus an internal NuGet package it depends on).
/// </summary>
public static class RepoContextMatcher
{
    public static RepoContext? Find(IReadOnlyList<RepoContext> repos, SourceLocation location)
    {
        if (repos.Count == 0)
        {
            return null;
        }

        var match = repos.FirstOrDefault(r =>
            r.RepoUrl is not null && string.Equals(r.RepoUrl, location.RepoUrl, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            return match;
        }

        // Exactly one repo associated and its URL didn't match (or couldn't be detected, e.g. no
        // "origin" remote): keep working as if it's the only candidate — this is the pre-multi-repo
        // behavior, preserved for the common single-repo case. With two or more repos associated
        // and no URL match, guessing which one is wrong more often than it's right, so return null
        // instead — callers already degrade gracefully (open falls back to the browsable URL;
        // blame/snippets are just skipped).
        return repos.Count == 1 ? repos[0] : null;
    }
}
