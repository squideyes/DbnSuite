using System.Text.Json;
using System.Text.Json.Serialization;
using DbnClient.Archive;
using DbnClient.Contracts;
using DbnClient.Dbn;
using DbnClient.Time;

namespace DbnClient.Tape;

/// <summary>One outright contract, reduced to what a backtest needs.</summary>
public sealed record CatalogContract
{
    public required uint InstrumentId { get; init; }

    /// <summary>CME's own name, such as <c>ESZ6</c>. It repeats every ten years.</summary>
    public required string Symbol { get; init; }

    /// <summary>One tick in DBN's 1e-9 units.</summary>
    public required long TickRaw { get; init; }

    /// <summary>What one tick is worth in dollars.</summary>
    public required decimal TickValue { get; init; }

    public required int Year { get; init; }

    public required int Month { get; init; }

    /// <summary>The first moment it trades, in nanoseconds since the UNIX epoch.</summary>
    public required long FirstTrade { get; init; }

    /// <summary>The last moment it trades, in nanoseconds since the UNIX epoch.</summary>
    public required long LastTrade { get; init; }
}

/// <summary>A session as a day's contracts share it. Times are nanoseconds since the UNIX epoch.</summary>
public sealed record CatalogSession(long Open, long Close, IReadOnlyList<SessionBreak> Breaks);

/// <summary>One contract on one day. A contract with nothing to say here keeps the day's first session.</summary>
public sealed record CatalogEntry
{
    public required uint InstrumentId { get; init; }

    /// <summary>Which of the day's sessions it traded.</summary>
    public int Session { get; init; }

    /// <summary>In DBN's 1e-9 units.</summary>
    public long? Settlement { get; init; }

    public long? OpenInterest { get; init; }

    public long? ClearedVolume { get; init; }
}

/// <summary>One trade date of a root.</summary>
public sealed record CatalogDay
{
    public required DateOnly TradeDate { get; init; }

    /// <summary>
    /// The day's one tape contract: the one that traded the most in the previous session, never
    /// going back to an earlier one. Null when there is none.
    /// </summary>
    public string? Held { get; init; }

    /// <summary>The day's distinct sessions, the most common first.</summary>
    public required IReadOnlyList<CatalogSession> Sessions { get; init; }

    public required IReadOnlyList<CatalogEntry> Contracts { get; init; }
}

/// <summary>
/// Everything slow-changing about one root: its contracts and its days. It loads once
/// at start-up, so nothing needs the archive to ask when a contract traded.
/// </summary>
public sealed class Catalog
{
    private const ushort SettlementStat = 3;
    private const ushort ClearedVolumeStat = 6;
    private const ushort OpenInterestStat = 9;
    private const decimal RawUnits = 1_000_000_000m;
    private const string StatusSchema = "status";
    private const string StatisticsSchema = "statistics";
    private const string BarSchema = "ohlcv-1s";

    private readonly Dictionary<uint, CatalogContract> byId;
    private readonly ILookup<string, CatalogContract> bySymbol;
    private readonly Dictionary<DateOnly, CatalogDay> byDate;
    private readonly HashSet<uint> everHeld;
    private readonly Dictionary<DateOnly, string?> excluded;

    [JsonConstructor]
    public Catalog(string dataset, string root, IReadOnlyList<CatalogContract> contracts, IReadOnlyList<CatalogDay> days)
    {
        Dataset = dataset;
        Root = root;
        Contracts = contracts;
        Days = days;

        byId = contracts.ToDictionary(c => c.InstrumentId);
        bySymbol = contracts.ToLookup(c => c.Symbol);
        byDate = days.ToDictionary(d => d.TradeDate);
        everHeld = [.. days.SelectMany(d => d.Contracts).Select(c => c.InstrumentId)];
        excluded = days.ToDictionary(d => d.TradeDate, NotFullEth);
        FullDays = [.. days.Where(d => excluded[d.TradeDate] is null)];
    }

