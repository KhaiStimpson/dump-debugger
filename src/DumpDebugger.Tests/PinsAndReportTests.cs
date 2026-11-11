using DumpDebugger.Core.Contracts;
using DumpDebugger.Session;

namespace DumpDebugger.Tests;

/// <summary>Shares the deadlock fixture's workspace sidecar with other tests, so they run one at a time.</summary>
[Collection(WorkspaceCollection.Name)]
public class PinsAndReportTests
{
    [Fact]
    public async Task Reopening_a_dump_restores_its_pins_and_notes()
    {
        var dump = await FixtureDumps.GetAsync("deadlock");
        await using (var bridge = new BridgeHarness())
        {
            await bridge.CallAsync<DumpOpened>("dump.open", new { path = dump });
            foreach (var existing in await bridge.CallAsync<List<Pin>>("pins.list"))
            {
                await bridge.CallAsync("pins.remove", new { uri = existing.Uri });
            }

            await bridge.CallAsync("pins.upsert", new { uri = "thread/47", kind = "thread", label = "#47", note = (string?)null });
            var pins = await bridge.CallAsync<List<Pin>>("pins.upsert", new { uri = "thread/47", kind = "thread", label = "#47", note = "Line 118, lock taken inside foreach." });
            Assert.Single(pins);
            await bridge.CallAsync("pins.upsert", new { uri = "finding/deadlock.cycle.1", kind = "finding", label = "Deadlock", note = "Root cause." });
            await bridge.CallAsync("dump.close");
        }

        await using (var bridge = new BridgeHarness())
        {
            await bridge.CallAsync<DumpOpened>("dump.open", new { path = dump });
            var restored = await bridge.CallAsync<List<Pin>>("pins.list");
            Assert.Equal(2, restored.Count);
            Assert.Equal("Line 118, lock taken inside foreach.", restored.Single(p => p.Uri == "thread/47").Note);
            Assert.True(File.Exists(Path.Combine(dump + ".dumpdebugger", "pins.json")));
        }
    }

    [Fact]
    public async Task Report_save_writes_the_file_the_user_picked()
    {
        var target = Path.Combine(Path.GetTempPath(), $"dd-report-{Guid.NewGuid():N}.html");
        var platform = new HeadlessPlatform { NextSavePath = target };
        string? response = null;
        await using var server = new HostRpcServer(platform, json =>
        {
            response = json;
            return Task.CompletedTask;
        });

        await server.HandleWebMessageAsync("""{"id":"1","method":"report.save","params":{"format":"html","fileName":"incident","content":"<!doctype html><p>ok</p>"}}""");

        Assert.Contains(target.Replace("\\", "\\\\"), response);
        Assert.Equal("<!doctype html><p>ok</p>", File.ReadAllText(target));
        File.Delete(target);
    }
}
