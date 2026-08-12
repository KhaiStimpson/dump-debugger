namespace DumpDebugger.Core.Source;

/// <summary>
/// A stack frame or evidence item resolved back to the exact source it was built from, via
/// Source Link data embedded in the dump's PDBs (see DumpDebugger.Analysis.SourceLink). No
/// local repo checkout is required to produce this — the commit and path come from the build
/// itself, not from anything the user configured.
/// </summary>
public sealed record SourceLocation(
    string RepoUrl,
    string CommitSha,
    string RelativePath,
    int Line,
    int Column);
