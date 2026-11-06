using System.Runtime.CompilerServices;
using DbnClient.Export;
using DbnClient.Time;

namespace DbnClient.Tape;

/// <summary>One day of a continuous series: the real contract held, as it traded.</summary>
/// <param name="Offset">
/// What to add to a price, in DBN's 1e-9 units, to continue it across later rolls. It is for
/// indicators, never fills. Null when a later roll has no settlement to measure it by.
/// </param>
public sealed record SeriesDay(DateOnly TradeDate, string Symbol, Tape Tape, long? Offset);

/// <summary>
/// A continuous series as a list of tapes: the contract the catalog's roll rule holds each
/// day. It keeps the current day in memory and fetches the next while that one replays.
/// </summary>
public sealed class TapeSeries(Catalog catalog, string tapeRoot, TapeKind kind, DateSpan tradeDates)
{
    private readonly IReadOnlyDictionary<DateOnly, long?> offsets = Offsets(catalog);

    /// <summary>Each day's tape, oldest first. A missing or damaged tape is a failed result, and ends the series.</summary>
    public async IAsyncEnumerable<Result<SeriesDay>> DaysAsync([EnumeratorCancellation] CancellationToken cancel)
    {
        var days = catalog.Days.Where(d => tradeDates.Contains(d.TradeDate)).ToList();

        if (days.Count == 0)
            yield break;

        var next = LoadAsync(days[0], cancel);

        for (var i = 0; i < days.Count; i++)
        {
            var day = await next.ConfigureAwait(false);

            if (!day.IsSuccess)
            {
                yield return day;
                yield break;
            }

            if (i + 1 < days.Count)
                next = LoadAsync(days[i + 1], cancel);

            yield return day;
        }
    }

    /// <summary>A bar stream that runs across this series' days. No bar spans two contracts.</summary>
    public SeriesBars Bars(BarSpec spec, Window window = default) => new(spec, window);

    /// <summary>The offset of each trade date, from the gaps of every later roll.</summary>
    internal static IReadOnlyDictionary<DateOnly, long?> Offsets(Catalog catalog)
    {
        var offsets = new Dictionary<DateOnly, long?>();
        var days = catalog.Days;
        long? offset = 0;

        for (var i = days.Count - 1; i >= 0; i--)
        {
            offsets[days[i].TradeDate] = offset;

            if (i > 0 && days[i].Held is { } held && days[i - 1].Held is { } before && held != before)
                offset = Gap(catalog, days[i - 1], before, held) is { } gap ? offset + gap : null;
        }

        return offsets;
    }

    /// <summary>How far the new contract's settlement stood from the old one's, on the session before the roll.</summary>
    private static long? Gap(Catalog catalog, CatalogDay day, string from, string to)
    {
        var old = Settlement(catalog, day, from);
        var fresh = Settlement(catalog, day, to);

        return old is { } o && fresh is { } f ? f - o : null;
    }

    private static long? Settlement(Catalog catalog, CatalogDay day, string symbol)
    {
        var contract = catalog.Contract(symbol, day.TradeDate);

        return day.Contracts.FirstOrDefault(c => c.InstrumentId == contract?.InstrumentId)?.Settlement;
    }

    private async Task<Result<SeriesDay>> LoadAsync(CatalogDay day, CancellationToken cancel)
    {
        if (day.Held is not { } symbol)
            return new Error(TapeErrors.NotHeld, $"The roll rule holds nothing on {day.TradeDate:yyyy-MM-dd}.");

        var path = TapePaths.For(tapeRoot, catalog.Dataset, kind, catalog.Root, symbol, day.TradeDate);
        var tape = await Tape.LoadAsync(path, cancel).ConfigureAwait(false);

        return tape.IsSuccess
            ? new SeriesDay(day.TradeDate, symbol, tape.Value, offsets.GetValueOrDefault(day.TradeDate))
            : tape.Error!;
    }
}

/// <summary>
/// Rolls bars across a series' days. Feed it each day with <see cref="Over"/>, and call
/// <see cref="Finish"/> after the last, for a weekly or monthly bar still open.
/// </summary>
public sealed class SeriesBars(BarSpec spec, Window window)
{
    private readonly BarRoller<SpecRule> roller = new(new SpecRule(spec));

    private string? symbol;

    /// <summary>The stream of one S1 day.</summary>
    public BarStream<S1Cursor, SpecRule> Over(SeriesDay day)
    {
        if (symbol is not null && symbol != day.Symbol)
            roller.Seal();

        symbol = day.Symbol;

        var (open, close) = day.Tape.Bounds(window);

        roller.Rule.Begin(open, close, day.TradeDate);

        return new BarStream<S1Cursor, SpecRule>(day.Tape.S1Cursor(window), roller, flushAtEnd: !roller.SpansDates);
    }

    /// <summary>Gives the bar still forming after the last day, if there is one.</summary>
    public bool Finish(out TapeBar bar) => roller.Take(out bar) || roller.Flush(out bar);
}
