using DumpDebugger.Core.Findings;

namespace DumpDebugger.Llm;

/// <summary>PLAN.md §8 provider interface. Detected at startup; the app hides "Explain this"
/// affordances entirely when IsAvailable is false rather than showing a broken control.</summary>
public interface INarrativeProvider
{
    bool IsAvailable { get; }

    string Description { get; } // "Claude Code CLI (subscription)" | "Anthropic API key" | "Disabled"

    /// <param name="sourceContext">Code snippets read from the associated repo at each finding's
    /// build commit (see SourceSnippetProvider) — empty when no repo is associated, or when
    /// nothing in the findings resolved a source location. Never raw memory, same as findings.</param>
    Task<Narrative> SummarizeAsync(FindingsDocument findings, IReadOnlyList<SourceSnippet> sourceContext, CancellationToken ct);
}
