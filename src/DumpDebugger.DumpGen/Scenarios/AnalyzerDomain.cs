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
