using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using System.Xml.Serialization;

namespace Contoso.Warehouse
{
    public sealed class InventoryService
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private int _stock = 3;

        public async Task<bool> ReserveAsync(string sku, int quantity)
        {
            await _gate.WaitAsync();
            try
            {
                if (_stock < quantity)
                {
                    // The bug: RestockAsync takes the same gate, and SemaphoreSlim isn't re-entrant.
                    await RestockAsync(sku);
                }

                _stock -= quantity;
                return true;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task RestockAsync(string sku)
        {
            await _gate.WaitAsync();
            try
            {
                await Task.Delay(50);
                _stock += sku.Length * 100;
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    public sealed class CarrierClient
    {
        /// <summary>The bug: the callback is never stored, so nothing can ever call it.</summary>
        public void RequestLabel(string orderId, Action<string> onReady) => _ = orderId.Length + onReady.GetHashCode();
    }

    public sealed class ShipmentTracker(CarrierClient carrier)
    {
        public async Task<string> PrintLabelAsync(string orderId)
        {
            var label = await WaitForLabelAsync(orderId);
            return $"{orderId}: {label}";
        }

        private Task<string> WaitForLabelAsync(string orderId)
        {
            var ready = new TaskCompletionSource<string>();
            carrier.RequestLabel(orderId, label => ready.TrySetResult(label));
            return ready.Task;
        }
    }

    /// <summary>Work started and not awaited by its caller; kept so failures can be logged when it finishes.</summary>
    public static class BackgroundJobs
    {
        private static readonly List<Task> s_running = [];

        public static void Track(Task job)
        {
            lock (s_running)
            {
                s_running.Add(job);
            }
        }
    }
}

namespace Contoso.Import
{
    public static class SkuValidator
    {
        // The bug: a quantifier inside a quantifier. A long SKU that fails at its last character
        // makes the engine try every way of splitting it into groups.
        private static readonly Regex s_format = new(@"^([A-Z0-9]+-?)+$");

        public static bool IsValid(string sku) => s_format.IsMatch(sku);
    }

    public static class ImportJob
    {
        public static int Run(string[] rows)
        {
            var valid = 0;
            foreach (var row in rows)
            {
                if (SkuValidator.IsValid(row))
                {
                    valid++;
                }
            }

            return valid;
        }
    }

    public static class SalesRollup
    {
        private static volatile bool s_stop;

        public static void Cancel() => s_stop = true;

        /// <summary>The bug: nothing ever calls Cancel, so the loop never exits.</summary>
        public static long Recompute(int[] amounts)
        {
            long total = 0;
            while (!s_stop)
            {
                for (var i = 0; i < amounts.Length; i++)
                {
                    total = unchecked((total * 31) + amounts[i]);
                }
            }

            return total;
        }
    }
}

namespace Contoso.Org
{
    public sealed class Department(string name)
    {
        public string Name { get; } = name;

        public Department? Parent { get; set; }
    }

    public static class OrgChart
    {
        /// <summary>The bug: nothing stops this when a department is (indirectly) its own parent.</summary>
        public static string PathOf(Department department) =>
            department.Parent is null ? department.Name : PathOf(department.Parent) + "/" + department.Name;
    }

    public static class OrgSync
    {
        public static void Publish()
        {
            var sales = new Department("Sales");
            var emea = new Department("EMEA") { Parent = sales };
            sales.Parent = emea; // bad data: a cycle
            Console.WriteLine(OrgChart.PathOf(emea));
        }
    }
}

namespace Contoso.Ledger
{
    public static class LedgerGuard
    {
        public static void EnsureBalanced(decimal debits, decimal credits)
        {
            if (debits != credits)
            {
                Environment.FailFast($"Ledger out of balance: debits {debits:F2}, credits {credits:F2}. Refusing to post.");
            }
        }
    }

    public static class PostingService
    {
        public static void PostBatch(string batchId)
        {
            _ = batchId;
            LedgerGuard.EnsureBalanced(1200.00m, 1150.00m);
        }
    }
}

namespace Contoso.Feeds
{
    public sealed class OrderFeed
    {
        public string? Id { get; set; }

        public decimal Total { get; set; }
    }

    public static class OrderFeedReader
    {
        /// <summary>The bug: this XmlSerializer constructor isn't cached, so every call generates a new assembly.</summary>
        public static OrderFeed Parse(string xml)
        {
            var serializer = new XmlSerializer(typeof(OrderFeed), new XmlRootAttribute("orderFeed"));
            using var reader = new StringReader(xml);
            return (OrderFeed)serializer.Deserialize(reader)!;
        }
    }
}

namespace Contoso.Plugins
{
    public sealed class PluginLoadContext() : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName) => null;
    }

    public static class PluginHost
    {
        private static readonly List<Assembly> s_versions = [];

        /// <summary>The bug: every reload loads the plugin into a new context and keeps the old ones referenced, so none can unload.</summary>
        public static void Reload(string pluginPath) =>
            s_versions.Add(new PluginLoadContext().LoadFromAssemblyPath(pluginPath));
    }
}
