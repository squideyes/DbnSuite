namespace DbnSuite.Client.Tape;

/// <summary>The top of a book. A side with no orders has size 0, and its price means nothing.</summary>
internal readonly record struct BookTop(int BidPrice, uint BidSize, int AskPrice, uint AskSize);

/// <summary>
/// One instrument's book in whole ticks. It follows the same rules as DbnClient's
/// OrderBook, which is right for exports but slower here. Levels near the market sit on
/// flat arrays around the first price seen. A stray order far away, such as a fat-fingered
/// offer at a thousand times the market, sits in a small sorted table beside them.
/// </summary>
internal sealed class TickBook
{
    private const int Levels = 1 << 18;

    private readonly Dictionary<ulong, Order> orders = [];
    private readonly long[] bids = new long[Levels];
    private readonly long[] asks = new long[Levels];
    private readonly SortedDictionary<int, long> farBids = new(Comparer<int>.Create((a, b) => b.CompareTo(a)));
    private readonly SortedDictionary<int, long> farAsks = [];

    private long origin;
    private bool placed;
    private int bestBid = -1;
    private int bestAsk = Levels;

    /// <summary>Applies one event. Trades and no-ops leave the book as it is.</summary>
    /// <param name="price">The price in ticks, or null when the record has none.</param>
    public void Apply(char action, ulong orderId, int? price, uint size, char side)
    {
        switch (action)
        {
            case 'R':
                Clear();
                break;

            case 'A' or 'M' when price is { } at && side is 'B' or 'A':
                Remove(orderId);

                orders[orderId] = new Order(side, at, size);
                Change(side, at, size);
                break;

            case 'C' or 'F':
                Reduce(orderId, size);
                break;
        }
    }

    public BookTop Top()
    {
        var (bid, bidSize) = Best(bestBid >= 0, bestBid, bids, farBids, higherWins: true);
        var (ask, askSize) = Best(bestAsk < Levels, bestAsk, asks, farAsks, higherWins: false);

        return new BookTop(bid, bidSize, ask, askSize);
    }

    private (int Price, uint Size) Best(bool hasNear, int index, long[] levels, SortedDictionary<int, long> far, bool higherWins)
    {
        var near = hasNear ? (Price: (int)(origin + index), Size: (uint)levels[index]) : default;

        if (far.Count == 0)
            return near;

        var (price, size) = far.First();

        return !hasNear || (higherWins ? price > near.Price : price < near.Price) ? (price, (uint)size) : near;
    }

    private void Clear()
    {
        orders.Clear();
        farBids.Clear();
        farAsks.Clear();
        Array.Clear(bids);
        Array.Clear(asks);
        bestBid = -1;
        bestAsk = Levels;
    }

    private void Reduce(ulong orderId, uint size)
    {
        if (!orders.TryGetValue(orderId, out var order))
            return;

        var left = order.Size - size;

        if (left <= 0)
        {
            Remove(orderId);
            return;
        }

        orders[orderId] = order with { Size = left };
        Change(order.Side, order.Price, -size);
    }

    private void Remove(ulong orderId)
    {
        if (orders.Remove(orderId, out var order))
            Change(order.Side, order.Price, -order.Size);
    }

    private void Change(char side, int price, long size)
    {
        if (!placed)
        {
            placed = true;
            origin = (long)price - Levels / 2;
        }

        var index = price - origin;

        if (index is < 0 or >= Levels)
            ChangeFar(side == 'B' ? farBids : farAsks, price, size);
        else if (side == 'B')
            ChangeBid((int)index, size);
        else
            ChangeAsk((int)index, size);
    }

    private void ChangeBid(int index, long size)
    {
        bids[index] += size;

        if (bids[index] > 0 && index > bestBid)
            bestBid = index;

        while (bestBid >= 0 && bids[bestBid] <= 0)
            bestBid--;
    }

    private void ChangeAsk(int index, long size)
    {
        asks[index] += size;

        if (asks[index] > 0 && index < bestAsk)
            bestAsk = index;

        while (bestAsk < Levels && asks[bestAsk] <= 0)
            bestAsk++;
    }

    private static void ChangeFar(SortedDictionary<int, long> far, int price, long size)
    {
        var total = far.GetValueOrDefault(price) + size;

        if (total > 0)
            far[price] = total;
        else
            far.Remove(price);
    }

    private readonly record struct Order(char Side, int Price, long Size);
}
