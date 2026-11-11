using System.Text;
using System.Text.RegularExpressions;
using DumpDebugger.Analysis;
using DumpDebugger.Analysis.Threads;
using DumpDebugger.Core.Contracts;

namespace DumpDebugger.Tests;

/// <summary>
/// Golden snapshots of what an analyzer says about a DumpGen scenario. Thread ids and addresses
/// change from capture to capture, so they're normalized away; everything a user reads
/// (titles, verdict, next steps, groupings) is compared exactly. Set UPDATE_GOLDEN=1 to accept
/// a deliberate change; a missing golden file is written on first run.
/// </summary>
internal static partial class Golden
{
    public static void Assert(string name, string actual)
    {
        var path = Repo.Path("src", "DumpDebugger.Tests", "Golden", name + ".txt");
        actual = actual.Replace("\r\n", "\n");
        if (!File.Exists(path) || Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
            return;
        }

        var expected = File.ReadAllText(path).Replace("\r\n", "\n");
        Xunit.Assert.True(expected == actual, $"Golden '{name}' differs. Re-run with UPDATE_GOLDEN=1 if the change is intended.\n--- expected\n{expected}\n--- actual\n{actual}");
    }

    public static async Task<(Diagnosis Diagnosis, ThreadGroupsResult Groups)> AnalyzeAsync(string scenario)
    {
        var dump = await FixtureDumps.GetAsync(scenario);
        using var index = DumpIndex.Open(dump);
        var diagnosis = await new AnalysisPipeline(index, _ => Task.CompletedTask).RunAsync(includeHeap: true, CancellationToken.None);
        var (threads, _) = await index.EnsureThreadsAsync(CancellationToken.None);
        var groups = ThreadQueries.Groups(threads, new ThreadGroupsQuery(ThreadGrouping.Stack, ThreadStateFilter.All, null));
        return (diagnosis, groups);
    }

    /// <summary>The user-visible content of a diagnosis, normalized for comparison.</summary>
    public static string Render(Diagnosis d, ThreadGroupsResult? groups = null, Func<Finding, bool>? include = null)
    {
        var labels = d.Verdict.Entities.Concat(d.Findings.SelectMany(f => f.Evidence))
            .GroupBy(e => e.Uri).ToDictionary(g => g.Key, g => g.First().Label);
        var text = RenderCore(d, groups, include);
        text = Chip().Replace(text, m => labels.TryGetValue(m.Groups[1].Value, out var label) ? $"[{label}]" : m.Value);
        return Normalize(text);
    }

    private static string RenderCore(Diagnosis d, ThreadGroupsResult? groups, Func<Finding, bool>? include)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"verdict: {d.Verdict.Severity}/{d.Verdict.Confidence}");
        sb.AppendLine($"  {d.Verdict.Headline}");
        sb.AppendLine($"  {d.Verdict.Body}");
        foreach (var f in d.Findings.Where(include ?? (_ => true)))
        {
            sb.AppendLine($"finding {f.Id} {f.Severity}/{f.Confidence} [{string.Join(",", f.Symptoms)}]");
            sb.AppendLine($"  title: {f.Title}");
            sb.AppendLine($"  why: {f.Why}");
            sb.AppendLine($"  means: {f.WhatItMeans}");
            foreach (var step in f.NextSteps)
            {
                sb.AppendLine($"  next: {step}");
            }

            foreach (var e in f.Evidence.OrderBy(e => e.Kind).ThenBy(e => e.Label, StringComparer.Ordinal))
            {
                sb.AppendLine($"  evidence: {e.Kind} {e.Label}");
            }
        }

        if (groups is not null)
        {
            foreach (var line in groups.Groups.Where(g => g.WhereIsApp)
                         .Select(g => $"group: {g.Count} × {g.Where} | {g.Request} | {g.StateLabel}{(g.InDeadlock ? " (deadlock)" : "")}")
                         .Select(Normalize)
                         .OrderBy(l => l, StringComparer.Ordinal))
            {
                sb.AppendLine(line);
            }
        }

        return sb.ToString();
    }

    public static string Normalize(string s)
    {
        s = Address().Replace(s, "0x…");
        s = ThreadUri().Replace(s, "thread/N");
        s = ThreadNumber().Replace(s, "#N");
        s = WorkItems().Replace(s, "N work items");
        return s;
    }

    [GeneratedRegex(@"{{([^}]+)}}")]
    private static partial Regex Chip();

    [GeneratedRegex(@"\d[\d,]* work items?")]
    private static partial Regex WorkItems();

    [GeneratedRegex("0x[0-9a-f]{6,}")]
    private static partial Regex Address();

    [GeneratedRegex(@"thread/\d+")]
    private static partial Regex ThreadUri();

    [GeneratedRegex(@"#\d+")]
    private static partial Regex ThreadNumber();
}
