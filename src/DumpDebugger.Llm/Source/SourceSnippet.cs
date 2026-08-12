namespace DumpDebugger.Llm;

/// <summary>A window of source code read from the associated repo at a finding's build commit,
/// for grounding the LLM narrative in real code instead of a type/method name alone.</summary>
/// <param name="Blame">Who last touched this exact line as of this commit, when available — the
/// "recent-change" signal: freshly-changed code is a very different lead than ancient, stable code.</param>
public sealed record SourceSnippet(
    string RepoRelativePath,
    string CommitSha,
    int Line,
    string Code,
    BlameInfo? Blame = null);
