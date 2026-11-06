namespace DbnClient.Tape;

/// <summary>
/// A place a cursor can begin: a position in the body, and the running state there.
/// Every event is a change from the one before, so a position alone is not enough.
/// </summary>
public readonly record struct RthMark
{
    public long BodyOffset { get; init; }

    public long EventIndex { get; init; }

    /// <summary>The last event's time, in the tape's time units.</summary>
    public long Time { get; init; }

    public int BidPrice { get; init; }

    public int AskPrice { get; init; }

    public uint BidSize { get; init; }

    public uint AskSize { get; init; }

    /// <summary>The last trade, or the last close on S1.</summary>
    public int LastPrice { get; init; }

    public byte MarketState { get; init; }

    /// <summary>The state before the first event, which comes from the header alone.</summary>
    internal static RthMark Opening(TapeKind kind, long openTime, int basePrice)
    {
        var unitTime = openTime / TapeHeader.TimeUnitOf(kind);

        return new RthMark
        {
            Time = kind == TapeKind.S1 ? unitTime - 1 : unitTime,
            BidPrice = basePrice,
            AskPrice = basePrice,
            LastPrice = basePrice
        };
    }
}
