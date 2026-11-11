using Contoso.Orders;

namespace Contoso.Scenarios;

/// <summary>
/// Performance fixture, not a problem scenario: a ~2 GB managed heap of millions of small
/// objects (the expensive shape for heap walks and root searches; a few huge byte[] would be
/// trivially cheap). Used to measure the "root path on a 2 GB heap under 10 s" target.
/// </summary>
public static class BigHeapScenario
{
    private static readonly List<Order> Archive = [];

    public static Task RunAsync()
    {
        const int orders = 4_000_000;
        Archive.Capacity = orders;
        for (var i = 0; i < orders; i++)
        {
            Archive.Add(new Order($"A-{i:D10}-{new string((char)('a' + (i % 26)), 200)}", new Sku($"SKU-{i % 5000}"), 1 + (i % 7)));
        }

        GC.Collect();
        return ScenarioCatalog.ReadyAndParkAsync();
    }
}
