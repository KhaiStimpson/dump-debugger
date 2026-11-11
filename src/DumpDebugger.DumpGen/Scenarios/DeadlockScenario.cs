using Contoso.Orders;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Contoso.Scenarios;

/// <summary>
/// The classic lock-order inversion, served over HTTP. <c>POST /api/orders/checkout</c> takes
/// <c>OrderLedger._gate</c> then <c>InventoryCache._sync</c>; <c>POST /api/inventory/reserve</c>
/// takes them the other way round. Once both are stuck, further ledger and inventory requests
/// queue behind the two held locks.
/// </summary>
public static class DeadlockScenario
{
    public static async Task RunAsync()
    {
        ThreadPool.SetMinThreads(64, 64);
        var ledger = new OrderLedger();
        var inventory = new InventoryCache();
        var checkout = new CheckoutService(ledger, inventory);
        var reservations = new InventoryService(ledger, inventory);

        var (_, client) = await WebHost.StartAsync(app =>
        {
            app.MapPost("/api/orders/checkout", () => checkout.Commit(new Order("A-88123", new Sku("SKU-1001"), 2)));
            app.MapPost("/api/inventory/reserve", () => reservations.Reserve(new Sku("SKU-1001"), 2));
            app.MapPost("/api/orders/{id}/notes", (string id) => ledger.Append(new LedgerEntry(id, "note")));
            app.MapGet("/api/inventory/{sku}", (string sku) => inventory.TryGet(new Sku(sku)) is { } item ? Results.Ok(item) : Results.NotFound());
            app.MapGet("/health", () => "ok");
        });

        WebHost.Fire(client, HttpMethod.Post, "/api/orders/checkout");
        WebHost.Fire(client, HttpMethod.Post, "/api/inventory/reserve");
        Rendezvous.WaitUntilBothInside();
        await Task.Delay(400);

        for (var i = 0; i < 6; i++)
        {
            WebHost.Fire(client, HttpMethod.Post, $"/api/orders/A-{88200 + i}/notes");
        }

        for (var i = 0; i < 4; i++)
        {
            WebHost.Fire(client, HttpMethod.Get, $"/api/inventory/SKU-{2000 + i}");
        }

        await client.GetStringAsync("/health");
        await Task.Delay(1500);
        await ScenarioCatalog.ReadyAndParkAsync();
    }
}
