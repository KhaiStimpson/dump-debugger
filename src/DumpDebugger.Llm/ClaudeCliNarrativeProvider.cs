using System.Diagnostics;
using System.Text.Json;
using DumpDebugger.Core.Findings;

namespace DumpDebugger.Llm;

/// <summary>
/// PLAN.md §2.5 primary provider: shells out to the Claude Code CLI in headless mode, reusing
/// the user's existing subscription login — no API key to manage or store. Detected once at
/// startup by probing `claude --version`; SummarizeAsync feeds the redacted findings JSON on
/// stdin and instructs the model to answer with JSON matching the Narrative shape, attributing
/// every claim to a finding id and never inventing evidence (§4.4/§8 prompt discipline).
/// </summary>
public sealed class ClaudeCliNarrativeProvider : INarrativeProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private const string SystemInstructions =
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

    private ClaudeCliNarrativeProvider()
    {
    }

    public bool IsAvailable => true;

    public string Description => "Claude Code CLI (subscription)";

    public static async Task<ClaudeCliNarrativeProvider?> TryDetectAsync(CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo("claude", "--version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

            return process.ExitCode == 0 ? new ClaudeCliNarrativeProvider() : null;
        }
        catch
        {
            return null; // `claude` not on PATH, or the probe failed for any other reason.
        }
    }

    public async Task<Narrative> SummarizeAsync(FindingsDocument findings, IReadOnlyList<SourceSnippet> sourceContext, CancellationToken ct)
    {
        var payload = Redactor.Redact(JsonSerializer.Serialize(findings, JsonOptions))
            + SourceSnippetProvider.FormatForPrompt(sourceContext);

        var psi = new ProcessStartInfo("claude")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(SystemInstructions);
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("json");

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start claude CLI.");
        await process.StandardInput.WriteAsync(payload).ConfigureAwait(false);
        process.StandardInput.Close();

        var stdout = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"claude CLI exited {process.ExitCode}: {stderr}");
        }

        using var envelope = JsonDocument.Parse(stdout);
        var resultText = envelope.RootElement.GetProperty("result").GetString()
            ?? throw new InvalidOperationException("claude CLI response had no 'result' field.");

        return ParseNarrative(resultText);
    }

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
