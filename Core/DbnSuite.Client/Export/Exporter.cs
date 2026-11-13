using DbnSuite.Client.Archive;
using DbnSuite.Client.Contracts;
using DbnSuite.Client.Dbn;
using DbnSuite.Client.Time;

namespace DbnSuite.Client.Export;

/// <summary>
/// Turns raw days into the files asked for: the records of a root, a contract
/// or a continuous series, kept to the trade dates and hours asked for,
/// optionally as bars, in any format.
/// </summary>
public sealed class Exporter(DbnArchive archive)
{
    private const long SecondsInAMinute = 60;

    private const string Mbo = "mbo";

    /// <param name="progress">Told each UTC day as it is read.</param>
    public async Task<Result<ExportSummary>> RunAsync(ExportRequest request, IProgress<DateOnly>? progress,
        CancellationToken cancel)
    {
        try
        {
            return await ExportAsync(request, progress, cancel).ConfigureAwait(false);
        }
        catch (ArchiveException e)
        {
            return e.Error;
        }
    }

    private async Task<Result<ExportSummary>> ExportAsync(ExportRequest request, IProgress<DateOnly>? progress,
        CancellationToken cancel)
    {
        if ((Sessions.Unsupported(archive.Dataset) ?? Check(request)) is { } problem)
            return problem;

        var symbol = request.Symbol;
        var continuous = symbol.Kind == SymbolKind.Continuous;
        var fromTrades = request.Schema == Mbo;
        var bars = request.Bars is not null;

        RollSchedule? schedule = null;

        if (continuous && !fromTrades)
        {
            var built = await RollSchedule.BuildAsync(archive, symbol, request.TradeDates, request.RollDays, null, cancel)
                .ConfigureAwait(false);

            if (!built.IsSuccess)
                return built.Error!;

            schedule = built.Value;

            // The earliest prices need every roll, so a missing gap stops the export before a file is written.
            if (schedule.Adjuster(request.TradeDates.From, request.Adjustment).Error is { } unadjusted)
                return unadjusted;
        }

        MboTransform? transform = null;

        if (request.As is { } target)
        {
            var created = MboTransform.Create(target, continuous);

            if (!created.IsSuccess)
                return created.Error!;

            transform = created.Value;
        }

        var all = transform is not null
            ? transform.Columns
            : bars
                ? BarBuilder.Columns(continuous, trades: fromTrades)
                : RecordColumns.For(request.Schema, continuous);

        if (OutputFiles.Unknown(request.Columns, all) is { Count: > 0 } unknown)
        {
            return new Error("input.bad_columns",
                $"No column {string.Join(", ", unknown)}. The columns are {string.Join(", ", all.Select(c => c.Name))}.");
        }

        var files = new OutputFiles(request, all, request.As ?? request.Bars?.Text ?? request.Schema);
        var cells = new Cell[all.Count];
        var missing = new List<DateOnly>();
        var contractsBySymbol = new Dictionary<string, Contract>();
        var pending = new List<Bar>();
        var rows = new List<(DateOnly Date, Cell[] Cells)>();
        DateOnly? writing = null;

        // A schema with no columns of its own is written by its records' headers; in any other, such a record is not data.
        var headersOnly = all.Any(c => c.Name == "rtype");

        var builder = request.Bars is { } spec
            ? new BarBuilder(spec, request.Sessions, pending.Add)
            : null;

        Func<decimal?, decimal?> PriceOf(Contract? contract, DateOnly tradeDate)
        {
            var adjust = schedule?.Adjuster(tradeDate, request.Adjustment).Value ?? (p => p);
            var tick = contract?.TickSize;

            return request.Prices switch
            {
                PriceMode.Ticks when tick is > 0 => p => adjust(p) / tick,

                // Without a tick size a price can't be put in ticks, and writing it as a decimal
                // would mix units in one column, so the export stops at the first such price.
                PriceMode.Ticks => p => p is null
                    ? null
                    : throw new ArchiveException(new Error(Errors.NoTickSize,
                        $"{contract?.Symbol ?? "An instrument"} on {tradeDate:yyyy-MM-dd} has no known tick size, so its prices can't be written in ticks. " +
                        "Its definition is missing for that day; export decimal prices instead.")),
                PriceMode.Raw => p => adjust(p) * 1_000_000_000m,
                _ => adjust
            };
        }

        async ValueTask WriteBarsAsync()
        {
            foreach (var bar in pending)
            {
                BarBuilder.Fill(bar, fromTrades, PriceOf(contractsBySymbol.GetValueOrDefault(bar.Symbol), bar.TradeDate), cells);
                await files.WriteAsync(bar.TradeDate, cells, ReadOnlyMemory<byte>.Empty).ConfigureAwait(false);
            }

            pending.Clear();
        }

        void Keep(DateOnly date, Cell[] row) => rows.Add((date, row));

        async ValueTask WriteRowsAsync()
        {
            foreach (var (date, row) in rows)
                await files.WriteAsync(date, row, ReadOnlyMemory<byte>.Empty).ConfigureAwait(false);

            rows.Clear();
        }

        await using (files.ConfigureAwait(false))
        {
            var utcDays = Sessions.UtcDays(request.TradeDates);

            for (var day = utcDays.From; day <= utcDays.To; day = day.AddDays(1))
            {
                var path = await archive.FetchAsync(request.Schema, symbol.Root, day, null, cancel).ConfigureAwait(false);

                if (!path.IsSuccess)
                {
                    if (path.Error!.Code == Errors.NotStored)
                        missing.Add(day);
                    else if (path.Error.Code != Errors.NoData)
                        return path.Error;

                    continue;
                }

                var instruments = await archive.InstrumentsAsync(symbol.Root, day, cancel).ConfigureAwait(false);

                if (instruments.Error is { Code: not (Errors.NotStored or Errors.NoData) } unread)
                    return unread;

                var contracts = instruments.IsSuccess ? instruments.Value : new Dictionary<uint, Contract>();

                // Without definitions a contract can't be told from its neighbours: say so, rather than write nothing.
                if (symbol.Kind == SymbolKind.Contract && contracts.Count == 0)
                {
                    missing.Add(day);

                    continue;
                }

                foreach (var c in contracts.Values)
                    contractsBySymbol[c.Symbol] = c;

                var opened = await archive.ReadAsync(request.Schema, symbol.Root, day, path.Value, cancel).ConfigureAwait(false);

                if (!opened.IsSuccess)
                    return opened.Error!;

                await using var reader = opened.Value;

                files.Metadata = reader.Metadata.Raw;

                var wanted = Wanted(request, reader.Metadata, contracts);

                await foreach (var raw in reader.ReadRawAsync(cancel).ConfigureAwait(false))
                {
                    // Filter on the header before decoding: most records of a day are not wanted.
                    var id = DbnRecord.InstrumentOf(raw.Span);

                    var accepted = request.Sessions.Accept(DbnRecord.IndexTimeOf(raw.Span));

                    if (wanted is not null)
                    {
                        if (!wanted.Contains(id))
                            continue;
                    }
                    else if (schedule is not null && (accepted is not { } on || schedule.On(on)?.InstrumentId != id))
                    {
                        continue;
                    }

                    // A transformation's book needs every event, even those outside the hours written.
                    if (transform is not null)
                    {
                        if (raw.Span[1] != MboRecord.Type || DbnRecord.Decode(raw.Span) is not MboRecord m)
                            continue;

                        var held = contracts.GetValueOrDefault(id);

                        if (symbol.Kind == SymbolKind.Root && !request.Spreads && held?.IsSpread == true)
                            continue;

                        var named = held?.Symbol ?? reader.Metadata.SymbolOf(id);

                        // Files take one trade date at a time, so finish the last one before the next begins.
                        if (accepted is { } starting && starting != writing)
                        {
                            writing = starting;
                            transform.Flush(starting, Keep);
                        }

                        transform.Process(m, accepted, named, continuous ? symbol.Text : null,
                            PriceOf(held, accepted ?? default), Keep);

                        await WriteRowsAsync().ConfigureAwait(false);
                        continue;
                    }

                    if (accepted is not { } tradeDate)
                        continue;

                    // Bars from mbo need only its trades.
                    if (builder is not null && raw.Span[1] == MboRecord.Type && raw.Length == MboRecord.Size && raw.Span[38] != (byte)'T')
                        continue;

                    var record = DbnRecord.Decode(raw.Span);

                    if (record is OtherRecord && !headersOnly)
                        continue;

                    var contract = contracts.GetValueOrDefault(id);

                    if (contract is null && schedule?.On(tradeDate) is { } scheduled)
                        contract = scheduled;

                    if (symbol.Kind == SymbolKind.Root && !request.Spreads && contract?.IsSpread == true)
                        continue;

                    var name = contract?.Symbol ?? reader.Metadata.SymbolOf(id);
                    var series = continuous ? symbol.Text : null;

                    if (builder is not null)
                    {
                        var key = series ?? name;

                        // Files take one trade date at a time, so finish the last one before the next begins.
                        if (tradeDate != writing)
                        {
                            writing = tradeDate;
                            builder.Flush(tradeDate);
                        }

                        if (record is OhlcvRecord { Open: { } o, High: { } h, Low: { } l, Close: { } c } bar)
                            builder.Add(key, name, series, tradeDate, bar.TsEvent, o, h, l, c, (long)bar.Volume, 0);
                        else if (record is MboRecord { Action: 'T', Price: { } p } trade)
                            builder.AddTrade(key, name, series, tradeDate, trade.TsRecv, p, trade.Quantity);

                        if (pending.Count > 0)
                            await WriteBarsAsync().ConfigureAwait(false);

                        continue;
                    }

                    RecordColumns.Fill(record, name, series, PriceOf(contract, tradeDate), cells);
                    await files.WriteAsync(tradeDate, cells, raw).ConfigureAwait(false);
                }

                progress?.Report(day);
            }

            builder?.Flush();
            await WriteBarsAsync().ConfigureAwait(false);

            transform?.Flush(null, Keep);
            await WriteRowsAsync().ConfigureAwait(false);

            await files.CompleteAsync().ConfigureAwait(false);
        }

        return new ExportSummary(files.Files, files.Rows, missing, schedule?.Rolls ?? []);
    }

