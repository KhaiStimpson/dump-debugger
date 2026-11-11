using DumpDebugger.Core.Contracts;

namespace DumpDebugger.Tests;

/// <summary>
/// R5: every analyzer has a DumpGen scenario and a golden snapshot. Each golden covers the
/// verdict plus the findings that scenario is about; other findings (e.g. async counts that
/// depend on how far background I/O got) are asserted loosely, not snapshotted.
/// </summary>
public class AnalyzerPackTests
{
    public static TheoryData<string, string, Severity> Scenarios => new()
    {
        { "threadpool", "threadpool-starvation", Severity.Critical },
        { "syncoverasync", "sync-over-async", Severity.Warning },
        { "exceptions", "exceptions", Severity.Warning },
        { "finalizer", "finalizer", Severity.Critical },
        { "loh", "loh-fragmentation", Severity.Warning },
        { "httpclient", "http-sockets", Severity.Warning },
        { "timers", "timers", Severity.Warning },
        { "crash", "exceptions", Severity.Critical },
    };

    private static readonly Dictionary<string, string[]> Subjects = new()
    {
        ["threadpool"] = ["threadpool-starvation"],
        ["syncoverasync"] = ["sync-over-async", "async-stacks"],
        ["exceptions"] = ["exceptions"],
        ["finalizer"] = ["finalizer"],
        ["loh"] = ["loh-fragmentation"],
        ["httpclient"] = ["http-sockets"],
        ["timers"] = ["timers"],
        ["crash"] = ["exceptions"],
    };

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Scenario_leads_with_its_analyzer_and_matches_golden(string scenario, string analyzer, Severity severity)
    {
        var (d, groups) = await Golden.AnalyzeAsync(scenario);

        var top = d.Findings[0];
        Assert.Equal(analyzer, top.Analyzer);
        Assert.Equal(severity, top.Severity);
        Assert.Equal(top.Id, d.Verdict.FindingId);
        Assert.NotEmpty(top.NextSteps);
        Assert.NotEmpty(top.Evidence);

        Golden.Assert(scenario, Golden.Render(d, groups, f => Subjects[scenario].Contains(f.Analyzer)));
    }

    [Fact]
    public async Task Sync_over_async_points_at_what_the_blocked_tasks_wait_for()
    {
        var (d, _) = await Golden.AnalyzeAsync("syncoverasync");
        var asyncFinding = Assert.Single(d.Findings, f => f.Analyzer == "async-stacks" && f.Title.Contains("Task.Delay"));
        Assert.StartsWith("200 async operations", asyncFinding.Title);
    }

    [Fact]
    public async Task Thread_pool_queue_is_read_from_the_heap()
    {
        var (d, _) = await Golden.AnalyzeAsync("threadpool");
        var vital = d.Vitals.Single(v => v.Key == "threadPool");
        Assert.Equal("24/24", vital.Value);
        var queued = int.Parse(System.Text.RegularExpressions.Regex.Match(vital.Detail, @"([\d,]+) queued").Groups[1].Value.Replace(",", ""));
        Assert.True(queued >= 200, $"Expected at least the 200 work items the scenario queues, read {queued}.");
    }

    [Fact]
    public async Task Healthy_scenarios_raise_none_of_the_pack()
    {
        var (d, _) = await Golden.AnalyzeAsync("deadlock");
        Assert.DoesNotContain(d.Findings, f => f.Analyzer is "threadpool-starvation" or "finalizer" or "timers" or "loh-fragmentation" or "http-sockets");
    }
}
