using System.Collections.Concurrent;

namespace Contoso.Catalog
{
    public sealed class CatalogService
    {
        // Stands in for a connection pool with no free connections: nobody ever releases it.
        private readonly SemaphoreSlim _connections = new(0);

        public static void RefreshIndex() => Thread.Sleep(10);

        public string[] Search(string term)
        {
            _connections.Wait();
            return [term];
        }
    }
}

namespace Contoso.Payments
{
    public sealed class PaymentGateway
    {
        public void Authorize(string cartId, decimal amount) =>
            throw new TimeoutException($"Payment gateway did not answer within 30 s for cart {cartId}.");
    }

    public sealed class PaymentService(PaymentGateway gateway)
    {
        public bool Charge(string cartId, decimal amount)
        {
            try
            {
                gateway.Authorize(cartId, amount);
                return true;
            }
            catch (TimeoutException ex)
            {
                RetryPolicy.Backoff(ex);
                return false;
            }
        }
    }

    public static class RetryPolicy
    {
        /// <summary>The bug: an unbounded back-off that parks the request thread forever.</summary>
        public static void Backoff(Exception cause) => Thread.Sleep(Timeout.Infinite);
    }

    public static class ErrorLog
    {
        private static readonly List<Exception> s_recent = [];

        public static void Record(Exception ex) => s_recent.Add(ex);
    }
}

namespace Contoso.Data
{
    public static class LegacyConnectionPool
    {
        public static readonly object Gate = new();
    }

    public sealed class LegacyConnection(int id)
    {
        private readonly int _id = id;

        ~LegacyConnection()
        {
            // Returning the handle to the pool takes the pool lock: if anything holds it, the finalizer thread waits.
            lock (LegacyConnectionPool.Gate)
            {
                _ = _id;
            }
        }
    }
}

namespace Contoso.Media
{
    public static class ThumbnailCache
    {
        private static readonly Dictionary<string, byte[]> s_thumbnails = [];

        public static void Keep(string key, byte[] image) => s_thumbnails[key] = image;
    }
}

namespace Contoso.Rates
{
    public sealed class RatesClient(Uri baseAddress)
    {
        /// <summary>The bug: a new HttpClient (handler, connection pool, socket) for every call.</summary>
        public async Task<string> GetRateAsync(string currency)
        {
            var http = new HttpClient { BaseAddress = baseAddress, Timeout = Timeout.InfiniteTimeSpan };
            return await http.GetStringAsync($"/downstream/rates/{currency}");
        }
    }
}

namespace Contoso.Sessions
{
    public sealed class SessionCache
    {
        private readonly ConcurrentDictionary<string, Timer> _expiryTimers = new();

        /// <summary>The bug: a timer per session, never disposed or removed.</summary>
        public void Touch(string sessionId) =>
            _expiryTimers[sessionId] = new Timer(_ => Expire(sessionId), null, TimeSpan.FromMinutes(20), Timeout.InfiniteTimeSpan);

        private void Expire(string sessionId) => _ = sessionId;
    }
}

namespace Contoso.Billing
{
    public static class InvoiceExporter
    {
        private static readonly Dictionary<string, string> s_templates = [];

        public static void ExportAll()
        {
            foreach (var customer in new[] { "C-1001", "C-1002" })
            {
                Render(customer);
            }
        }

        private static string Render(string customer) =>
            s_templates.TryGetValue("invoice", out var template)
                ? template.Replace("{customer}", customer)
                : throw new InvalidOperationException($"No invoice template is configured (customer {customer}).");
    }
}

namespace Contoso.Products
{
    public sealed class ProductRow(int id, string category, string description)
    {
        public int Id { get; } = id;

        public string Category { get; } = category;

        public string Description { get; } = description;
    }

    public static class ProductCatalog
    {
        private static readonly string[] Categories = ["Outdoor footwear", "Rainwear and shells", "Camping and shelter", "Climbing hardware", "Travel luggage"];

        private static readonly string[] Descriptions =
        [
            "Waterproof full-grain leather boot with a cushioned midsole and a lugged rubber outsole for wet, uneven ground.",
            "Three-layer breathable shell with taped seams, pit zips and a helmet-compatible hood for sustained rain.",
            "Two-person, three-season tent with aluminium poles, a full-coverage fly and two vestibules for gear storage.",
            "Lightweight hot-forged aluminium carabiner with a keylock nose and a screw gate, rated for single-pitch use.",
            "Expandable hard-shell cabin case with four spinner wheels, a telescopic handle and an integrated TSA lock.",
            "Insulated stainless-steel bottle that keeps drinks cold for twenty-four hours and fits standard cup holders.",
            "Merino-blend base layer with flatlock seams that wicks moisture and resists odour over multi-day trips.",
            "Adjustable trekking poles in carbon fibre with cork grips, quick-lock levers and interchangeable baskets.",
        ];

        private static readonly List<ProductRow> s_rows = [];

        public static void Load(int count)
        {
            for (var i = 0; i < count; i++)
            {
                s_rows.Add(new ProductRow(i, ReadColumn(Categories[i % Categories.Length]), ReadColumn(Descriptions[i % Descriptions.Length])));
            }
        }

        /// <summary>The bug: like a data reader, this hands back a brand-new string for every value, even when the text repeats.</summary>
        private static string ReadColumn(string value) => new(value.AsSpan());
    }
}

namespace Contoso.Notifications
{
    public static class PriceFeed
    {
        public static event EventHandler<decimal>? PriceChanged;

        public static void Publish(decimal price) => PriceChanged?.Invoke(null, price);
    }

    public sealed class PriceTicker
    {
        private readonly string _sku;
        private decimal _last;

        /// <summary>The bug: subscribes for life. Nothing ever unsubscribes, so the static event keeps every ticker.</summary>
        public PriceTicker(string sku)
        {
            _sku = sku;
            PriceFeed.PriceChanged += OnPriceChanged;
        }

        private void OnPriceChanged(object? sender, decimal price) => _last = _sku.Length > 0 ? price : _last;
    }
}

namespace Contoso.Imaging
{
    public sealed class FrameBuffer
    {
        public FrameBuffer(int size)
        {
            Pixels = new byte[size];
            Handle = System.Runtime.InteropServices.GCHandle.Alloc(Pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        }

        public byte[] Pixels { get; }

        public System.Runtime.InteropServices.GCHandle Handle { get; }
    }

    public static class FrameBufferPool
    {
        private static readonly List<FrameBuffer> s_buffers = [];

        /// <summary>The bug: buffers are pinned for the native encoder and the handle is never freed.</summary>
        public static FrameBuffer Rent(int size)
        {
            var buffer = new FrameBuffer(size);
            s_buffers.Add(buffer);
            return buffer;
        }
    }
}

namespace Contoso.Devices
{
    public sealed class DeviceListener(int id)
    {
        private readonly SemaphoreSlim _messages = new(0);

        public void Start() => new Thread(Listen) { IsBackground = true, Name = $"Device-{id}" }.Start();

        /// <summary>The bug: a whole thread per device, parked here until that device sends something.</summary>
        private void Listen()
        {
            while (true)
            {
                _messages.Wait();
            }
        }
    }

    public static class DeviceHub
    {
        private static readonly List<DeviceListener> s_listeners = [];

        public static void Connect(int devices)
        {
            for (var i = 0; i < devices; i++)
            {
                var listener = new DeviceListener(i);
                s_listeners.Add(listener);
                listener.Start();
            }
        }
    }
}
