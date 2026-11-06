namespace DbnClient.Tape;

/// <summary>Encodes one ordered stream of top-of-book changes, trades and market states.</summary>
public sealed class L1Writer(TapeInfo info) : TapeWriter(TapeKind.L1, info)
{
    private const byte TradeBit = 1;
    private const byte GapBit = 2;
    private const byte BidPriceBit = 4;
    private const byte AskPriceBit = 8;
    private const byte BidSizeBit = 16;
    private const byte AskSizeBit = 32;
    private const int SideShift = 2;
    private const byte ImpliedBit = 16;
    private const int SizeShift = 5;
    private const uint LargestInlineSize = 7;

    private int bidPrice = info.BasePrice;
    private int askPrice = info.BasePrice;
    private uint bidSize;
    private uint askSize;
    private int lastPrice = info.BasePrice;
    private byte marketState;

    /// <summary>
    /// Adds the book's top, if it changed. A side with no orders has size 0 and keeps its
    /// last price, because a price of 0 ticks is legal. Time is in microseconds since the epoch.
    /// </summary>
    /// <returns>False once the writer has refused; see <see cref="TapeWriter.Refusal"/>.</returns>
    public bool Quote(long time, int bid, uint bidLots, int ask, uint askLots)
    {
        if (Refusal is not null || !InSequence(time))
            return false;

        var newBid = bidLots == 0 ? bidPrice : bid;
        var newAsk = askLots == 0 ? askPrice : ask;

        var control = (byte)(
            (newBid != bidPrice ? BidPriceBit : 0)
            | (newAsk != askPrice ? AskPriceBit : 0)
            | (bidLots != bidSize ? BidSizeBit : 0)
            | (askLots != askSize ? AskSizeBit : 0));

        if (control == 0)
            return true;

        var gap = time - LastTime;

        BeginEvent((byte)(control | (gap != 0 ? GapBit : 0)));

        if (gap != 0)
            PutVarint((ulong)gap);

        if (newBid != bidPrice)
            PutZigzag((long)newBid - bidPrice);

        if (newAsk != askPrice)
            PutZigzag((long)newAsk - askPrice);

        if (bidLots != bidSize)
            PutSize(newBid != bidPrice, bidLots, bidSize);

        if (askLots != askSize)
            PutSize(newAsk != askPrice, askLots, askSize);

        LastTime = time;
        bidPrice = newBid;
        askPrice = newAsk;
        bidSize = bidLots;
        askSize = askLots;
        Checksum = TapeChecksum.Quote(Checksum, time, bidPrice, bidSize, askPrice, askSize);

        return true;
    }

    /// <summary>Adds a trade. Write it before the quote change it causes, as on the feed.</summary>
    /// <returns>False once the writer has refused; see <see cref="TapeWriter.Refusal"/>.</returns>
    public bool Trade(long time, int price, uint size, TradeSide side)
    {
        if (Refusal is not null || !InSequence(time))
            return false;

        if (side is not (TradeSide.None or TradeSide.Buyer or TradeSide.Seller))
            return Refuse($"Side {(byte)side} is not a side.");

        var gap = time - LastTime;
        var inlineSize = size is >= 1 and <= LargestInlineSize ? (int)size : 0;
        var implied = (side == TradeSide.Buyer && price == askPrice) || (side == TradeSide.Seller && price == bidPrice);

        var control = (byte)(
            TradeBit
            | (gap != 0 ? GapBit : 0)
            | ((byte)side << SideShift)
            | (implied ? ImpliedBit : 0)
            | (inlineSize << SizeShift));

        BeginEvent(control);

        if (gap != 0)
            PutVarint((ulong)gap);

        if (!implied)
            PutZigzag((long)price - lastPrice);

        if (inlineSize == 0)
            PutVarint(size);

        LastTime = time;
        lastPrice = price;
        Checksum = TapeChecksum.Trade(Checksum, time, price, size, side);

        return true;
    }

    /// <summary>Adds the market's status, if it changed. The value is Databento's own action code.</summary>
    /// <returns>False once the writer has refused; see <see cref="TapeWriter.Refusal"/>.</returns>
    public bool State(long time, byte action)
    {
        if (Refusal is not null || !InSequence(time))
            return false;

        if (action == 0)
            return Refuse("Action 0 means no change, so it cannot be a state.");

        if (action == marketState)
            return true;

        var gap = time - LastTime;

        BeginEvent(gap != 0 ? GapBit : (byte)0);

        if (gap != 0)
            PutVarint((ulong)gap);

        PutByte(action);

        LastTime = time;
        marketState = action;
        Checksum = TapeChecksum.State(Checksum, time, action);

        return true;
    }

    private protected override RthMark Capture() => new()
    {
        Time = LastTime,
        BidPrice = bidPrice,
        AskPrice = askPrice,
        BidSize = bidSize,
        AskSize = askSize,
        LastPrice = lastPrice,
        MarketState = marketState
    };

    private void PutSize(bool priceMoved, uint size, uint before)
    {
        if (priceMoved)
            PutVarint(size);
        else
            PutZigzag((long)size - before);
    }
}
