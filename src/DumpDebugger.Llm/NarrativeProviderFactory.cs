namespace DumpDebugger.Llm;

/// <summary>PLAN.md §2.5 detection order: Claude Code CLI, then ANTHROPIC_API_KEY, then disabled.</summary>
public static class NarrativeProviderFactory
{
    public static async Task<INarrativeProvider> DetectAsync(CancellationToken ct = default)
    {
        var cli = await ClaudeCliNarrativeProvider.TryDetectAsync(ct).ConfigureAwait(false);
        if (cli is not null)
        {
            return cli;
        }

        var api = AnthropicApiNarrativeProvider.TryDetect();
        if (api is not null)
        {
            return api;
        }

        return NullNarrativeProvider.Instance;
    }
}
