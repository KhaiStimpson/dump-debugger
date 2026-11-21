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
        { "strings", "duplicate-strings", Severity.Warning },
        { "events", "event-handlers", Severity.Warning },
        { "pinned", "gc-handles", Severity.Warning },
        { "threads", "thread-count", Severity.Warning },
        { "asynclock", "async-locks", Severity.Critical },
        { "hotcpu", "hot-threads", Severity.Warning },
        { "stackoverflow", "crash", Severity.Critical },
        { "failfast", "crash", Severity.Critical },
        { "assemblies", "assemblies", Severity.Warning },
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
        ["strings"] = ["duplicate-strings"],
        ["events"] = ["event-handlers"],
        ["pinned"] = ["gc-handles"],
        ["threads"] = ["thread-count"],
        ["asynclock"] = ["async-locks"],
        ["hotcpu"] = ["hot-threads"],
        ["stackoverflow"] = ["crash"],
        ["failfast"] = ["crash"],
        ["assemblies"] = ["assemblies"],
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
        Assert.DoesNotContain(d.Findings, f => f.Analyzer is "threadpool-starvation" or "finalizer" or "timers" or "loh-fragmentation" or "http-sockets"
            or "duplicate-strings" or "event-handlers" or "gc-handles" or "thread-count"
            or "async-locks" or "hot-threads" or "crash" or "assemblies");
    }

    [Fact]
    public async Task Async_lock_scenario_also_reports_the_task_nothing_can_complete()
    {
        var (d, _) = await Golden.AnalyzeAsync("asynclock");
        var orphaned = Assert.Single(d.Findings, f => f.Id.StartsWith("async-lock.orphaned."));
        Assert.StartsWith("12 async operations in `ShipmentTracker.PrintLabelAsync`", orphaned.Title);
        Assert.Equal(Severity.Warning, orphaned.Severity);
    }

    [Fact]
    public async Task Hot_threads_reads_cpu_time_and_the_regex_from_the_dump()
    {
        var (d, _) = await Golden.AnalyzeAsync("hotcpu");
        var regex = Assert.Single(d.Findings, f => f.Id.StartsWith("cpu.regex."));
        Assert.Contains("^([A-Z0-9]+-?)+$", regex.Why);
        Assert.Contains("of CPU used", regex.Why);
        var busy = Assert.Single(d.Findings, f => f.Id.StartsWith("cpu.busy."));
        Assert.Contains("SalesRollup.Recompute", busy.Title);
    }

    [Fact]
    public async Task Assemblies_scenario_reports_generated_and_duplicated_assemblies()
    {
        var (d, _) = await Golden.AnalyzeAsync("assemblies");
        Assert.Contains(d.Findings, f => f.Id == "assemblies.dynamic" && f.Title.Contains("`XmlSerializer` for `OrderFeed`"));
        Assert.Contains(d.Findings, f => f.Id.StartsWith("assemblies.copies.") && f.Title.Contains("loaded 61 times"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public void Recursion_is_found_as_the_shortest_repeating_run_of_frames(int period)
    {
        var top = new[] { "[HelperMethodFrame]", "String.Concat" };
        var cycle = Enumerable.Range(0, period).Select(i => $"Tree.Visit{i}").ToArray();
        var frames = top.Concat(Enumerable.Range(0, 120).Select(i => cycle[i % period])).Append("Program.Main").ToList();

        Assert.Equal((2, period), DumpDebugger.Analysis.Findings.CrashCauseAnalyzer.FindRecursion(frames));
    }

    [Fact]
    public void An_ordinary_stack_is_not_recursion()
    {
        var frames = Enumerable.Range(0, 60).Select(i => $"Pipeline.Step{i}").ToList();
        Assert.Null(DumpDebugger.Analysis.Findings.CrashCauseAnalyzer.FindRecursion(frames));
        Assert.Null(DumpDebugger.Analysis.Findings.CrashCauseAnalyzer.FindRecursion(["A.Run", "A.Run", "A.Run", "Program.Main"]));
    }

    [Theory]
    [InlineData(0xC0000005u, "an access violation")]
    [InlineData(0xC00000FDu, "a stack overflow")]
    [InlineData(0xC0000374u, "heap corruption")]
    [InlineData(0xC0000139u, "a native exception")]
    [InlineData(0x80000003u, null)] // a breakpoint: a snapshot, not a crash
    [InlineData(0x00000000u, null)]
    public void Native_exception_codes_are_named(uint code, string? expected) =>
        Assert.Equal(expected, DumpDebugger.Analysis.Findings.CrashCauseAnalyzer.NativeExceptionName(code));

    [Theory]
    [InlineData("4DE0FF15EEE61C00", true)]   // call qword ptr [rip+disp]
    [InlineData("000000E810203040", true)]   // call rel32
    [InlineData("909090909090FFD0", true)]   // call rax
    [InlineData("3F00009066666666", false)]  // padding in front of a stub, not a call
    public void A_return_address_is_one_that_follows_a_call(string bytesBefore, bool expected) =>
        Assert.Equal(expected, DumpDebugger.Analysis.Findings.CrashCauseAnalyzer.FollowsCall(Convert.FromHexString(bytesBefore), DumpDebugger.Core.Dump.DumpArchitecture.X64));

    [Theory]
    [InlineData("System.Text.RegularExpressions.RegexInterpreter.Backtrack", "Regex")]
    [InlineData("System.Collections.Generic.Dictionary<System.String, System.Int32>.FindValue", "Dictionary")]
    [InlineData("System.Collections.Hashtable.get_Item", "Dictionary")]
    [InlineData("System.Threading.SpinWait.SpinOnceCore", "Spin")]
    [InlineData("System.Linq.Enumerable.Sum", "None")]
    public void Framework_frames_on_a_running_thread_name_the_usual_cpu_culprits(string frame, string expected) =>
        Assert.Equal(expected, DumpDebugger.Analysis.Findings.HotThreadAnalyzer.Classify([null, frame]).ToString());
}
