namespace DumpDebugger.Core.Source;

/// <summary>
/// A local clone of the repository a dump's source came from, associated with a workspace by
/// the user (see WorkspaceService.SaveRepoContext). Distinct from SourceLocation: locations are
/// resolved automatically from the dump itself, this is only needed when something wants actual
/// file content — reading a snippet for the LLM narrative, "open in editor" — rather than just
/// a repo URL and commit.
/// </summary>
public sealed record RepoContext(string LocalPath);
