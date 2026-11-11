using System.Diagnostics;
using DumpDebugger.Core.Contracts;

namespace DumpDebugger.Tests;

[Collection(WorkspaceCollection.Name)]
public class CompareTests
{
    [Fact]
    public async Task Leak_scenario_is_flagged_against_its_baseline_and_compares_from_cache()
    {
        var baseline = await FixtureDumps.GetAsync("leak-baseline");
        var current = await FixtureDumps.GetAsync("leak");
        foreach (var dump in new[] { baseline, current })
        {
            if (Directory.Exists(dump + ".dumpdebugger"))
            {
                Directory.Delete(dump + ".dumpdebugger", recursive: true);
            }
        }

        // First run: the baseline has never been analyzed, so compare analyzes it once with a
        // temporary worker; the current dump is open and snapshots itself after analysis.
        CompareResult first;
        await using (var bridge = new BridgeHarness())
        {
            await bridge.CallAsync<DumpOpened>("dump.open", new { path = current });
            first = await bridge.CallAsync<CompareResult>("compare.run", new { baselinePath = baseline }, TimeSpan.FromMinutes(3));
        }

        var suspect = first.LeakSuspects.First();
        Assert.Equal("Contoso.Orders.CartSnapshot", suspect.TypeName);
        Assert.Equal(5_000, suspect.CountA);
        Assert.Equal(120_000, suspect.CountB);
        Assert.Equal("static SessionStore.s_snapshots", suspect.DominantRoot);
        Assert.NotEmpty(suspect.SampleAddressesInB);
        Assert.Contains(first.Types, t => t.TypeName == "System.Byte[]" && t.SizeDelta > 50L * 1024 * 1024);
        Assert.True(first.B.IsOpen);

        // Second run: both snapshots are cached; nothing is open and no worker is started.
        Assert.True(File.Exists(Path.Combine(baseline + ".dumpdebugger", "snapshot.json")));
        Assert.True(File.Exists(Path.Combine(current + ".dumpdebugger", "snapshot.json")));
        await using (var bridge = new BridgeHarness())
        {
            var clock = Stopwatch.StartNew();
            var cached = await bridge.CallAsync<CompareResult>("compare.run", new { baselinePath = baseline, currentPath = current });
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"Cached compare took {clock.Elapsed.TotalSeconds:F1}s.");
            Assert.Null(bridge.Server.Session);
            Assert.Equal("Contoso.Orders.CartSnapshot", cached.LeakSuspects.First().TypeName);
            Assert.False(cached.B.IsOpen);
        }
    }
}
