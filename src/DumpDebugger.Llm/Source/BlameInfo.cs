namespace DumpDebugger.Llm;

/// <summary>Who last touched a specific line, as of a specific commit — "is this hot/leaking
/// code freshly changed or ancient and stable" is exactly the kind of signal a bare stack frame
/// can't give you.</summary>
public sealed record BlameInfo(string CommitSha, string Author, DateTimeOffset When, string Summary);
