using DbnClient.Archive;
using DbnClient.Dbn;
using DbnClient.Time;

namespace DbnClient.Contracts;

/// <summary>How a continuous series' earlier prices are changed at each roll.</summary>
public enum Adjustment
{
    /// <summary>Prices as traded.</summary>
    None,

    /// <summary>Each roll's gap is added to every earlier price (back-adjustment).</summary>
    Difference,

    /// <summary>Every earlier price is scaled by each roll's ratio, keeping percentage moves.</summary>
    Ratio
}

/// <summary>One change of contract: <see cref="TradeDate"/> is the first trade date <see cref="To"/> is used.</summary>
/// <param name="FromSettle">The old contract's settlement on the session before the roll.</param>
/// <param name="ToSettle">The new contract's settlement on that session.</param>
public sealed record Roll(DateOnly TradeDate, Contract From, Contract To, decimal? FromSettle, decimal? ToSettle)
{
    /// <summary>
    /// The calendar day the schedule changed contract, which decides the prices
    /// a roll adjusts. A weekend puts it before <see cref="TradeDate"/>.
    /// </summary>
    internal DateOnly Changed { get; init; } = TradeDate;

    public decimal? Gap => ToSettle - FromSettle;

    public decimal? Ratio => FromSettle is { } f && f != 0 && ToSettle is { } t ? t / f : null;
}

/// <summary>
/// Which contract a continuous name holds on each trade date. A roll is decided
/// from the last complete session and takes effect at the next session's open,
/// so nothing is decided with hindsight, and a series never rolls back to an
/// earlier contract.
/// </summary>
public sealed class RollSchedule
{
    /// <summary>How far back data is read before the first date, to have a previous session to decide from.</summary>
    private const int Lookback = 7;

    private const ushort SettlementPrice = 3;
    private const ushort OpenInterest = 9;

    private readonly SortedDictionary<DateOnly, Contract> held;

    private RollSchedule(SymbolSpec series, SortedDictionary<DateOnly, Contract> held, IReadOnlyList<Roll> rolls)
    {
        Series = series;
        this.held = held;
        Rolls = rolls;
    }

    public SymbolSpec Series { get; }

    public IReadOnlyList<Roll> Rolls { get; }

    /// <summary>
    /// Calendar rolls happen this many days before expiry unless told otherwise:
    /// the equity indexes roll on the Thursday eight days before the third Friday.
    /// </summary>
    public const int DefaultRollDays = 8;

    /// <summary>The contract held on a trade date, or null when none was.</summary>
    public Contract? On(DateOnly tradeDate) => held.GetValueOrDefault(tradeDate);

    /// <summary>
    /// What a price on <paramref name="tradeDate"/> becomes once later rolls are
    /// adjusted for, or <see cref="Errors.NoSettlement"/> when one of those rolls
    /// has no gap or ratio to adjust by.
    /// </summary>
    public Result<Func<decimal?, decimal?>> Adjuster(DateOnly tradeDate, Adjustment adjustment)
    {
        var later = Rolls.Where(r => r.Changed > tradeDate).ToList();

        Func<decimal?, decimal?> adjust = p => p;

        switch (adjustment)
        {
            case Adjustment.Difference:
            {
                if (later.Find(r => r.Gap is null) is { } roll)
                    return Unadjustable(roll, "gap");

                var offset = later.Sum(r => r.Gap!.Value);

                adjust = p => p + offset;
                break;
            }

            case Adjustment.Ratio:
            {
                if (later.Find(r => r.Ratio is null) is { } roll)
                    return Unadjustable(roll, "ratio");

                var factor = later.Aggregate(1m, (f, r) => f * r.Ratio!.Value);

                adjust = p => p * factor;
                break;
            }
        }

        return adjust;
    }

