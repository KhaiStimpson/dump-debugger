using DumpDebugger.Core.Findings;

namespace DumpDebugger.Llm;

/// <summary>PLAN.md §2.5 default: no key, no CLI, no network. Every panel still works.</summary>
public sealed class NullNarrativeProvider : INarrativeProvider
{
    public static readonly NullNarrativeProvider Instance = new();

    public bool IsAvailable => false;

    public string Description => "Disabled (no Claude Code CLI or ANTHROPIC_API_KEY found)";

    public Task<Narrative> SummarizeAsync(FindingsDocument findings, IReadOnlyList<SourceSnippet> sourceContext, CancellationToken ct) =>
        throw new InvalidOperationException("NullNarrativeProvider is never available; check IsAvailable first.");
}
