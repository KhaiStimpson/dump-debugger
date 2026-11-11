using System.Text;
using Contoso.Orders;
using Contoso.Reports;

namespace Contoso.Scenarios;

/// <summary>
/// Memory fixtures, as a pair taken hours apart from the same app. Both hold rendered PDFs in a
/// static <c>ReportCache</c> that never evicts. Between baseline and current,
/// <c>SessionStore.s_snapshots</c> (a static list of cart snapshots nobody removes) grows ~24×
/// and one more large report is cached: the leak Compare should flag.
/// </summary>
public static class LeakScenario
{
    public static Task RunCurrentAsync() => RunAsync(snapshots: 120_000, reports: [("Q3-statements", 120), ("Q3-invoices-EU", 90), ("Q3-invoices-US", 60)]);

    public static Task RunBaselineAsync() => RunAsync(snapshots: 5_000, reports: [("Q3-statements", 120), ("Q3-invoices-EU", 90)]);

    private static Task RunAsync(int snapshots, (string Key, int MegaBytes)[] reports)
    {
        foreach (var (key, mb) in reports)
        {
            ReportCache.Put(key, RenderPdf(key, mb * 1024 * 1024));
        }

        for (var i = 0; i < snapshots; i++)
        {
            SessionStore.Capture(new CartSnapshot(
                $"C-{100_000 + i}",
                DateTime.UtcNow.AddSeconds(-i),
                [new CartLine(new Sku($"SKU-{i % 977}"), 1 + (i % 3), 9.99m), new CartLine(new Sku("SKU-1001"), 1, 24.50m)]));
        }

        GC.Collect();
        return ScenarioCatalog.ReadyAndParkAsync();
    }

    /// <summary>A buffer that starts like a real PDF and isn't all zeros (so it is committed and dumped).</summary>
    private static byte[] RenderPdf(string title, int size)
    {
        var buffer = new byte[size];
        var header = Encoding.ASCII.GetBytes($"%PDF-1.7\n%{title}\n");
        header.CopyTo(buffer, 0);
        var rng = new Random(title.GetHashCode());
        for (var i = header.Length; i < size; i += 4096)
        {
            buffer[i] = (byte)rng.Next(256);
        }

        return buffer;
    }
}