    /// <summary>
    /// The days that are full ETH days, the only ones tapes are made for: see <see cref="Excluded"/>.
    /// The catalog keeps the others, whose settlements and rolls still count.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<CatalogDay> FullDays { get; }

    /// <summary>
    /// Why a trade date has no tapes, or null when it is a full ETH day: a session that opened at 17:00
    /// Chicago time the evening before and closed at 16:00, each within five minutes, halts inside it allowed.
    /// Short sessions, such as holidays and early closes, are left out whole, so every tape day is alike.
    /// </summary>
    public string? Excluded(DateOnly tradeDate) =>
        excluded.TryGetValue(tradeDate, out var reason) ? reason : "it has no session";

    /// <summary>True when the date is a full ETH day, so it has tapes.</summary>
    public bool IsFullDay(DateOnly tradeDate) => Excluded(tradeDate) is null;

    /// <summary>True when the date lies within the catalog's days, so its absence means no session rather than not yet known.</summary>
    public bool Judges(DateOnly tradeDate) => Days.Count > 0 && tradeDate >= Days[0].TradeDate && tradeDate <= Days[^1].TradeDate;

    /// <summary>
    /// Judges the session the day's contracts traded: the held contract's, else the one most of them
    /// share. The day's first session is the most common across every instrument, spreads included,
    /// which can be a stray.
    /// </summary>
    private string? NotFullEth(CatalogDay day)
    {
        if (day.Sessions.Count == 0)
            return "it has no session";

        var held = day.Held is { } symbol && Contract(symbol, day.TradeDate) is { } contract
            ? day.Contracts.FirstOrDefault(c => c.InstrumentId == contract.InstrumentId)
            : null;

        var index = held?.Session
            ?? day.Contracts.GroupBy(c => c.Session).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => g.Key).FirstOrDefault();

        var session = day.Sessions[Math.Clamp(index, 0, day.Sessions.Count - 1)];

