using DbnClient.Contracts;
using DbnClient.Time;

namespace DbnClient.Export;

/// <summary>How output is split into files.</summary>
public enum FileLayout
{
    /// <summary>One file per trade date.</summary>
    PerDay,

    /// <summary>One file for the whole range.</summary>
    Single,

    /// <summary><c>series=ES.v.0/date=2026-10-28/</c> folders, which DuckDB and Spark read as one table.</summary>
    Partitioned
}

/// <summary>Everything <see cref="Exporter"/> needs to know about one export.</summary>
public sealed record ExportRequest
{
    public required string Schema { get; init; }
    public required SymbolSpec Symbol { get; init; }
    public required DateSpan TradeDates { get; init; }
    public required SessionFilter Sessions { get; init; }
    public required string Output { get; init; }

    public OutputFormat Format { get; init; } = OutputFormat.Csv;
    public Compression Compression { get; init; } = Compression.None;
    public FileLayout Layout { get; init; } = FileLayout.PerDay;

    /// <summary>A file name of your own, with {series}, {kind}, {date}, {from}, {to} and {ext}.</summary>
    public string? NameTemplate { get; init; }

    public TimeZoneInfo Zone { get; init; } = TimeZoneInfo.Utc;
    public TimeMode Time { get; init; } = TimeMode.Iso;
    public bool SplitTime { get; init; }
    public PriceMode Prices { get; init; } = PriceMode.Decimal;

    /// <summary>Only these columns, in this order; all of them when empty.</summary>
    public IReadOnlyList<string> Columns { get; init; } = [];

    public BarSpec? Bars { get; init; }

    /// <summary>What mbo becomes: trades, tbbo, mbp-1, mbp-10, bbo-1s or bbo-1m.</summary>
    public string? As { get; init; }

    /// <summary>For a continuous series.</summary>
    public Adjustment Adjustment { get; init; } = Adjustment.None;
    public int RollDays { get; init; } = RollSchedule.DefaultRollDays;

    /// <summary>For a root: keep spreads as well as outright contracts.</summary>
    public bool Spreads { get; init; } = true;
}

/// <summary>What an export did.</summary>
public sealed record ExportSummary(IReadOnlyList<string> Files, long Rows, IReadOnlyList<DateOnly> MissingDays,
    IReadOnlyList<Roll> Rolls);
