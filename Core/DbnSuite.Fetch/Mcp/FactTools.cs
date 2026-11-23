using System.ComponentModel;
using DbnSuite.Client;
using DbnSuite.Client.Archive;
using DbnSuite.Client.Calendars;
using DbnSuite.Client.Contracts;
using DbnSuite.Client.Dbn;
using DbnSuite.Client.Tape;
using DbnSuite.Client.Time;
using DbnSuite.Fetch.Commands;
using ModelContextProtocol.Server;

namespace DbnSuite.Fetch.Mcp;

/// <summary>
/// The tools every <c>dbnf mcp</c> offers: facts about the archive, never its records. Each reads only,
/// though reading an S3 archive downloads what it reads into the cache, as dbnf always does.
/// </summary>
internal sealed class FactTools(ToolHost host)
{
    private const int MostCoverageDays = 1_100;
    private const int DefaultCoverageDays = 365;
    private const int MostCalendarDays = 366;
    private const int MostRollDays = 366;
    private const int MostCatalogDays = 366;
    private const int DefaultCatalogDays = 30;
    private const int MostVolumeDays = 92;
    private const string BarSchema = "ohlcv-1s";
    private const string Folder = "folder";
    private const string S3 = "s3";

    [McpServerTool(Name = "archive_status", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("Which archive dbnf reads (a folder, or an S3 endpoint, bucket and prefix) and whether it is healthy: "
        + "whether the login reaches it, this computer's clock against the archive's, and the cache folder and its free space. "
        + "Also which optional tools this server offers. It never shows the access key or secret key. "
        + "Logging in is for the user to do at a terminal with dbnf login; no tool here does it.")]
    public async Task<ArchiveStatus> ArchiveStatusAsync(CancellationToken cancel)
    {
        var status = new ArchiveStatus
        {
            Profile = host.Parse.GetValue(host.Session.Archive) is null ? host.Profile : null,
            Reachable = false,
            DataTools = host.AllowData,
            Out = host.Out,
            MaxGb = host.MaxBytes / (double)McpCommand.Gigabyte,
            Tapes = host.Tapes
        };

        var resolved = DoctorCommand.Resolve(host.Session, host.Parse);

        if (!resolved.IsSuccess)
            return status with { Problem = host.Hide(resolved.Error!.Message) };

        if (resolved.Value is not { } options)
        {
            return status with
            {
                Problem = $"There is no login named {host.Profile}. The user can run dbnf login at a terminal; this server never logs in."
            };
        }

        host.Remember(options);

        var checkup = await DoctorCommand.CheckAsync(options, host.CacheRoot, cancel).ConfigureAwait(false);

        status = status with
        {
            Kind = options.IsFolder ? Folder : S3,
            Location = WithoutUser(options.Location),
            Bucket = options.IsFolder ? null : options.Bucket,
            Prefix = options.IsFolder || options.Prefix.Length == 0 ? null : options.Prefix,
            Region = options.IsFolder ? null : options.Region,
            Dataset = options.Dataset,
            KeySaved = options.IsFolder ? null : checkup.KeyUsable,
            ClockSkewSeconds = checkup.ClockSkew is { } skew ? Math.Round(skew.TotalSeconds) : null,
            ClockOk = options.IsFolder ? null : checkup.ClockOk,
            Cache = checkup.Cache?.Root,
            CacheBytes = checkup.Cache?.Used,
            CacheFreeBytes = checkup.Cache?.Free,
            CacheOk = checkup.Cache?.Ok
        };

        if (checkup.Layout is not { } layout)
        {
            return status with
            {
                Problem = Session.Usable(options, host.Profile)?.Message ?? "The login cannot open the archive."
            };
        }

        return layout.IsSuccess
            ? status with { Reachable = true, Layout = layout.Value }
            : status with { Problem = host.Hide(layout.Error!.Message) };
    }

    [McpServerTool(Name = "list_series", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("The series the archive holds, like dbnf ls: each root and schema with its days held, days with no data "
        + "(weekends, holidays), size in bytes, and first and last day held. Days are UTC days, a file each.")]
    public Task<SeriesList> ListSeriesAsync(
        [Description("Only this root, such as ES.")] string? root = null,
        [Description("Only this schema, such as mbo or ohlcv-1s.")] string? schema = null,
        CancellationToken cancel = default) =>
        host.RunAsync<SeriesList>(async archive =>
        {
            var listed = await archive.ListSeriesAsync(cancel).ConfigureAwait(false);

            if (!listed.IsSuccess)
                return listed.Error!;

            var series = ListCommand.Select(listed.Value, schema, root);

            return new SeriesList(
                [.. series.Select(s => new SeriesEntry(s.Root, s.Schema, s.Files, s.EmptyDays, s.Bytes, s.First, s.Last))],
                series.Sum(s => s.Bytes));
        });

    [McpServerTool(Name = "coverage", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("Which UTC days one root and schema hold, like dbnf coverage: how many days hold a file, the days with no data "
        + "(the archive asked and there was nothing, such as weekends and holidays), and the missing days (never fetched). "
        + "By default the last 365 days up to the last day held; at most 1,100 days at a time.")]
    public Task<CoverageResult> CoverageAsync(
        [Description("A root, such as ES.")] string root,
        [Description("A schema, such as mbo, ohlcv-1s, definition, statistics or status.")] string schema,
        [Description("The first UTC day.")] DateOnly? from = null,
        [Description("The last UTC day, included.")] DateOnly? to = null,
        CancellationToken cancel = default) =>
        host.RunAsync<CoverageResult>(async archive =>
        {
            var name = root.Trim().ToUpperInvariant();
            var checkedSchema = Session.Schema(schema);

            if (!checkedSchema.IsSuccess)
                return checkedSchema.Error!;

            var days = await archive.ListDaysAsync(checkedSchema.Value, name, cancel).ConfigureAwait(false);

            if (!days.IsSuccess)
                return days.Error!;

            var held = days.Value;

            if (held.Count == 0 && from is null && to is null)
                return new Error(Errors.NotStored, $"The archive holds nothing for {name} {checkedSchema.Value}.");

            var last = to ?? (held.Count > 0 ? held[^1].Day : from!.Value);
            var first = from ?? Later(held.Count > 0 ? held[0].Day : last, last.AddDays(1 - DefaultCoverageDays));

            if (Span(first, last, MostCoverageDays) is { } bad)
                return bad;

            var coverage = CoverageCommand.Classify(held, first, last);

            return new CoverageResult(name, checkedSchema.Value, first, last,
                held.Count > 0 ? held[0].Day : null, held.Count > 0 ? held[^1].Day : null,
                coverage.Stored.Count, coverage.NoData, coverage.Missing);
        });

    [McpServerTool(Name = "contracts", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("A root's contracts from the archive's definitions, like dbnf contracts: symbol (ESZ26), CME's own symbol (ESZ6), "
        + "instrument id, when it was listed and expires, and its tick size. Outright contracts only, unless spreads is true.")]
    public Task<ContractsResult> ContractsAsync(
        [Description("A root, such as ES.")] string root,
        [Description("List spreads as well as outright contracts.")] bool spreads = false,
        [Description("Only contracts that have not expired.")] bool active = false,
        CancellationToken cancel = default) =>
        host.RunAsync<ContractsResult>(async archive =>
        {
            var name = root.Trim().ToUpperInvariant();
            var found = await archive.ContractsAsync(name, null, cancel).ConfigureAwait(false);

            if (!found.IsSuccess)
                return found.Error!;

            return new ContractsResult(name, [.. ContractsCommand.Select(found.Value, spreads, active, DateTimeOffset.UtcNow)
                .Select(c => new ContractEntry(c.Symbol, c.RawSymbol, c.InstrumentId, c.IsSpread, c.FirstTrade, c.Expiration, c.TickSize))]);
        });

    [McpServerTool(Name = "rolls", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("When a continuous series changes contract, like dbnf rolls: each roll's first trade date on the new contract, "
        + "the old and new contracts, their settlements the session before, and the gap between them. "
        + "ES.v.0 follows volume, ES.c.0 the calendar, ES.n.0 open interest; a bare root means ROOT.v.0. At most 366 days at a time.")]
    public Task<RollsResult> RollsAsync(
        [Description("A continuous name such as ES.v.0, or a root such as ES.")] string series,
        [Description("The first trade date.")] DateOnly from,
        [Description("The last trade date, included; today by default.")] DateOnly? to = null,
        CancellationToken cancel = default) =>
        host.RunAsync<RollsResult>(async archive =>
        {
            var spec = RollsCommand.Series(SymbolSpec.Parse(series) is { IsSuccess: true, Value.Kind: SymbolKind.Root } root
                ? $"{root.Value.Root}.v.0"
                : series);

            if (!spec.IsSuccess)
                return spec.Error!;

            var last = to ?? Today;

            if (Span(from, last, MostRollDays) is { } bad)
                return bad;

            var span = new DateSpan(from, last);
            var schedule = await RollSchedule.BuildAsync(archive, spec.Value, span, null, null, cancel).ConfigureAwait(false);

            if (!schedule.IsSuccess)
                return schedule.Error!;

            return new RollsResult(spec.Value.Text, from, last, schedule.Value.On(from)?.Symbol,
                [.. schedule.Value.Rolls.Select(r => new RollEntry(r.TradeDate, r.From.Symbol, r.To.Symbol, r.FromSettle, r.ToSettle, r.Gap))]);
        });

    [McpServerTool(Name = "trading_calendar", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("A root's CME Globex sessions over a range of trade dates, from DbnSuite's rule calendar: each weekday's open and "
        + "close (Chicago time), early closes, late opens, holidays with no session, halts, and regular trading hours (RTH). "
        + "A trade date's session opens the evening before. It covers the US equity index futures (ES, MES, NQ, MNQ, YM, MYM, RTY, M2K). "
        + "It needs no archive. At most 366 days at a time.")]
    public CalendarResult TradingCalendar(
        [Description("A root, such as ES.")] string root,
        [Description("The first trade date.")] DateOnly from,
        [Description("The last trade date, included; the same as from by default.")] DateOnly? to = null)
    {
        var name = root.Trim().ToUpperInvariant();
        var last = to ?? from;
        var calendar = RuleCalendar.CmeUsEquityIndex;

        if (Span(from, last, MostCalendarDays) is { } bad)
            throw host.Refuse(bad);

        if (!calendar.Roots.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            throw host.Refuse(new Error(CalendarErrors.NotCovered,
                $"The trading calendar covers {string.Join(", ", calendar.Roots)}; it has nothing for {name} yet."));
        }

        var days = new List<CalendarDay>();

        for (var date = from; date <= last; date = date.AddDays(1))
        {
            if (!calendar.Session.TradingDays.Contains(date.DayOfWeek))
                continue;

            var changes = calendar.ChangesOn(date);
            var notes = changes.Select(c => new CalendarNote(Kind(c.Kind), c.Note, c.Source)).ToList();
            var found = calendar.Find(name, date);

            if (!found.IsSuccess)
            {
                days.Add(new CalendarDay(date, false, found.Error!.Message, null, null, false, false, [], null, null, notes));
                continue;
            }

            var session = found.Value;

            days.Add(new CalendarDay(date, true, null,
                Local(session.Open, calendar.Zone), Local(session.Close, calendar.Zone),
                changes.Any(c => c.Kind == CalendarChangeKind.EarlyClose),
                changes.Any(c => c.Kind == CalendarChangeKind.LateOpen),
                [.. session.Breaks.Select(b => new CalendarHalt(Local(b.Start, calendar.Zone), Local(b.End, calendar.Zone)))],
                session.RthOpen is { } rthOpen ? Local(rthOpen, calendar.Zone) : null,
                session.RthClose is { } rthClose ? Local(rthClose, calendar.Zone) : null,
                notes));
        }

        return new CalendarResult(name, calendar.Id, Zones.Default, days);
    }

    [McpServerTool(Name = "tape_catalog", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("A root's tape catalog, like dbnf tape catalog shows it but never building one: for each trade date, the one "
        + "contract its tapes hold (the one that traded the most the session before), whether it is a full ETH day (only those get "
        + "tapes) or why not, and which kinds of tape exist (s1, trade, l1). It reads the --tapes folder when the server has one, "
        + "else the archive's own store, which holds tapes when the login is a tape store's. By default the catalog's last 30 "
        + "trade dates; at most 366 at a time.")]
    public async Task<TapeCatalogResult> TapeCatalogAsync(
        [Description("A root, such as ES.")] string root,
        [Description("The first trade date.")] DateOnly? from = null,
        [Description("The last trade date, included.")] DateOnly? to = null,
        CancellationToken cancel = default)
    {
        var name = root.Trim().ToUpperInvariant();
        var dataset = Sessions.Dataset;
        string source;
        Catalog catalog;
        Dictionary<string, HashSet<DateOnly>> held = [];

        if (host.Tapes is { } folder)
        {
            source = Catalog.PathOf(folder, dataset, name);

            if (!File.Exists(source))
                throw host.Refuse(NoCatalog(name, $"{folder} holds no catalog for {name}."));

            catalog = Loaded(await Catalog.LoadAsync(source, cancel).ConfigureAwait(false));

            foreach (var kind in TapePaths.Kinds)
                held[kind] = [.. TapePaths.Dates(folder, dataset, KindOf(kind), name)];
        }
        else
        {
            (source, catalog, held) = await host.RunAsync<(string, Catalog, Dictionary<string, HashSet<DateOnly>>)>(async archive =>
            {
                var key = $"{archive.Dataset}/catalog/{name}.catalog.json";
                var stored = await archive.Store.FindAsync(key, cancel).ConfigureAwait(false);

                if (stored is null)
                {
                    return NoCatalog(name, $"The archive's store holds no catalog for {name}. Tapes live in a tape store: "
                        + "start dbnf mcp with --profile naming the tape store's login, or with --tapes naming a folder of tapes.");
                }

                var path = await archive.FetchAsync(stored, null, cancel).ConfigureAwait(false);
                var loaded = await Catalog.LoadAsync(path, cancel).ConfigureAwait(false);

                if (!loaded.IsSuccess)
                    return loaded.Error!;

                var kinds = new Dictionary<string, HashSet<DateOnly>>();

                foreach (var kind in TapePaths.Kinds)
                {
                    var dates = new HashSet<DateOnly>();

                    await foreach (var tape in archive.Store.ObjectsAsync(TapePaths.KindFolder("", archive.Dataset, kind, name), cancel).ConfigureAwait(false))
                    {
                        if (TapePaths.TradeDateOf(tape.Key) is { } date)
                            dates.Add(date);
                    }

                    kinds[kind] = dates;
                }

                return (key, loaded.Value, kinds);
            }).ConfigureAwait(false);
        }

        var days = catalog.Days;
        var last = to ?? (days.Count > 0 ? days[^1].TradeDate : from ?? Today);
        var first = from ?? (days.Count > 0 ? Later(days[0].TradeDate, last.AddDays(1 - DefaultCatalogDays)) : last);

        if (Span(first, last, MostCatalogDays) is { } bad)
            throw host.Refuse(bad);

        var entries = days
            .Where(d => d.TradeDate >= first && d.TradeDate <= last)
            .Select(d =>
            {
                var contract = catalog.HeldOn(d.TradeDate);
                var volume = contract is null ? null : d.Contracts.FirstOrDefault(c => c.InstrumentId == contract.InstrumentId)?.ClearedVolume;

                return new CatalogDayEntry(d.TradeDate, d.Held, catalog.IsFullDay(d.TradeDate), catalog.Excluded(d.TradeDate), volume,
                    [.. TapePaths.Kinds.Where(k => held.TryGetValue(k, out var dates) && dates.Contains(d.TradeDate))]);
            })
            .ToList();

        return new TapeCatalogResult(name, catalog.Dataset, source,
            days.Count > 0 ? days[0].TradeDate : null, days.Count > 0 ? days[^1].TradeDate : null,
            catalog.Contracts.Count, entries);
    }

    [McpServerTool(Name = "tape_info", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("One tape's header, like dbnf tape info: its contract, kind, trade date, session open and close (Chicago time), "
        + "where RTH starts and ends, how many events it holds and its size. It reads only the header, never the events. "
        + "The tape is looked for in the --tapes folder when the server has one, else in the archive's own store.")]
    public async Task<TapeInfoResult> TapeInfoAsync(
        [Description("A root, such as ES.")] string root,
        [Description("The trade date.")] DateOnly date,
        [Description("The kind of tape: s1 (one-second bars), trade (every trade) or l1 (top of book, trades and trading state).")] string kind,
        CancellationToken cancel = default)
    {
        var name = root.Trim().ToUpperInvariant();
        var kindName = kind.Trim().ToLowerInvariant();

        if (!TapePaths.Kinds.Contains(kindName))
            throw host.Refuse(new Error("input.bad_tape_kind", $"\"{kind}\" is not a kind. A kind is s1, trade or l1."));

        if (host.Tapes is { } folder)
        {
            var path = TapePaths.For(folder, Sessions.Dataset, KindOf(kindName), name, date);

            if (!File.Exists(path))
                throw host.Refuse(NoTape($"{folder} holds no {kindName} tape of {name} for {Text.Day(date)}."));

            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, TapeHeader.Size, useAsync: true);

            await using (file.ConfigureAwait(false))
                return Info(path, Header(await Tape.ReadHeaderAsync(file, cancel).ConfigureAwait(false)));
        }

        return await host.RunAsync<TapeInfoResult>(async archive =>
        {
            var key = Text.Invariant($"{TapePaths.KindFolder("", archive.Dataset, kindName, name)}{date.Year}/{name}.{kindName}.{date:yyyyMMdd}.tape");

            if (await archive.Store.FindAsync(key, cancel).ConfigureAwait(false) is null)
                return NoTape($"The archive's store holds no {kindName} tape of {name} for {Text.Day(date)}.");

            var stream = await archive.Store.OpenAsync(key, 0, cancel).ConfigureAwait(false);

            await using (stream.ConfigureAwait(false))
            {
                var header = await Tape.ReadHeaderAsync(stream, cancel).ConfigureAwait(false);

                return header.IsSuccess ? Info(key, header.Value) : header.Error!;
            }
        }).ConfigureAwait(false);
    }

    [McpServerTool(Name = "volume_by_day", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("Each trade date's total volume for a root, and its most traded contract: daily totals, never individual trades. "
        + "Volume is the archive's ohlcv-1s bars of outright contracts (spreads left out) summed over each CME trade date, "
        + "17:00 to 17:00 Chicago time; the leader is what dbnf's ES.v.0 holds, and what tapes hold, the trade date after. "
        + "It reads one ohlcv-1s file a UTC day, so the first look at a range downloads them. At most 92 days at a time.")]
    public Task<VolumeResult> VolumeByDayAsync(
        [Description("A root, such as ES.")] string root,
        [Description("The first trade date.")] DateOnly from,
        [Description("The last trade date, included; the same as from by default.")] DateOnly? to = null,
        CancellationToken cancel = default) =>
        host.RunAsync<VolumeResult>(async archive =>
        {
            var name = root.Trim().ToUpperInvariant();
            var last = to ?? from;

            if (Span(from, last, MostVolumeDays) is { } bad)
                return bad;

            if (Sessions.Unsupported(archive.Dataset) is { } unsupported)
                return unsupported;

            var span = new DateSpan(from, last);
            var listed = await archive.ContractsAsync(name, null, cancel).ConfigureAwait(false);

            if (listed.Error is { Code: not Errors.NotStored } unread)
                return unread;

            var contracts = listed.IsSuccess ? listed.Value.ToDictionary(c => c.InstrumentId) : [];
            var volumes = new SortedDictionary<DateOnly, Dictionary<string, long>>();
            var missing = new List<DateOnly>();
            var utc = Sessions.UtcDays(span);

            for (var day = utc.From; day <= utc.To; day = day.AddDays(1))
            {
                var opened = await archive.OpenAsync(BarSchema, name, day, cancel).ConfigureAwait(false);

                if (!opened.IsSuccess)
                {
                    if (opened.Error!.Code == Errors.NotStored)
                        missing.Add(day);
                    else if (opened.Error.Code != Errors.NoData)
                        return opened.Error;

                    continue;
                }

                await using var reader = opened.Value;

                await foreach (var record in reader.ReadAsync(cancel).ConfigureAwait(false))
                {
                    if (record is not OhlcvRecord bar)
                        continue;

                    var tradeDate = Sessions.TradeDate(bar.TsEvent);

                    if (!span.Contains(tradeDate))
                        continue;

                    var contract = contracts.GetValueOrDefault(bar.InstrumentId);
                    var symbol = contract?.Symbol ?? reader.Metadata.SymbolOf(bar.InstrumentId);

                    // Without a definition, a spread still shows in its name, which joins its legs.
                    if (contract?.IsSpread ?? symbol.Contains('-', StringComparison.Ordinal))
                        continue;

                    if (!volumes.TryGetValue(tradeDate, out var byContract))
                        volumes[tradeDate] = byContract = [];

                    byContract[symbol] = byContract.GetValueOrDefault(symbol) + (long)bar.Volume;
                }
            }

            var days = volumes
                .Where(v => v.Value.Values.Sum() > 0)
                .Select(v =>
                {
                    var leader = v.Value.OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal).First();

                    return new VolumeDay(v.Key, v.Value.Values.Sum(), leader.Key, leader.Value);
                })
                .ToList();

            return new VolumeResult(name, from, last,
                "ohlcv-1s bars of outright contracts, summed by CME trade date", days, missing);
        });

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private static DateOnly Later(DateOnly a, DateOnly b) => a > b ? a : b;

    /// <summary>A range back to front, or longer than a tool reads at once, is refused.</summary>
    private static Error? Span(DateOnly from, DateOnly to, int most)
    {
        if (from > to)
            return new Error("input.bad_range", $"The range starts {Text.Day(from)}, after it ends ({Text.Day(to)}).");

        return to.DayNumber - from.DayNumber + 1 > most
            ? new Error("input.bad_range", Text.Invariant($"{Text.Day(from)} to {Text.Day(to)} is more than {most} days; ask for less at a time."))
            : null;
    }

    private static DateTimeOffset Local(long nanos, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(DateTimeOffset.UnixEpoch.AddTicks(nanos / 100), zone);

    private static string Kind(CalendarChangeKind kind) => kind switch
    {
        CalendarChangeKind.Closed => "closed",
        CalendarChangeKind.EarlyClose => "early_close",
        CalendarChangeKind.LateOpen => "late_open",
        CalendarChangeKind.Halt => "halt",
        _ => "regular"
    };

    private static TapeKind KindOf(string name) => name switch
    {
        "s1" => TapeKind.S1,
        "trade" => TapeKind.Trade,
        _ => TapeKind.L1
    };

    private static string NameOf(TapeKind kind) => kind switch
    {
        TapeKind.S1 => "s1",
        TapeKind.Trade => "trade",
        _ => "l1"
    };

    private static Error NoCatalog(string root, string message) => new("tape.no_catalog", message);

    private static Error NoTape(string message) => new("tape.not_found", message);

    private Catalog Loaded(Result<Catalog> loaded) => loaded.IsSuccess ? loaded.Value : throw host.Refuse(loaded.Error!);

    private TapeHeader Header(Result<TapeHeader> header) => header.IsSuccess ? header.Value : throw host.Refuse(header.Error!);

    private static TapeInfoResult Info(string source, TapeHeader h)
    {
        var chicago = Zones.Find(Zones.Default).Value;

        return new TapeInfoResult(source, h.Symbol, h.Root, NameOf(h.Kind), h.Dataset, h.TradeDate, h.InstrumentId,
            h.TickRaw / 1_000_000_000m, Local(h.OpenTime, chicago), Local(h.CloseTime, chicago), h.HasRth,
            h.HasRth ? h.RthStart.EventIndex : null, h.HasRth ? h.RthEndIndex : null,
            h.EventCount, h.BodyLength, h.StoredLength, h.IsCompressed, [.. TapeCommand.Describe(h)]);
    }

    /// <summary>A location as given, less any user name or password written into it.</summary>
    private static string WithoutUser(string location) =>
        Uri.TryCreate(location, UriKind.Absolute, out var uri) && uri.UserInfo.Length > 0 && !uri.IsFile
            ? new UriBuilder(uri) { UserName = "", Password = "" }.Uri.ToString()
            : location;
}
