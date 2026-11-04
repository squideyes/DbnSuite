using System.Globalization;
using System.Text.RegularExpressions;
using DbnClient.Time;

namespace DbnClient.Export;

public enum BarUnit
{
    Seconds,
    Day,
    Week,
    Month,

    /// <summary>Every N trades.</summary>
    Ticks,

    /// <summary>Every N contracts traded.</summary>
    Volume
}

/// <summary>A bar size: <c>5s</c>, <c>1m</c>, <c>4h</c>, <c>1d</c>, <c>1w</c>, <c>1mo</c>, <c>500t</c> or <c>1000v</c>.</summary>
public sealed partial record BarSpec(BarUnit Unit, long Size, string Text)
{
    private const long SecondsInADay = 24 * 3600;

    /// <summary>Activity bars count trades, so they come from <c>mbo</c>.</summary>
    public bool FromTrades => Unit is BarUnit.Ticks or BarUnit.Volume;

    public long Nanos => Size * 1_000_000_000L;

    /// <summary>A bar of a day or more, which NinjaTrader imports by its date alone.</summary>
    public bool DayOrLonger => Unit is BarUnit.Day or BarUnit.Week or BarUnit.Month
        || Unit == BarUnit.Seconds && Size >= SecondsInADay;

    public static Result<BarSpec> Parse(string text)
    {
        var match = Pattern().Match(text.Trim().ToLowerInvariant());

        if (!match.Success || !long.TryParse(match.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1)
            return new Error("input.bad_bars", $"\"{text}\" is not a bar size such as 5s, 1m, 15m, 4h, 1d, 1w, 1mo, 500t or 1000v.");

        var spec = match.Groups["unit"].Value switch
        {
            "s" => new BarSpec(BarUnit.Seconds, n, text),
            "m" => new BarSpec(BarUnit.Seconds, n * 60, text),
            "h" => new BarSpec(BarUnit.Seconds, n * 3600, text),
            "d" when n == 1 => new BarSpec(BarUnit.Day, 1, text),
            "w" when n == 1 => new BarSpec(BarUnit.Week, 1, text),
            "mo" when n == 1 => new BarSpec(BarUnit.Month, 1, text),
            "t" => new BarSpec(BarUnit.Ticks, n, text),
            "v" => new BarSpec(BarUnit.Volume, n, text),
            _ => null
        };

        return spec is not null
            ? spec
            : new Error("input.bad_bars", $"\"{text}\": days, weeks and months come one at a time (1d, 1w, 1mo).");
    }

    [GeneratedRegex("^(?<n>[0-9]+)(?<unit>mo|s|m|h|d|w|t|v)$")]
    private static partial Regex Pattern();
}

/// <summary>One finished bar.</summary>
public sealed record Bar(string Key, string Symbol, string? Series, DateOnly TradeDate, ulong Start, ulong End,
    decimal Open, decimal High, decimal Low, decimal Close, long Volume, long Trades);

/// <summary>
/// Builds bars from 1-second bars or from trades. Time bars count from the
/// session's open, so the first RTH 5-minute ES bar opens at 08:30 Chicago;
/// no bar spans two trade dates, except weekly and monthly ones.
/// </summary>
public sealed class BarBuilder(BarSpec spec, SessionFilter sessions, Action<Bar> emit)
{
    private readonly Dictionary<string, Building> open = [];

    /// <param name="trades">Whether bars count trades, which only bars built from trades can.</param>
    public static IReadOnlyList<Column> Columns(bool series, bool trades) =>
    [
        new("ts_event", CellKind.Time), new("ts_end", CellKind.Time), new("symbol", CellKind.Text),
        new("open", CellKind.Price), new("high", CellKind.Price), new("low", CellKind.Price), new("close", CellKind.Price),
        new("volume", CellKind.Integer),
        .. trades ? [new Column("trades", CellKind.Integer)] : Array.Empty<Column>(),
        .. series ? [new Column("series", CellKind.Text)] : Array.Empty<Column>()
    ];

    public static void Fill(Bar bar, bool trades, Func<decimal?, decimal?> price, Span<Cell> cells)
    {
        cells[0] = Cell.Of(bar.Start);
        cells[1] = Cell.Of(bar.End);
        cells[2] = Cell.Of(bar.Symbol);
        cells[3] = Cell.Of(price(bar.Open));
        cells[4] = Cell.Of(price(bar.High));
        cells[5] = Cell.Of(price(bar.Low));
        cells[6] = Cell.Of(price(bar.Close));
        cells[7] = Cell.Of(bar.Volume);

        var i = 8;

        if (trades)
            cells[i++] = Cell.Of(bar.Trades);

        if (bar.Series is not null)
            cells[i] = Cell.Of(bar.Series);
    }

