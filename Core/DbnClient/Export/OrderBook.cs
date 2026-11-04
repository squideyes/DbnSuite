using DbnClient.Dbn;

namespace DbnClient.Export;

/// <summary>One price level: its price, the size resting there, and how many orders make it up.</summary>
public readonly record struct Level(decimal Price, long Size, int Count);

/// <summary>
/// One instrument's book, rebuilt from its <c>mbo</c> events. Each day's file
/// opens with a clear and a snapshot of every resting order, so a day replays
/// on its own.
/// </summary>
public sealed class OrderBook
{
    private readonly Dictionary<ulong, Order> orders = [];
    private readonly SortedDictionary<decimal, (long Size, int Count)> bids = new(Comparer<decimal>.Create((a, b) => b.CompareTo(a)));
    private readonly SortedDictionary<decimal, (long Size, int Count)> asks = new();

    /// <summary>Applies one event. Trades leave the book as it is; the fills that follow them change it.</summary>
    public void Apply(MboRecord m)
    {
        switch (m.Action)
        {
            case 'R':
                orders.Clear();
                bids.Clear();
                asks.Clear();
                break;

            case 'A' when m.Price is { } price && m.Side is 'B' or 'A':
                Remove(m.OrderId);
                orders[m.OrderId] = new Order(m.Side, price, m.Quantity);
                Change(m.Side, price, m.Quantity, 1);
                break;

            case 'M' when m.Price is { } price && m.Side is 'B' or 'A':
                // A modify can move an order or resize it; either way it is the order's new state.
                Remove(m.OrderId);
                orders[m.OrderId] = new Order(m.Side, price, m.Quantity);
                Change(m.Side, price, m.Quantity, 1);
                break;

            case 'C':
            case 'F':
                Reduce(m.OrderId, m.Quantity);
                break;
        }
    }

    /// <summary>The best <paramref name="depth"/> levels of a side, best first; missing levels are not returned.</summary>
    public IEnumerable<Level> Side(char side, int depth) =>
        (side == 'B' ? bids : asks).Take(depth).Select(l => new Level(l.Key, l.Value.Size, l.Value.Count));

    public Level? Best(char side)
    {
        foreach (var level in Side(side, 1))
            return level;

        return null;
    }

    private void Reduce(ulong orderId, uint quantity)
    {
        if (!orders.TryGetValue(orderId, out var order))
            return;

        var left = order.Size - quantity;

        if (left <= 0)
        {
            Remove(orderId);
            return;
        }

        orders[orderId] = order with { Size = left };
        Change(order.Side, order.Price, -quantity, 0);
    }

    private void Remove(ulong orderId)
    {
        if (!orders.Remove(orderId, out var order))
            return;

        Change(order.Side, order.Price, -order.Size, -1);
    }

    private void Change(char side, decimal price, long size, int count)
    {
        var levels = side == 'B' ? bids : asks;
        var (total, orderCount) = levels.GetValueOrDefault(price);

        total += size;
        orderCount += count;

        if (total <= 0 || orderCount <= 0)
            levels.Remove(price);
        else
            levels[price] = (total, orderCount);
    }

    private readonly record struct Order(char Side, decimal Price, long Size);
}
