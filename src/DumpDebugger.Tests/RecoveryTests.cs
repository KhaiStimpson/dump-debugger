using System.Diagnostics;
using DumpDebugger.Core.Contracts;
using DumpDebugger.Core.Rpc;
using DumpDebugger.Mcp;
using DumpDebugger.Session;

namespace DumpDebugger.Tests;

/// <summary>What happens after something goes wrong: a worker dies, a cache can't be written, an analysis has to run again.</summary>
[Collection(WorkspaceCollection.Name)]
public class RecoveryTests
{
    private static async Task KillWorkerAsync(WorkerSession worker)
    {
        using var process = Process.GetProcessById(worker.ProcessId);
        process.Kill();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        for (var i = 0; i < 100 && !worker.HasExited; i++)
        {
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task A_dump_whose_worker_died_leaves_the_mcp_registry_and_can_be_opened_again()
    {
        var dump = await FixtureDumps.GetAsync("deadlock");
        await using var registry = new DumpRegistry(() => null);
        var first = await registry.OpenAsync(dump, default);
        await KillWorkerAsync(first.Session.Worker);

        Assert.Empty(registry.List());
        Assert.Contains("No dump 'd1' is open", Assert.Throws<ModelContextProtocol.McpException>(() => registry.Resolve("d1")).Message);

        var second = await registry.OpenAsync(dump, default);
        Assert.NotSame(first.Session, second.Session);
        Assert.False(second.Session.Worker.HasExited);
        var groups = await second.Session.Connection.CallAsync<ThreadGroupsResult>("threads.groups", new ThreadGroupsQuery(ThreadGrouping.Flat, ThreadStateFilter.All, null));
        Assert.True(groups!.TotalThreads >= 3);
    }

    [Fact]
    public async Task After_the_apps_worker_dies_queries_say_to_reopen_and_reopening_works()
    {
        var dump = await FixtureDumps.GetAsync("deadlock");
        await using var bridge = new BridgeHarness();
        await bridge.CallAsync<DumpOpened>("dump.open", new { path = dump });
        await KillWorkerAsync(bridge.Server.Session!);

        var query = await Assert.ThrowsAsync<RpcException>(() => bridge.CallAsync("threads.groups", new { groupBy = "flat", state = "all" }));
        Assert.Equal(RpcError.WorkerUnavailable, query.Error.Code);
        Assert.Contains("Reopen the dump", query.Error.Message);
        var restart = await Assert.ThrowsAsync<RpcException>(() => bridge.CallAsync("analysis.restart", new { force = true }));
        Assert.Equal(RpcError.WorkerUnavailable, restart.Error.Code);

        await bridge.CallAsync<DumpOpened>("dump.open", new { path = dump });
        var groups = await bridge.CallAsync<ThreadGroupsResult>("threads.groups", new { groupBy = "flat", state = "all" });
        Assert.True(groups.TotalThreads >= 3);
    }

    [Fact]
    public async Task Analysis_can_be_run_again_on_request_but_a_finished_one_is_not_rerun_by_accident()
    {
        var dump = await FixtureDumps.GetAsync("deadlock");
        await using var bridge = new BridgeHarness();
        await bridge.CallAsync<DumpOpened>("dump.open", new { path = dump });
        if (bridge.Server.AnalysisTask is { } first)
        {
            await first.WaitAsync(TimeSpan.FromSeconds(120));
        }

        var before = bridge.Server.LatestDiagnosis!;
        Assert.True(before.Complete);
        var refused = await Assert.ThrowsAsync<RpcException>(() => bridge.CallAsync("analysis.restart", new { force = false }));
        Assert.Contains("already complete", refused.Error.Message);

        await bridge.CallAsync<Ack>("analysis.restart", new { force = true });
        await bridge.Server.AnalysisTask!.WaitAsync(TimeSpan.FromSeconds(120));
        var after = bridge.Server.LatestDiagnosis!;
        Assert.True(after.Complete);
        Assert.False(after.FromCache);
        Assert.True(after.AnalyzedAt > before.AnalyzedAt);
        Assert.Equal(before.Verdict.Headline, after.Verdict.Headline);
    }

    [Fact]
    public async Task A_diagnosis_that_cannot_be_cached_is_still_a_successful_analysis()
    {
        var dump = await FixtureDumps.GetAsync("deadlock");
        var cache = Workspace.For(dump).PathOf("diagnosis.json");
        var kept = File.Exists(cache) ? File.ReadAllBytes(cache) : null;
        File.Delete(cache);
        Directory.CreateDirectory(cache); // a directory where the file should go: the write fails
        try
        {
            await using var bridge = new BridgeHarness();
            var opened = await bridge.CallAsync<DumpOpened>("dump.open", new { path = dump });
            Assert.False(opened.CachedAnalysis);
            await bridge.Server.AnalysisTask!.WaitAsync(TimeSpan.FromSeconds(120));

            Assert.True(bridge.Server.LatestDiagnosis!.Complete);
            Assert.DoesNotContain(bridge.Notifications, n => n.Method == "analysis.failed");
            Assert.Contains(bridge.Notifications, n => n.Method == "debug.event"
                && ContractJson.FromElement<DebugNotification>(n.Params) is { Stage: "workspace", Error: true });
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
            if (kept is not null)
            {
                File.WriteAllBytes(cache, kept);
            }
        }
    }

    [Fact]
    public async Task A_compare_that_arrives_while_the_snapshot_is_being_captured_shares_that_capture()
    {
        var dump = await FixtureDumps.GetAsync("deadlock");
        var workspace = Workspace.For(dump);
        File.Delete(workspace.PathOf("snapshot.json"));
        File.Delete(workspace.PathOf("snapshot.manifest.json"));

        var builds = 0;
        await using var session = await DumpSession.OpenAsync(dump, new DumpSessionHooks(
            Notify: (method, payload) =>
            {
                if (method == "debug.event" && ContractJson.FromElement<DebugNotification>(payload) is { Stage: "snapshot" })
                {
                    Interlocked.Increment(ref builds);
                }

                return Task.CompletedTask;
            },
            Symbols: () => new SymbolSettings([], [])), default);

        // Several callers at once, on top of the background capture that follows analysis.
        var snapshots = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => session.GetSnapshotAsync(default)));
        if (session.SnapshotTask is { } background)
        {
            await background.WaitAsync(TimeSpan.FromSeconds(60));
        }

        Assert.All(snapshots, s => Assert.NotEmpty(s.Types));
        Assert.Equal(1, builds);
        Assert.True(workspace.HasSnapshotFor(session.RuntimeIdentity));

        // This fixture's sidecar is committed without a snapshot; leave it that way.
        File.Delete(workspace.PathOf("snapshot.json"));
        File.Delete(workspace.PathOf("snapshot.manifest.json"));
    }
}