    /// <summary>Adds a 1-second bar (or any finer bar) that opened at <paramref name="ts"/>.</summary>
    /// <param name="key">What bars are kept apart by: the series, or each contract of a root.</param>
    public void Add(string key, string symbol, string? series, DateOnly tradeDate, ulong ts,
        decimal open, decimal high, decimal low, decimal close, long volume, long trades)
    {
        var (bucket, start, end) = Bucket(tradeDate, ts);

        // A bar belongs to one contract, so a series that rolls inside a week or a month starts a new one.
        if (!this.open.TryGetValue(key, out var bar) || bar.Bucket != bucket || bar.Symbol != symbol
            || bar.TradeDate != tradeDate && spec.Unit < BarUnit.Week)
        {
            if (bar is not null)
                Emit(key, bar);

            bar = new Building(bucket, symbol, series, tradeDate, start, end, open);
            this.open[key] = bar;
        }

        bar.Take(symbol, high, low, close, volume, trades, end);

        if (spec.FromTrades && (spec.Unit == BarUnit.Ticks ? bar.Trades : bar.Volume) >= spec.Size)
        {
            Emit(key, bar);
            this.open.Remove(key);
        }
    }

    /// <summary>Adds one trade.</summary>
    public void AddTrade(string key, string symbol, string? series, DateOnly tradeDate, ulong ts, decimal price, long size) =>
        Add(key, symbol, series, tradeDate, ts, price, price, price, price, size, 1);

    /// <summary>Emits every bar still open.</summary>
    public void Flush()
    {
        foreach (var (key, bar) in open.OrderBy(b => b.Value.Start))
            Emit(key, bar);

        open.Clear();
    }

    /// <summary>
    /// Emits, oldest first, the bars that <paramref name="tradeDate"/> cannot add to.
    /// Called as each trade date begins, it keeps bars of a root, whose contracts
    /// trade at their own pace, in date order.
    /// </summary>
    public void Flush(DateOnly tradeDate)
    {
        long? period = spec.Unit is BarUnit.Week or BarUnit.Month ? Bucket(tradeDate, 0).Bucket : null;

        foreach (var (key, bar) in open.Where(b => period is { } p ? b.Value.Bucket != p : b.Value.TradeDate != tradeDate)
            .OrderBy(b => b.Value.Start).ToList())
        {
            Emit(key, bar);
            open.Remove(key);
        }
    }

    private void Emit(string key, Building bar) =>
        emit(new Bar(key, bar.Symbol, bar.Series, bar.TradeDate, bar.Start, bar.End,
            bar.Open, bar.High, bar.Low, bar.Close, bar.Volume, bar.Trades));

    /// <summary>Which bar a time falls in, and that bar's start and end.</summary>
    private (long Bucket, ulong Start, ulong End) Bucket(DateOnly tradeDate, ulong ts)
    {
        switch (spec.Unit)
        {
            case BarUnit.Seconds:
            {
                var anchor = Nanos(sessions.Anchor(tradeDate));
                var index = ((long)ts - anchor) / spec.Nanos;
                var start = (ulong)(anchor + index * spec.Nanos);

                return (tradeDate.DayNumber * 1_000_000L + index, start, start + (ulong)spec.Nanos);
            }

            case BarUnit.Day:
                return (tradeDate.DayNumber, (ulong)Nanos(sessions.Anchor(tradeDate)), (ulong)Nanos(sessions.Close(tradeDate)));

            case BarUnit.Week:
            {
                var monday = tradeDate.AddDays(-(((int)tradeDate.DayOfWeek + 6) % 7));

                return (monday.DayNumber, (ulong)Nanos(sessions.Anchor(tradeDate)), (ulong)Nanos(sessions.Close(tradeDate)));
            }

            case BarUnit.Month:
                return (tradeDate.Year * 12L + tradeDate.Month, (ulong)Nanos(sessions.Anchor(tradeDate)), (ulong)Nanos(sessions.Close(tradeDate)));

            default:
                // Activity bars close on their count; a trade date starts a new one.
                return (tradeDate.DayNumber, ts, ts);
        }
    }

    private static long Nanos(DateTimeOffset time) => (time - DateTimeOffset.UnixEpoch).Ticks * 100;

    private sealed class Building(long bucket, string symbol, string? series, DateOnly tradeDate, ulong start, ulong end, decimal open)
    {
        public long Bucket { get; } = bucket;
        public string Symbol { get; private set; } = symbol;
        public string? Series { get; } = series;
        public DateOnly TradeDate { get; } = tradeDate;
        public ulong Start { get; } = start;
        public ulong End { get; private set; } = end;
        public decimal Open { get; } = open;
        public decimal High { get; private set; } = open;
        public decimal Low { get; private set; } = open;
        public decimal Close { get; private set; } = open;
        public long Volume { get; private set; }
        public long Trades { get; private set; }

        public void Take(string symbol, decimal high, decimal low, decimal close, long volume, long trades, ulong end)
        {
            Symbol = symbol;
            High = Math.Max(High, high);
            Low = Math.Min(Low, low);
            Close = close;
            Volume += volume;
            Trades += trades;
            End = Math.Max(End, end);
        }
    }
}