    private static Error Unadjustable(Roll roll, string measure) => new(Errors.NoSettlement,
        $"The roll on {roll.TradeDate:yyyy-MM-dd} from {roll.From.Symbol} to {roll.To.Symbol} has no {measure}: the archive " +
        "has no usable settlement for both contracts on the session before it, so earlier prices cannot be adjusted. " +
        "Export without adjustment, or add that session's statistics to the archive.");

    public static async Task<Result<RollSchedule>> BuildAsync(DbnArchive archive, SymbolSpec series, DateSpan tradeDates,
        int rollDays, IProgress<long>? progress, CancellationToken cancel)
    {
        if (series.Kind != SymbolKind.Continuous)
            throw new ArgumentException("Only a continuous name has a roll schedule.", nameof(series));

        if (Sessions.Unsupported(archive.Dataset) is { } unsupported)
            return unsupported;

        var listed = await archive.ContractsAsync(series.Root, progress, cancel).ConfigureAwait(false);

        if (!listed.IsSuccess)
            return listed.Error!;

        var outrights = listed.Value.Where(c => !c.IsSpread && c.Expiration is not null).ToList();
        var byId = outrights.ToDictionary(c => c.InstrumentId);
        var days = new DateSpan(tradeDates.From.AddDays(-Lookback), tradeDates.To);

        StatisticSeries statistics;
        Dictionary<DateOnly, Dictionary<uint, decimal>> volumes;

        try
        {
            statistics = await StatisticsAsync(archive, series.Root, days, byId, progress, cancel).ConfigureAwait(false);
            volumes = series.Rule == 'v'
                ? await VolumesAsync(archive, series.Root, days, byId, progress, cancel).ConfigureAwait(false)
                : [];
        }
        catch (ArchiveException e)
        {
            return e.Error;
        }

        var held = new SortedDictionary<DateOnly, Contract>();
        var rolls = new List<Roll>();
        Contract? current = null;

        for (var date = tradeDates.From; date <= tradeDates.To; date = date.AddDays(1))
        {
            var opens = Sessions.SessionStart(date);
            var live = outrights
                .Where(c => c.Expiration > opens && (c.FirstTrade is null || c.FirstTrade <= opens))
                .ToList();

            var ranked = series.Rule switch
            {
                'c' => live.Where(c => DateOnly.FromDateTime(c.Expiration!.Value.UtcDateTime).AddDays(-rollDays) > date)
                    .OrderBy(c => c.Expiration).ToList(),
                'v' => Rank(live, VolumeBefore(volumes, date)),
                _ => Rank(live, statistics.Latest(OpenInterest, opens, quantity: true))
            };

            if (ranked.Count <= series.Rank)
                continue;

            var pick = ranked[series.Rank];

            // Never back to an earlier contract while the one held still trades.
            if (current is not null && pick.Expiration < current.Expiration && current.Expiration > opens)
                pick = current;

            if (current is not null && pick.InstrumentId != current.InstrumentId)
            {
                var settles = statistics.Latest(SettlementPrice, opens, quantity: false);

                // Decided over a weekend, it is first used on the Monday.
                rolls.Add(new Roll(Sessions.FirstTradeDate(date), current, pick,
                    settles.TryGetValue(current.InstrumentId, out var f) ? f : null,
                    settles.TryGetValue(pick.InstrumentId, out var t) ? t : null) { Changed = date });
            }

            held[date] = pick;
            current = pick;
        }

        return new RollSchedule(series, held, rolls);
    }

    /// <summary>Contracts by a measure, largest first; a contract with none comes last, by expiry.</summary>
    private static List<Contract> Rank(List<Contract> live, IReadOnlyDictionary<uint, decimal> measure) =>
        [.. live.OrderByDescending(c => measure.GetValueOrDefault(c.InstrumentId)).ThenBy(c => c.Expiration)];

