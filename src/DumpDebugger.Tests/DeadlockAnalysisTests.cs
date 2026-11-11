using DumpDebugger.Core.Contracts;

namespace DumpDebugger.Tests;

[Collection(WorkspaceCollection.Name)]
public class DeadlockAnalysisTests
{
    [Fact]
    public async Task Deadlock_fixture_yields_an_english_verdict_naming_methods_and_locks()
    {
        var (d, _) = await Golden.AnalyzeAsync("deadlock");

        Assert.Equal(Severity.Critical, d.Verdict.Severity);
        Assert.Equal(Confidence.High, d.Verdict.Confidence);
        Assert.StartsWith("The app is hung: 2 requests are deadlocked", d.Verdict.Headline);

        var deadlock = d.Findings[0];
        Assert.Equal("deadlock", deadlock.Analyzer);
        Assert.Contains("CheckoutService.Commit", deadlock.Title);
        Assert.Contains("InventoryService.Reserve", deadlock.Title);
        Assert.Contains(deadlock.Evidence, e => e.Kind == EntityKind.Lock && e.Label == "OrderLedger._gate");
        Assert.Contains(deadlock.Evidence, e => e.Kind == EntityKind.Lock && e.Label == "InventoryCache._sync");

        // The v1 failure mode: runtime helper frames must never be where a thread "is".
        var everything = string.Join("\n", d.Findings.Select(f => f.Title + f.Why + f.WhatItMeans + string.Join("", f.NextSteps)));
        Assert.DoesNotContain("InlinedCallFrame", everything);
        Assert.DoesNotContain("DebuggerU2MCatchHandlerFrame", everything);
    }

    [Fact]
    public async Task Deadlock_fixture_matches_golden_findings()
    {
        var (d, groups) = await Golden.AnalyzeAsync("deadlock");
        Golden.Assert("deadlock", Golden.Render(d, groups));
    }

    [Fact]
    public async Task Bridge_streams_the_diagnosis_after_open_and_caches_it_in_the_workspace()
    {
        var dump = await FixtureDumps.GetAsync("deadlock");
        var workspaceDir = dump + ".dumpdebugger";
        if (Directory.Exists(workspaceDir))
        {
            Directory.Delete(workspaceDir, recursive: true);
        }

        await using (var bridge = new BridgeHarness())
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var opened = await bridge.CallAsync<DumpOpened>("dump.open", new { path = dump });
            Assert.False(opened.CachedAnalysis);
            await bridge.Server.AnalysisTask!.WaitAsync(TimeSpan.FromSeconds(60));

            // R2 done-when: open → verdict in under 5 s on the fixture (worker start included).
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"Open to verdict took {clock.Elapsed.TotalSeconds:F1}s.");

            var state = await bridge.CallAsync<AnalysisState>("analysis.get");
            Assert.NotNull(state.Diagnosis);
            Assert.True(state.Diagnosis!.Complete);
            Assert.Contains(bridge.Notifications, n => n.Method == "analysis.updated");

            var groups = await bridge.CallAsync<ThreadGroupsResult>("threads.groups", new { groupBy = "stack", state = "blocked" });
            Assert.Contains(groups.Groups, g => g.InDeadlock);
        }

        await using (var bridge = new BridgeHarness())
        {
            var reopened = await bridge.CallAsync<DumpOpened>("dump.open", new { path = dump });
            Assert.True(reopened.CachedAnalysis);
            var state = await bridge.CallAsync<AnalysisState>("analysis.get");
            Assert.True(state.Diagnosis!.FromCache);
        }
    }
}
