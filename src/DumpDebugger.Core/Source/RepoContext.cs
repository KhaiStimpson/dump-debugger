namespace DumpDebugger.Core.Source;

/// <summary>
/// A local clone of a repository a dump's source came from, associated with a workspace by the
/// user (see WorkspaceService.SaveRepoContexts). Distinct from SourceLocation: locations are
/// resolved automatically from the dump itself, this is only needed when something wants actual
/// file content — reading a snippet for the LLM narrative, blame, "open in editor" — rather than
/// just a repo URL and commit.
///
/// A workspace can have more than one of these associated at once — a dump commonly spans
/// modules built from different repos (the app itself, plus an internal NuGet package) — so
/// RepoUrl (detected from `git remote get-url origin` at association time, normalized the same
/// way a SourceLocation's RepoUrl is) is what RepoContextMatcher uses to pick the right clone
/// for a given location.
/// </summary>
public sealed record RepoContext(string LocalPath, string? RepoUrl)
{
    /// <summary>The absolute local path a resolved location corresponds to in this clone —
    /// pure path arithmetic, no I/O, no check that the file (or even this commit) exists here.</summary>
    public string ResolveLocalPath(SourceLocation location) =>
        Path.Combine(LocalPath, location.RelativePath.Replace('/', Path.DirectorySeparatorChar));
}
