namespace DbnSuite.Client.Tape;

/// <summary>Encodes every trade of one contract.</summary>
public sealed class TradeWriter(TapeInfo info) : TapeWriter(TapeKind.Trade, info)
{
    private const byte GapBit = 4;
    private const byte PriceBit = 8;
    private const int SizeShift = 4;
    private const uint LargestInlineSize = 15;

    private int lastPrice = info.BasePrice;

    /// <summary>Adds a trade. Time is in microseconds since the epoch.</summary>
    /// <returns>False once the writer has refused; see <see cref="TapeWriter.Refusal"/>.</returns>
    public bool Add(long time, int price, uint size, TradeSide side)
    {
        if (Refusal is not null || !InSequence(time))
            return false;

        if (side is not (TradeSide.None or TradeSide.Buyer or TradeSide.Seller))
            return Refuse($"Side {(byte)side} is not a side.");

        var gap = time - LastTime;
        var inlineSize = size is >= 1 and <= LargestInlineSize ? (int)size : 0;

        var control = (byte)(
            (byte)side
            | (gap != 0 ? GapBit : 0)
            | (price != lastPrice ? PriceBit : 0)
            | (inlineSize << SizeShift));

        BeginEvent(control);

        if (gap != 0)
            PutVarint((ulong)gap);

        if (price != lastPrice)
            PutZigzag((long)price - lastPrice);

        if (inlineSize == 0)
            PutVarint(size);

        LastTime = time;
        lastPrice = price;
        Checksum = TapeChecksum.Print(Checksum, time, price, size, side);

        return true;
    }

    private protected override RthMark Capture() => new() { Time = LastTime, LastPrice = lastPrice };
}