        return SessionRules.NotFullEth(day.TradeDate, session.Open, session.Close);
    }

    public string Dataset { get; }

    public string Root { get; }

    public IReadOnlyList<CatalogContract> Contracts { get; }

    public IReadOnlyList<CatalogDay> Days { get; }

    public static string PathOf(string tapeRoot, string dataset, string root) =>
        Path.Combine(tapeRoot, dataset, "catalog", $"{root}.catalog.json");

    public CatalogContract? Contract(uint instrumentId) => byId.GetValueOrDefault(instrumentId);

    /// <summary>The one contract a trade date's tapes hold, or null when the date has none.</summary>
    public CatalogContract? HeldOn(DateOnly tradeDate) =>
        Day(tradeDate)?.Held is { } symbol ? Contract(symbol, tradeDate) : null;

    /// <summary>The contract of that name. Names repeat every ten years, so a date picks between them.</summary>
    /// <param name="on">A day it was listed on. Without one, the latest of the name.</param>
    public CatalogContract? Contract(string symbol, DateOnly? on = null)
    {
        var named = bySymbol[symbol].OrderByDescending(c => c.LastTrade);

        if (on is not { } date)
            return named.FirstOrDefault();

        var moment = (Sessions.SessionEnd(date) - DateTimeOffset.UnixEpoch).Ticks * 100;
        var listed = named.Where(c => c.FirstTrade < moment && c.LastTrade >= Yesterday(moment)).ToList();
        var entries = Day(date)?.Contracts;

        // A name can have a second, unused definition. The real one is what the statistics speak of,
        // that day if they can: a holiday has none.
        return listed.FirstOrDefault(c => entries?.Any(e => e.InstrumentId == c.InstrumentId) == true)
            ?? listed.FirstOrDefault(c => everHeld.Contains(c.InstrumentId))
            ?? listed.FirstOrDefault();
    }

    public CatalogDay? Day(DateOnly tradeDate) => byDate.GetValueOrDefault(tradeDate);

    /// <summary>Reads a root's definitions, status and statistics into a new catalog.</summary>
    public static async Task<Result<Catalog>> BuildAsync(DbnArchive archive, string root, IProgress<string>? progress, CancellationToken cancel)
    {
        try
        {
            progress?.Report("definitions");

            var contracts = await ContractsAsync(archive, root, cancel).ConfigureAwait(false);

            progress?.Report($"{contracts.Count} contracts; status");

            var recorded = await SpansAsync(archive, root, contracts, cancel).ConfigureAwait(false);

            progress?.Report("bars");

            var spans = SessionRules.Repair(recorded, await FirstBarsAsync(archive, root, contracts, cancel).ConfigureAwait(false));

            progress?.Report("statistics");

            var statistics = await StatisticsAsync(archive, root, contracts, cancel).ConfigureAwait(false);

            progress?.Report("rolls");

            var dates = spans.Values.SelectMany(s => s).Select(s => Sessions.TradeDate((ulong)s.Start)).ToList();

            if (dates.Count == 0)
                return new Error(TapeErrors.Refused, $"The archive has no status for {root}.");

            var held = await HeldAsync(archive, root, new DateSpan(dates.Min(), dates.Max()), cancel).ConfigureAwait(false);

            if (!held.IsSuccess)
                return held.Error!;

            return new Catalog(archive.Dataset, root,
                [.. contracts.Values.OrderBy(c => c.LastTrade)],
                DaysFrom(root, spans, statistics, held.Value));
        }
        catch (ArchiveException e)
        {
            return e.Error;
        }
    }

    public async Task<Result<string>> SaveAsync(string tapeRoot, CancellationToken cancel)
    {
        var path = PathOf(tapeRoot, Dataset, Root);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var temporary = $"{path}.{Guid.NewGuid():N}.tmp";

            try
            {
                var file = File.Create(temporary);

                await using (file.ConfigureAwait(false))
                    await JsonSerializer.SerializeAsync(file, this, CatalogJson.Default.Catalog, cancel).ConfigureAwait(false);

                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                // Gone already after the move; left behind only by a stop or a full disk part way through.
                File.Delete(temporary);
            }

            return path;
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            return new Error(TapeErrors.Io, $"Could not save {path}: {problem.Message}");
        }
    }

    public static async Task<Result<Catalog>> LoadAsync(string path, CancellationToken cancel)
    {
        try
        {
            var file = File.OpenRead(path);

            await using (file.ConfigureAwait(false))
            {
                var catalog = await JsonSerializer.DeserializeAsync(file, CatalogJson.Default.Catalog, cancel).ConfigureAwait(false);

                return catalog is null ? new Error(TapeErrors.Corrupt, $"{path} is empty.") : catalog;
            }
        }
        catch (JsonException problem)
        {
            return new Error(TapeErrors.Corrupt, $"{path} is not a catalog: {problem.Message}");
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            return new Error(TapeErrors.Io, $"Could not read {path}: {problem.Message}");
        }
    }

    /// <summary>Puts spans, statistics and the roll rule together, a day at a time.</summary>
    internal static IReadOnlyList<CatalogDay> DaysFrom(
        string root,
        IReadOnlyDictionary<uint, List<TradingSpan>> spans,
        IReadOnlyDictionary<(DateOnly Date, uint Id), DayStatistics> statistics,
        Func<DateOnly, string?> held)
    {
        var sessions = new SortedDictionary<DateOnly, List<(uint Id, CatalogSession Session)>>();

        foreach (var (id, list) in spans)
        {
            foreach (var day in SessionRules.Days(root, list))
            {
                if (!sessions.TryGetValue(day.TradeDate, out var members))
                    sessions[day.TradeDate] = members = [];

                members.Add((id, new CatalogSession(day.Open, day.Close, day.Breaks)));
            }
        }

        return [.. sessions.Select(s => Day(s.Key, s.Value, statistics, held(s.Key)))];
    }

    private static CatalogDay Day(DateOnly date, List<(uint Id, CatalogSession Session)> members,
        IReadOnlyDictionary<(DateOnly Date, uint Id), DayStatistics> statistics, string? held)
    {
        var groups = members
            .GroupBy(m => Key(m.Session))
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        var entries = new List<CatalogEntry>();

        for (var index = 0; index < groups.Count; index++)
        {
            foreach (var (id, _) in groups[index])
            {
                var day = statistics.GetValueOrDefault((date, id));

                // A contract nobody holds or trades has nothing a backtest would ask for.
                if (index == 0 && day is null or { OpenInterest: null or 0, ClearedVolume: null or 0 })
                    continue;

                entries.Add(new CatalogEntry
                {
                    InstrumentId = id,
                    Session = index,
                    Settlement = day?.Settlement,
                    OpenInterest = day?.OpenInterest,
                    ClearedVolume = day?.ClearedVolume
                });
            }
        }

        return new CatalogDay
        {
            TradeDate = date,
            Held = held,
            Sessions = [.. groups.Select(g => g.First().Session)],
            Contracts = [.. entries.OrderBy(e => e.InstrumentId)]
        };
    }

    private static string Key(CatalogSession session) =>
        $"{session.Open}|{session.Close}|{string.Join(',', session.Breaks.Select(b => $"{b.Start}-{b.End}"))}";

    private static long Yesterday(long moment) => moment - 86_400_000_000_000L;

    private static async Task<Dictionary<uint, CatalogContract>> ContractsAsync(DbnArchive archive, string root, CancellationToken cancel)
    {
        var contracts = new Dictionary<uint, CatalogContract>();

        await foreach (var raw in RecordsAsync(archive, DbnArchive.DefinitionSchema, root, cancel).ConfigureAwait(false))
        {
            if (raw.Length < 2 || raw[1] != DefinitionRecord.Type || raw.Length != DefinitionRecord.Size)
                continue;

            if (DbnRecord.Decode(raw) is not DefinitionRecord { InstrumentClass: DefinitionRecord.Future } definition)
                continue;

            var tickRaw = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(raw[24..]);
            var unitRaw = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(raw[80..]);

            if (tickRaw is <= 0 or DbnPrice.Undefined)
                continue;

            contracts[definition.InstrumentId] = new CatalogContract
            {
                InstrumentId = definition.InstrumentId,
                Symbol = definition.RawSymbol,
                TickRaw = tickRaw,
                TickValue = unitRaw == DbnPrice.Undefined ? 0 : tickRaw / RawUnits * (unitRaw / RawUnits),
                Year = definition.MaturityYear,
                Month = definition.MaturityMonth,
                FirstTrade = (long)definition.Activation,
                LastTrade = (long)definition.Expiration
            };
        }

        return contracts;
    }

    private static async Task<Dictionary<uint, List<TradingSpan>>> SpansAsync(DbnArchive archive, string root,
        Dictionary<uint, CatalogContract> contracts, CancellationToken cancel)
    {
        var trackers = new Dictionary<uint, SessionRules.SpanTracker>();

        await foreach (var raw in RecordsAsync(archive, StatusSchema, root, cancel).ConfigureAwait(false))
        {
            if (raw[1] != StatusRecord.Type || !contracts.ContainsKey(DbnRecord.InstrumentOf(raw)))
                continue;

            var id = DbnRecord.InstrumentOf(raw);

            if (!trackers.TryGetValue(id, out var tracker))
                trackers[id] = tracker = new SessionRules.SpanTracker();

            tracker.Add(StatusLog.Point(raw));
        }

        return trackers.ToDictionary(t => t.Key, t => t.Value.Spans);
    }

    /// <summary>When each contract first traded on each trade date.</summary>
    private static async Task<Dictionary<uint, Dictionary<DateOnly, long>>> FirstBarsAsync(DbnArchive archive, string root,
        Dictionary<uint, CatalogContract> contracts, CancellationToken cancel)
    {
        var firsts = new Dictionary<uint, Dictionary<DateOnly, long>>();

        await foreach (var raw in RecordsAsync(archive, BarSchema, root, cancel).ConfigureAwait(false))
        {
            if (raw[1] != OhlcvRecord.FirstType || !contracts.ContainsKey(DbnRecord.InstrumentOf(raw)))
                continue;

            var id = DbnRecord.InstrumentOf(raw);
            var time = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(8));
            var date = Sessions.TradeDate(time);

            if (!firsts.TryGetValue(id, out var days))
                firsts[id] = days = [];

            if (!days.TryGetValue(date, out var first) || (long)time < first)
                days[date] = (long)time;
        }

        return firsts;
    }

    private static async Task<Dictionary<(DateOnly Date, uint Id), DayStatistics>> StatisticsAsync(DbnArchive archive, string root,
        Dictionary<uint, CatalogContract> contracts, CancellationToken cancel)
    {
        var days = new Dictionary<(DateOnly, uint), DayStatistics>();

        await foreach (var raw in RecordsAsync(archive, StatisticsSchema, root, cancel).ConfigureAwait(false))
        {
            if (raw[1] != StatRecord.Type || !contracts.ContainsKey(DbnRecord.InstrumentOf(raw)))
                continue;

            if (DbnRecord.Decode(raw) is not StatRecord stat || stat.TsRef == DbnTime.Undefined)
                continue;

            if (stat.StatType is not (SettlementStat or ClearedVolumeStat or OpenInterestStat))
                continue;

            var key = (DateOnly.FromDateTime(DbnTime.ToTime(stat.TsRef).UtcDateTime), stat.InstrumentId);
            var day = days.GetValueOrDefault(key) ?? new DayStatistics(null, null, null);
            var quantity = stat.Quantity == StatRecord.UndefinedQuantity ? (long?)null : stat.Quantity;

            var price = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(raw[32..]);

            // A settlement with no price says nothing, and must not wipe out one that has.
            if (stat.StatType == SettlementStat && price == DbnPrice.Undefined)
                continue;

            days[key] = stat.StatType switch
            {
                SettlementStat => day with { Settlement = price },
                ClearedVolumeStat => day with { ClearedVolume = quantity },
                _ => day with { OpenInterest = quantity }
            };
        }

        return days;
    }

    /// <summary>Each date's most traded contract, by the previous session's volume (<c>.v.0</c>).</summary>
    private static async Task<Result<Func<DateOnly, string?>>> HeldAsync(DbnArchive archive, string root, DateSpan span, CancellationToken cancel)
    {
        var series = SymbolSpec.Parse($"{root}.v.0");

        if (!series.IsSuccess)
            return series.Error!;

        var schedule = await RollSchedule.BuildAsync(archive, series.Value, span, null, null, cancel).ConfigureAwait(false);

        if (!schedule.IsSuccess)
            return schedule.Error!;

        var rolls = schedule.Value;

        return new Func<DateOnly, string?>(date => rolls.On(date)?.RawSymbol);
    }

    private static async IAsyncEnumerable<byte[]> RecordsAsync(DbnArchive archive, string schema, string root,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancel)
    {
        var days = await archive.ListDaysAsync(schema, root, cancel).ConfigureAwait(false);

        if (!days.IsSuccess)
            throw new ArchiveException(days.Error!);

        foreach (var stored in days.Value.Where(d => !d.Empty))
        {
            var opened = await archive.OpenAsync(schema, root, stored.Day, cancel).ConfigureAwait(false);

            if (!opened.IsSuccess)
                throw new ArchiveException(opened.Error!);

            var reader = opened.Value;

            await using (reader.ConfigureAwait(false))
            {
                await foreach (var raw in reader.ReadRawAsync(cancel).ConfigureAwait(false))
                    yield return raw.ToArray();
            }
        }
    }
}

/// <summary>What the statistics say about one contract on one day.</summary>
internal sealed record DayStatistics(long? Settlement, long? OpenInterest, long? ClearedVolume);

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Catalog))]
internal sealed partial class CatalogJson : JsonSerializerContext;
