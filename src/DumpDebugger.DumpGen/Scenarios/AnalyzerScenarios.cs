using Contoso.Catalog;
using Contoso.Data;
using Contoso.Media;
using Contoso.Rates;
using Contoso.Payments;
using Contoso.Sessions;
using Microsoft.AspNetCore.Builder;

namespace Contoso.Scenarios;

/// <summary>Thread-pool starvation: every worker is blocked on a gate that never opens, and new requests queue up behind them.</summary>
public static class ThreadPoolScenario
{
    public const int Workers = 24;

    public static async Task RunAsync()
    {
        // A pool that can't grow past 24 workers stands in for one that can't grow fast enough.
        ThreadPool.SetMinThreads(Workers, Workers);
        ThreadPool.SetMaxThreads(Workers, Workers);
        var catalog = new CatalogService();
        var (_, client) = await WebHost.StartAsync(app =>
        {
            app.MapGet("/api/catalog/search", () => catalog.Search("boots"));
        });

        for (var i = 0; i < 120; i++)
        {
            WebHost.Fire(client, HttpMethod.Get, "/api/catalog/search");
        }

        // Synchronous from here on: the pool is about to have no free threads for continuations.
        Thread.Sleep(2000);

        // Background work (cache refresh, telemetry flush) queued from a non-pool thread: it can't start.
        var producer = new Thread(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                ThreadPool.QueueUserWorkItem(_ => CatalogService.RefreshIndex());
            }
        });
        producer.Start();
        producer.Join();
        Thread.Sleep(1000);
        await ScenarioCatalog.ReadyAndParkAsync();
    }
}

/// <summary>Requests stuck in a retry back-off inside catch blocks (exceptions in flight), plus an exception storm kept alive by an error log.</summary>
public static class ExceptionsScenario
{
    public static async Task RunAsync()
    {
        ThreadPool.SetMinThreads(64, 64);
        var payments = new PaymentService(new PaymentGateway());
        var (_, client) = await WebHost.StartAsync(app =>
        {
            app.MapPost("/api/payments/charge", () => payments.Charge("C-1", 42.50m));
        });

        for (var i = 0; i < 2_000; i++)
        {
            try
            {
                throw new InvalidOperationException($"Cart C-{i % 50} is locked by another checkout.");
            }
            catch (InvalidOperationException ex)
            {
                ErrorLog.Record(ex);
            }
        }

        for (var i = 0; i < 5; i++)
        {
            WebHost.Fire(client, HttpMethod.Post, "/api/payments/charge");
        }

        await Task.Delay(2500);
        await ScenarioCatalog.ReadyAndParkAsync();
    }
}

/// <summary>The finalizer thread is blocked on a lock another thread never releases, so finalizable objects pile up.</summary>
public static class FinalizerScenario
{
    public static Task RunAsync()
    {
        var holder = new Thread(() =>
        {
            lock (LegacyConnectionPool.Gate)
            {
                Thread.Sleep(Timeout.Infinite);
            }
        }) { IsBackground = true, Name = "PoolMaintenance" };
        holder.Start();
        Thread.Sleep(200);

        for (var i = 0; i < 50_000; i++)
        {
            _ = new LegacyConnection(i);
        }

        GC.Collect();
        Thread.Sleep(1500);
        return ScenarioCatalog.ReadyAndParkAsync();
    }
}

/// <summary>Large Object Heap fragmentation: alternating 1 MB buffers are kept and dropped, leaving holes the LOH doesn't compact by default.</summary>
public static class LohScenario
{
    public static Task RunAsync()
    {
        // Allocate everything first (so the GC can't recycle holes as we go), then let every
        // other buffer go: 200 one-megabyte holes between live buffers.
        var all = new List<byte[]>();
        for (var i = 0; i < 400; i++)
        {
            var buffer = new byte[1024 * 1024];
            buffer[0] = (byte)i;
            all.Add(buffer);
        }

        for (var i = 0; i < all.Count; i += 2)
        {
            ThumbnailCache.Keep($"thumb-{i}", all[i]);
        }

        all.Clear();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
        return ScenarioCatalog.ReadyAndParkAsync();
    }
}

/// <summary>A new HttpClient per request: 200 in-flight calls each own a client, handler, connection pool and socket.</summary>
public static class HttpClientScenario
{
    public static async Task RunAsync()
    {
        ThreadPool.SetMinThreads(64, 64);
        Uri? baseAddress = null;
        var (_, client) = await WebHost.StartAsync(app =>
        {
            app.MapGet("/api/quote", async () => await new RatesClient(baseAddress!).GetRateAsync("EUR"));
            app.MapGet("/downstream/rates/{ccy}", async (string ccy) =>
            {
                await Task.Delay(Timeout.Infinite);
                return ccy;
            });
        });
        baseAddress = client.BaseAddress;

        for (var i = 0; i < 200; i++)
        {
            WebHost.Fire(client, HttpMethod.Get, "/api/quote");
        }

        await Task.Delay(4000);
        await ScenarioCatalog.ReadyAndParkAsync();
    }
}

/// <summary>Timer leak: a timer per session, kept in a dictionary that nothing ever cleans.</summary>
public static class TimersScenario
{
    public static Task RunAsync()
    {
        var sessions = new SessionCache();
        for (var i = 0; i < 5_000; i++)
        {
            sessions.Touch($"S-{i:D6}");
        }

        return ScenarioCatalog.ReadyAndParkAsync();
    }
}

/// <summary>A real crash: an unhandled exception on a background thread terminates the process, and the runtime writes the dump.</summary>
public static class CrashScenario
{
    public static Task RunAsync()
    {
        var worker = new Thread(() => Contoso.Billing.InvoiceExporter.ExportAll()) { IsBackground = true, Name = "InvoiceExport" };
        worker.Start();
        worker.Join();
        return Task.CompletedTask;
    }
}