    /// <summary>Each contract's volume in the latest session before <paramref name="date"/> that had any.</summary>
    private static IReadOnlyDictionary<uint, decimal> VolumeBefore(Dictionary<DateOnly, Dictionary<uint, decimal>> volumes,
        DateOnly date) =>
        volumes.Where(v => v.Key < date && v.Value.Values.Sum() > 0).OrderByDescending(v => v.Key)
            .Select(v => v.Value).FirstOrDefault() ?? [];

    private static async Task<Dictionary<DateOnly, Dictionary<uint, decimal>>> VolumesAsync(DbnArchive archive, string root,
        DateSpan days, Dictionary<uint, Contract> outrights, IProgress<long>? progress, CancellationToken cancel)
    {
        var volumes = new Dictionary<DateOnly, Dictionary<uint, decimal>>();

        await foreach (var record in RecordsAsync(archive, "ohlcv-1s", root, days, progress, cancel).ConfigureAwait(false))
        {
            if (record is not OhlcvRecord bar || !outrights.ContainsKey(bar.InstrumentId))
                continue;

            var date = Sessions.TradeDate(bar.TsEvent);

            if (!volumes.TryGetValue(date, out var day))
                volumes[date] = day = [];

            day[bar.InstrumentId] = day.GetValueOrDefault(bar.InstrumentId) + bar.Volume;
        }

        return volumes;
    }

    private static async Task<StatisticSeries> StatisticsAsync(DbnArchive archive, string root, DateSpan days,
        Dictionary<uint, Contract> outrights, IProgress<long>? progress, CancellationToken cancel)
    {
        var series = new StatisticSeries();

        await foreach (var record in RecordsAsync(archive, "statistics", root, days, progress, cancel).ConfigureAwait(false))
        {
            if (record is StatRecord s && outrights.ContainsKey(s.InstrumentId) && s.StatType is SettlementPrice or OpenInterest)
                series.Add(s);
        }

        return series;
    }

    /// <summary>
    /// Every record of a schema over a span of UTC days; days the archive lacks are
    /// skipped, but a day it could not give is an <see cref="ArchiveException"/>.
    /// </summary>
    private static async IAsyncEnumerable<DbnRecord> RecordsAsync(DbnArchive archive, string schema, string root, DateSpan days,
        IProgress<long>? progress, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancel)
    {
        for (var day = days.From; day <= days.To; day = day.AddDays(1))
        {
            var opened = await archive.OpenAsync(schema, root, day, cancel).ConfigureAwait(false);

            if (!opened.IsSuccess)
            {
                if (opened.Error!.Code is Errors.NotStored or Errors.NoData)
                    continue;

                throw new ArchiveException(opened.Error);
            }

            await using var reader = opened.Value;

            await foreach (var record in reader.ReadAsync(cancel).ConfigureAwait(false))
                yield return record;

            progress?.Report(1);
        }
    }

    /// <summary>Settlements and open interest, each instrument's values in time order.</summary>
    private sealed class StatisticSeries
    {
        private readonly List<StatRecord> records = [];

        public void Add(StatRecord record) => records.Add(record);

        /// <summary>Each instrument's latest value of a statistic published before <paramref name="before"/>.</summary>
        public IReadOnlyDictionary<uint, decimal> Latest(ushort statType, DateTimeOffset before, bool quantity)
        {
            var cutoff = (ulong)((before - DateTimeOffset.UnixEpoch).Ticks * 100);
            var latest = new Dictionary<uint, (ulong At, decimal Value)>();

            foreach (var r in records)
            {
                if (r.StatType != statType || r.TsEvent >= cutoff)
                    continue;

                decimal? value = quantity
                    ? r.Quantity == StatRecord.UndefinedQuantity ? null : r.Quantity
                    : r.Price;

                if (value is { } v && (!latest.TryGetValue(r.InstrumentId, out var seen) || r.TsEvent >= seen.At))
                    latest[r.InstrumentId] = (r.TsEvent, v);
            }

            return latest.ToDictionary(l => l.Key, l => l.Value.Value);
        }
    }
}
