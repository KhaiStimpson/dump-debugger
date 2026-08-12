namespace DumpDebugger.Llm;

/// <summary>A window of source code read from the associated repo at a finding's build commit,
/// for grounding the LLM narrative in real code instead of a type/method name alone.</summary>
public sealed record SourceSnippet(
    string RepoRelativePath,
    string CommitSha,
    int Line,
    string Code);