    /// <summary>
    /// The instrument ids a file's records are kept for, or null when every
    /// record is (a root) or a roll schedule decides per trade date.
    /// </summary>
    private static HashSet<uint>? Wanted(ExportRequest request, DbnMetadata metadata, IReadOnlyDictionary<uint, Contract> contracts) =>
        request.Symbol.Kind switch
        {
            SymbolKind.Root => null,
            SymbolKind.Contract => [.. contracts.Values
                .Where(c => string.Equals(c.Symbol, request.Symbol.Text, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(c.RawSymbol, request.Symbol.Text, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.InstrumentId)],

            // mbo was fetched as ES.n.0 and ES.n.1, so its own mapping names the series.
            _ when request.Schema == Mbo => [.. metadata.Symbols
                .Where(s => string.Equals(s.Value, request.Symbol.Text, StringComparison.OrdinalIgnoreCase))
                .Select(s => s.Key)],
            _ => null
        };

    private static Error? Check(ExportRequest request)
    {
        var continuous = request.Symbol.Kind == SymbolKind.Continuous;

        if (continuous && request.Schema == Mbo && request.Symbol.Rule != 'n' || continuous && request.Schema == Mbo && request.Symbol.Rank > 1)
            return new Error("input.bad_series", $"mbo is kept for {request.Symbol.Root}.n.0 and {request.Symbol.Root}.n.1 only.");

        // Order book events keep the prices they happened at; no roll schedule is built for them.
        if (continuous && request.Schema == Mbo && request.Adjustment != Adjustment.None)
            return new Error("input.bad_adjustment", "--adjust is for bars and records built on a roll schedule; mbo keeps raw prices, so an mbo series cannot be adjusted.");

        // A NinjaTrader file is one instrument, with no symbol in its lines: a root's contracts and
        // spreads would be mixed into one series.
        if (request.Format == OutputFormat.NinjaTrader && request.Symbol.Kind == SymbolKind.Root)
        {
            return new Error("input.bad_format",
                $"NinjaTrader output is one instrument a file; name a contract, such as {request.Symbol.Root}Z26, or a continuous series, such as {request.Symbol.Root}.c.0.");
        }

        if (request.Format == OutputFormat.NinjaTrader && request.Columns.Count > 0)
            return new Error("input.bad_format", "NinjaTrader output has a fixed layout; --columns is for the other formats.");

        if (request.As is { } target)
        {
            if (request.Schema != Mbo)
                return new Error("input.bad_as", $"--as builds {target} from mbo, so the schema must be mbo.");

            if (request.Bars is not null)
                return new Error("input.bad_as", "--as and --bars cannot both be given; bars come from mbo's trades on their own.");

            if (request.Format == OutputFormat.Dbn)
                return new Error("input.bad_format", $"DBN output copies records as stored; write {target} as CSV, JSON Lines or Parquet.");

            if (request.Format == OutputFormat.NinjaTrader && target != "trades")
                return new Error("input.bad_format", "NinjaTrader output takes bars, or --as trades for ticks.");

            return null;
        }

        if (request.Bars is { } bars)
        {
            if (bars.FromTrades && request.Schema != Mbo)
                return new Error("input.bad_bars", $"{bars.Text} bars count trades, so they come from mbo.");

            // An hour or a UTC day doesn't line up with a session, and a coarser bar can't be split.
            if (request.Schema is not (Mbo or "ohlcv-1s" or "ohlcv-1m"))
                return new Error("input.bad_bars", "Bars are built from ohlcv-1s, or from mbo's trades.");

            if (request.Schema == "ohlcv-1m" && bars.Unit == BarUnit.Seconds && bars.Size % SecondsInAMinute != 0)
                return new Error("input.bad_bars", $"{bars.Text} bars can't be built from ohlcv-1m; use ohlcv-1s.");

            if (request.Format == OutputFormat.Dbn)
                return new Error("input.bad_format", "DBN output copies records as stored; write bars as CSV, JSON Lines, Parquet or NinjaTrader.");
        }
        else if (request.Format == OutputFormat.NinjaTrader)
        {
            return new Error("input.bad_format", "NinjaTrader output is for bars; add --bars, such as --bars 1m.");
        }

        if (request.Format == OutputFormat.Dbn && request.Columns.Count > 0)
            return new Error("input.bad_format", "DBN output keeps every field; --columns is for the other formats.");

        if (continuous && request.Adjustment != Adjustment.None && request.Format == OutputFormat.Dbn)
            return new Error("input.bad_format", "DBN output copies prices as traded, so it cannot be adjusted.");

        // A DBN file has one symbol map, and a series changes instrument at each roll.
        if (continuous && request.Format == OutputFormat.Dbn && request.Layout == FileLayout.Single
            && request.TradeDates.From != request.TradeDates.To)
        {
            return new Error("input.bad_format", "A DBN file has one symbol map, so a series over several days takes one file a day.");
        }

        return null;
    }
}
