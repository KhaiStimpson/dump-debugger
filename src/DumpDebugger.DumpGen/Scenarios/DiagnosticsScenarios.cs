using Microsoft.AspNetCore.Builder;
using Contoso.Feeds;
using Contoso.Import;
using Contoso.Invoicing;
using Contoso.Ledger;
using Contoso.Org;
using Contoso.Plugins;
using Contoso.Warehouse;

namespace Contoso.Scenarios;

/// <summary>
/// An async lock that deadlocks on itself: one reservation holds the gate and awaits it again,
/// forty more queue behind it, and no thread is blocked anywhere. Plus label requests awaiting
/// a TaskCompletionSource whose only completer was dropped.
/// </summary>
public static class AsyncLockScenario
{
    public static async Task RunAsync()
    {
        var inventory = new InventoryService();
        BackgroundJobs.Track(inventory.ReserveAsync("SKU-BOOT-42", 5));
        for (var i = 0; i < 40; i++)
        {
            BackgroundJobs.Track(inventory.ReserveAsync($"SKU-{i:D3}", 1));
        }

        var tracker = new ShipmentTracker(new CarrierClient());
        for (var i = 0; i < 12; i++)
        {
            BackgroundJobs.Track(tracker.PrintLabelAsync($"ORD-{i:D4}"));
        }

        await Task.Delay(500);
        GC.Collect();
        await ScenarioCatalog.ReadyAndParkAsync();
    }
}

/// <summary>Runaway CPU: four import threads stuck in a backtracking regular expression, and a rollup loop that never exits.</summary>
public static class HotCpuScenario
{
    public static Task RunAsync()
    {
        // Valid until the very last character, which is what makes the regex try every split.
        var rows = new[] { "WAREHOUSE7AISLE14SHELF3BIN22LOT2026X!" };
        for (var i = 0; i < 4; i++)
        {
            new Thread(() => ImportJob.Run(rows)) { IsBackground = true, Name = $"Import-{i}" }.Start();
        }

        var amounts = Enumerable.Range(1, 4096).ToArray();
        new Thread(() => SalesRollup.Recompute(amounts)) { IsBackground = true, Name = "SalesRollup" }.Start();

        Thread.Sleep(6000);
        return ScenarioCatalog.ReadyAndParkAsync();
    }
}

/// <summary>A real stack overflow: unbounded recursion over cyclic data ends the process, and the runtime writes the dump.</summary>
public static class StackOverflowScenario
{
    public static Task RunAsync()
    {
        var worker = new Thread(OrgSync.Publish) { IsBackground = true, Name = "OrgSync" };
        worker.Start();
        worker.Join();
        return Task.CompletedTask;
    }
}

/// <summary>The app ends itself with Environment.FailFast when an invariant fails, and the runtime writes the dump.</summary>
public static class FailFastScenario
{
    public static Task RunAsync()
    {
        var worker = new Thread(() => PostingService.PostBatch("B-2026-11")) { IsBackground = true, Name = "LedgerPosting" };
        worker.Start();
        worker.Join();
        return Task.CompletedTask;
    }
}

/// <summary>Assemblies that pile up: an XmlSerializer generated per call, and a plugin reloaded into a new load context each time.</summary>
public static class AssembliesScenario
{
    public static Task RunAsync()
    {
        for (var i = 0; i < 300; i++)
        {
            OrderFeedReader.Parse($"<orderFeed><Id>O-{i}</Id><Total>{i}.50</Total></orderFeed>");
        }

        var plugin = typeof(Microsoft.Diagnostics.NETCore.Client.DiagnosticsClient).Assembly.Location;
        for (var i = 0; i < 60; i++)
        {
            PluginHost.Reload(plugin);
        }

        return ScenarioCatalog.ReadyAndParkAsync();
    }
}

/// <summary>
/// SQL connection-pool exhaustion: export requests keep their connection open while they await a
/// PDF renderer that has stopped answering, a loyalty lookup leaks one on an early return, and
/// with all 20 pooled connections gone, customer requests block in <c>SqlConnection.Open</c>.
/// </summary>
public static class ConnectionPoolScenario
{
    public const int MaxPoolSize = 20;
    public const int Exports = 14;
    public const int Leaks = MaxPoolSize - Exports;
    public const int Waiting = 30;

    public static async Task RunAsync()
    {
        ThreadPool.SetMinThreads(64, 64);
        // The driver hosts the database; started directly (no driver), host it here.
        var port = int.TryParse(Environment.GetEnvironmentVariable(FakeSqlServer.PortVariable), out var p) ? p : FakeSqlServer.Start().Port;
        Db.ConnectionString =
            $"Server=tcp:127.0.0.1,{port};Database=Orders;User ID=billing;Password=not-a-secret;Encrypt=False;Max Pool Size={MaxPoolSize};Connect Timeout=600;Application Name=Contoso.Invoicing";

        for (var i = 0; i < Leaks; i++)
        {
            LoyaltyPoints.Balance($"CLOSED-{i:D3}");
        }

        var exporter = new InvoiceExporter();
        var customers = new CustomerDirectory();
        var (_, client) = await WebHost.StartAsync(app =>
        {
            app.MapPost("/api/invoices/{id}/export", (string id) => exporter.ExportAsync(id));
            app.MapGet("/api/customers/{id}/orders", (string id) => customers.CountOrders(id));
        });

        for (var i = 0; i < Exports; i++)
        {
            WebHost.Fire(client, HttpMethod.Post, $"/api/invoices/INV-{i:D4}/export");
        }

        await Task.Delay(1500);
        for (var i = 0; i < Waiting; i++)
        {
            WebHost.Fire(client, HttpMethod.Get, $"/api/customers/C-{i:D3}/orders");
        }

        await Task.Delay(2500);
        await ScenarioCatalog.ReadyAndParkAsync();
    }
}
