using System.Net.Http.Json;
using System.Text.Json;
using DumpDebugger.Core.Findings;

namespace DumpDebugger.Llm;

/// <summary>
/// PLAN.md §2.5 alternate provider, used only when ANTHROPIC_API_KEY is set in the
/// environment (never prompted for, never stored by this app — read from the environment
/// only). Calls the Messages API directly rather than pulling in the Anthropic SDK, to keep
/// this project's dependency footprint minimal. Coded against the documented Messages API
/// shape but not exercised in this session (no API key was available in this environment) —
/// same "correct but unvalidated" status as the HttpContext extraction path in Phase 5.
/// </summary>
public sealed class AnthropicApiNarrativeProvider : INarrativeProvider
{
    private const string ApiUrl = "https://api.anthropic.com/v1/messages";
    private const string Model = "claude-opus-5";

    private readonly string _apiKey;
    private readonly HttpClient _httpClient = new();

    private AnthropicApiNarrativeProvider(string apiKey) => _apiKey = apiKey;

    public bool IsAvailable => true;

    public string Description => "Anthropic API key";

    public static AnthropicApiNarrativeProvider? TryDetect()
    {
        var key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        return string.IsNullOrWhiteSpace(key) ? null : new AnthropicApiNarrativeProvider(key);
    }

    public async Task<Narrative> SummarizeAsync(FindingsDocument findings, IReadOnlyList<SourceSnippet> sourceContext, CancellationToken ct)
    {
        var payload = Redactor.Redact(JsonSerializer.Serialize(findings)) + SourceSnippetProvider.FormatForPrompt(sourceContext);

        using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
        request.Headers.Add("x-api-key", _apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content = JsonContent.Create(new
        {
            model = Model,
            max_tokens = 2048,
            system = ClaudeCliSystemPrompt,
            messages = new[] { new { role = "user", content = payload } },
        });

        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        var text = doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString() ?? "";
        return ParseNarrative(text);
    }

    private const string ClaudeCliSystemPrompt =
        "You are analyzing a .NET memory dump's findings document (produced by deterministic " +
        "analyzers, not by you). Findings are authoritative: do not invent evidence, attribute " +
        "every claim to a finding id, and say \"the dump does not show this\" rather than " +
        "speculating beyond the data. You may also be given source code snippets read directly " +
        "from the repository at the exact commit the dump's build was compiled from (resolved " +
        "via Source Link data embedded in the dump's PDBs) — these are ground truth for what " +
        "the code actually does at that point; cite them by file:line when they inform a claim, " +
        "and don't speculate about code you were not shown. Respond with ONLY a single JSON " +
        "object of this exact shape, no markdown fencing, no other text: " +
        "{\"summary\":\"...\",\"hypotheses\":[\"...\"],\"nextSteps\":[\"...\"]}";

    private static Narrative ParseNarrative(string resultText)
    {
        var jsonStart = resultText.IndexOf('{');
        var jsonEnd = resultText.LastIndexOf('}');
        if (jsonStart < 0 || jsonEnd <= jsonStart)
        {
            return new Narrative(resultText, [], []);
        }

        try
        {
            using var doc = JsonDocument.Parse(resultText[jsonStart..(jsonEnd + 1)]);
            var root = doc.RootElement;
            var summary = root.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : resultText;
            var hypotheses = root.TryGetProperty("hypotheses", out var h)
                ? h.EnumerateArray().Select(e => e.GetString() ?? "").ToList()
                : [];
            var nextSteps = root.TryGetProperty("nextSteps", out var n)
                ? n.EnumerateArray().Select(e => e.GetString() ?? "").ToList()
                : [];

            return new Narrative(summary, hypotheses, nextSteps);
        }
        catch (JsonException)
        {
            return new Narrative(resultText, [], []);
        }
    }
}
