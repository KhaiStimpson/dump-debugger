namespace Contoso.Orders;

public readonly record struct Sku(string Code);

public sealed record Order(string Id, Sku Sku, int Quantity);

public sealed record LedgerEntry(string OrderId, string Kind);

public sealed record StockItem(Sku Sku, int OnHand);

/// <summary>Lets the first checkout and the first reservation both get inside their first lock before either goes for its second.</summary>
public static class Rendezvous
{
    private static readonly Barrier Both = new(2);
    private static readonly ManualResetEventSlim Inside = new();
    private static int _arrivals;

    public static void Arrive()
    {
        if (Interlocked.Increment(ref _arrivals) <= 2)
        {
            Both.SignalAndWait();
            Inside.Set();
            Thread.Sleep(100);
        }
    }

    public static void WaitUntilBothInside() => Inside.Wait();
}

public sealed class OrderLedger
{
    private readonly object _gate = new();
    private readonly List<LedgerEntry> _entries = [];

    internal object Gate => _gate;

    public void Append(LedgerEntry entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
        }
    }

    internal void AppendUnlocked(LedgerEntry entry) => _entries.Add(entry);
}

public sealed class InventoryCache
{
    private readonly object _sync = new();
    private readonly Dictionary<Sku, StockItem> _items = new() { [new Sku("SKU-1001")] = new StockItem(new Sku("SKU-1001"), 40) };

    internal object Sync => _sync;

    public StockItem? TryGet(Sku sku)
    {
        lock (_sync)
        {
            return _items.GetValueOrDefault(sku);
        }
    }

    public bool TryReserve(Sku sku, int quantity)
    {
        lock (_sync)
        {
            return ReserveUnlocked(sku, quantity);
        }
    }

    internal bool ReserveUnlocked(Sku sku, int quantity)
    {
        if (!_items.TryGetValue(sku, out var item) || item.OnHand < quantity)
        {
            return false;
        }

        _items[sku] = item with { OnHand = item.OnHand - quantity };
        return true;
    }
}

public sealed class CheckoutService(OrderLedger ledger, InventoryCache inventory)
{
    /// <summary>Takes the ledger gate, then (via TryReserve) the inventory lock.</summary>
    public bool Commit(Order order)
    {
        lock (ledger.Gate)
        {
            ledger.AppendUnlocked(new LedgerEntry(order.Id, "checkout"));
            Rendezvous.Arrive();
            return inventory.TryReserve(order.Sku, order.Quantity);
        }
    }
}

public sealed class InventoryService(OrderLedger ledger, InventoryCache inventory)
{
    /// <summary>Takes the inventory lock, then (via Append) the ledger gate: the opposite order.</summary>
    public bool Reserve(Sku sku, int quantity)
    {
        lock (inventory.Sync)
        {
            var ok = inventory.ReserveUnlocked(sku, quantity);
            Rendezvous.Arrive();
            ledger.Append(new LedgerEntry(sku.Code, "reserve"));
            return ok;
        }
    }
}
