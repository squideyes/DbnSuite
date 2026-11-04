namespace DbnClient.Export;

/// <summary>What a column holds, which decides how each format writes it.</summary>
public enum CellKind
{
    /// <summary>A DBN time: nanoseconds since the UNIX epoch, UTC.</summary>
    Time,

    /// <summary>A price, already in the units asked for.</summary>
    Price,

    /// <summary>A whole number: a size, a count, an id.</summary>
    Integer,

    Text
}

public sealed record Column(string Name, CellKind Kind);

/// <summary>One value of a row. Times and integers use <see cref="Number"/>.</summary>
public readonly record struct Cell(long Number, decimal? Price, string? Text)
{
    public static Cell Of(long number) => new(number, null, null);

    public static Cell Of(ulong nanos) => new((long)nanos, null, null);

    public static Cell Of(decimal? price) => new(0, price, null);

    public static Cell Of(string? text) => new(0, null, text);

    /// <summary>An undefined time, size or price: an empty cell.</summary>
    public static Cell Empty => new(long.MinValue, null, null);

    public bool IsEmpty => Number == long.MinValue && Price is null && Text is null;
}

/// <summary>How prices are written.</summary>
public enum PriceMode
{
    /// <summary>Exact decimals, such as 6876.25.</summary>
    Decimal,

    /// <summary>Whole ticks of the contract, such as 27505 for 6876.25 at 0.25.</summary>
    Ticks,

    /// <summary>Databento's own integers, in units of 1e-9.</summary>
    Raw
}

/// <summary>How times are written.</summary>
public enum TimeMode
{
    /// <summary>ISO 8601 in the chosen zone, with nine digits and the offset.</summary>
    Iso,

    UnixNanos,
    UnixMillis,

    /// <summary>An Excel serial date in the chosen zone.</summary>
    Excel
}
