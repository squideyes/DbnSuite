namespace DbnClient.Tape;

/// <summary>What the caller states about a tape before writing it.</summary>
public sealed record TapeInfo
{
    public required string Dataset { get; init; }

    public required string Symbol { get; init; }

    public required string Root { get; init; }

    public required DateOnly TradeDate { get; init; }

    /// <summary>One tick in DBN's 1e-9 units: 250,000,000 for ES.</summary>
    public required long TickRaw { get; init; }

    /// <summary>This symbol's session open on this date, in nanoseconds since the UNIX epoch.</summary>
    public required long OpenTime { get; init; }

    /// <summary>This symbol's session close on this date, in nanoseconds since the UNIX epoch.</summary>
    public required long CloseTime { get; init; }

    /// <summary>Starting price in ticks.</summary>
    public required int BasePrice { get; init; }

    public required uint InstrumentId { get; init; }

    /// <summary>Wrap the body in zstd, for storage and network copies.</summary>
    public bool Compress { get; init; }
}
