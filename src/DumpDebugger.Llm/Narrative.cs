namespace DumpDebugger.Llm;

/// <summary>PLAN.md §8 "Output": narrative summary, ranked hypotheses, suggested next steps.</summary>
public sealed record Narrative(
    string Summary,
    IReadOnlyList<string> Hypotheses,
    IReadOnlyList<string> NextSteps);
