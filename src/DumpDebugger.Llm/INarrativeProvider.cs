using DumpDebugger.Core.Findings;

namespace DumpDebugger.Llm;

/// <summary>PLAN.md §8 provider interface. Detected at startup; the app hides "Explain this"
/// affordances entirely when IsAvailable is false rather than showing a broken control.</summary>
public interface INarrativeProvider
{
    bool IsAvailable { get; }

    string Description { get; } // "Claude Code CLI (subscription)" | "Anthropic API key" | "Disabled"

    Task<Narrative> SummarizeAsync(FindingsDocument findings, CancellationToken ct);
}
