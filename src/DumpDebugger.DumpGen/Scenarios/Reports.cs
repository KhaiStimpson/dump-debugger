using Contoso.Orders;

namespace Contoso.Reports
{
    public sealed class RenderOptions
    {
        public string Locale { get; init; } = "en-US";

        public bool Landscape { get; init; }
    }

    public sealed class CachedReport(string key, byte[] pdf)
    {
        public string Key { get; } = key;

        public byte[] Pdf { get; } = pdf;

        public DateTime RenderedAt { get; } = new(2026, 11, 9, 6, 0, 12, DateTimeKind.Utc);

        public int HitCount { get; set; } = 3;

        public RenderOptions Options { get; } = new();

        // The bug the Objects view should surface: nothing ever sets this.
        private readonly TimeSpan? _expiry = null;

        public bool IsExpired(DateTime now) => _expiry is { } e && now - RenderedAt > e;
    }

    /// <summary>Rendered reports, cached forever: there's no eviction.</summary>
    public static class ReportCache
    {
        private static readonly Dictionary<string, CachedReport> s_entries = [];

        public static void Put(string key, byte[] pdf) => s_entries[key] = new CachedReport(key, pdf);

        public static int Count => s_entries.Count;
    }
}

namespace Contoso.Orders
{
    public sealed record CartLine(Sku Sku, int Quantity, decimal Price);

    public sealed record CartSnapshot(string CartId, DateTime TakenAt, List<CartLine> Lines);

    /// <summary>Snapshots for "resume your cart": added on every page view, never removed.</summary>
    public static class SessionStore
    {
        private static readonly List<CartSnapshot> s_snapshots = [];

        public static void Capture(CartSnapshot snapshot) => s_snapshots.Add(snapshot);

        public static int Count => s_snapshots.Count;
    }
}
