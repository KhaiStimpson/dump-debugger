using Contoso.Orders;
using Microsoft.AspNetCore.Builder;

namespace Contoso.Scenarios;

/// <summary>
/// Sync-over-async at scale: 200 concurrent <c>GET /api/pricing/quote</c> requests each block a
/// pool thread on <c>.Result</c> of an HttpClient call to a downstream that never answers. The
/// pool is pre-grown so all 200 threads exist (this is also the 200-thread fixture the Threads
/// view is measured against).
/// </summary>
public static class SyncOverAsyncScenario
{
    public const int Requests = 200;

    public static async Task RunAsync()
    {
        ThreadPool.SetMinThreads(Requests + 40, Requests + 40);
        PricingClient? pricing = null;

        var (_, client) = await WebHost.StartAsync(app =>
        {
            app.MapGet("/api/pricing/quote", () => pricing!.GetQuote(new Cart("C-1", 3)));
            app.MapGet("/downstream/rates", async () =>
            {
                await Task.Delay(Timeout.Infinite);
                return "never";
            });
            app.MapGet("/health", () => "ok");
        });

        pricing = new PricingClient(new HttpClient { BaseAddress = client.BaseAddress, Timeout = Timeout.InfiniteTimeSpan });

        for (var i = 0; i < Requests; i++)
        {
            WebHost.Fire(client, HttpMethod.Get, "/api/pricing/quote");
        }

        await client.GetStringAsync("/health");
        await Task.Delay(4000);
        await ScenarioCatalog.ReadyAndParkAsync();
    }
}
