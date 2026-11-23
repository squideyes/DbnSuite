using System.CommandLine;
using System.Diagnostics;
using DbnSuite.Client;
using DbnSuite.Client.Contracts;
using DbnSuite.Client.Export;
using DbnSuite.Client.Time;

namespace DbnSuite.Fetch.Commands;

/// <summary>
/// <c>dbnf export ohlcv-1s ES.v.0 --bars 5m --session rth --last 1y</c>: the
/// records of a root, a contract or a continuous series, kept to the trade
/// dates and hours asked for, as records or bars, in the format asked for.
/// </summary>
public static class ExportCommand
{
    public static Command Create(Session session)
    {
        var schema = new Argument<string>("schema") { Description = "The schema to read, such as ohlcv-1s, mbo or statistics." };
        var symbol = new Argument<string>("symbol") { Description = "A root (ES), a contract (ESZ26) or a continuous name (ES.v.0, ES.c.0, ES.n.0)." };
        var range = new RangeOptions();
        var output = new Option<string>("--out") { Description = "The folder to write into.", DefaultValueFactory = _ => "." };
        var format = new Option<string>("--format") { Description = "csv, parquet, jsonl, dbn or nt8 (NinjaTrader 8).", DefaultValueFactory = _ => "csv" };
        var compress = new Option<string>("--compress") { Description = "none, gzip or zstd, for csv, jsonl and dbn.", DefaultValueFactory = _ => "none" };
        var zone = new Option<string>("--tz") { Description = "The time zone times are written in.", DefaultValueFactory = _ => Zones.Default };
        var time = new Option<string>("--time") { Description = "iso, unix-ns, unix-ms or excel.", DefaultValueFactory = _ => "iso" };
        var splitTime = new Option<bool>("--split-time") { Description = "Write each time as a date column and a time column, in CSV and JSON Lines." };
        var price = new Option<string>("--price") { Description = "decimal, ticks or raw (Databento's integers).", DefaultValueFactory = _ => "decimal" };
        var sessionKind = new Option<string>("--session") { Description = "eth (the whole Globex session) or rth (regular hours).", DefaultValueFactory = _ => "eth" };
        var hours = new Option<string?>("--hours") { Description = "Hours of your own instead, such as 09:30-16:00." };
        var hoursZone = new Option<string>("--hours-tz") { Description = "The zone --hours is in.", DefaultValueFactory = _ => Zones.Default };
        var bars = new Option<string?>("--bars") { Description = "Bars instead of records: 5s, 1m, 15m, 1h, 1d, 1w, 1mo, or from mbo 500t (trades), 1000v (volume)." };
        var adjust = new Option<string>("--adjust") { Description = "For a continuous series other than mbo: none, difference (back-adjusted) or ratio.", DefaultValueFactory = _ => "none" };
        var rollDays = new Option<int?>("--roll-days") { Description = "For .c. series: roll this many days before expiry (before the delivery month for gold) instead of the root's own rule." };
        var single = new Option<bool>("--single") { Description = "One file for the whole range, not one per trade date." };
        var partition = new Option<bool>("--partition") { Description = "series=/date= folders that DuckDB and Polars read as one table." };
        var name = new Option<string?>("--name") { Description = "File names of your own: {series}, {kind}, {date}, {from}, {to}, {ext}." };
        var columns = new Option<string?>("--columns") { Description = "Only these columns, in this order, such as ts_event,price,size." };
        var noSpreads = new Option<bool>("--no-spreads") { Description = "For a root: leave out spreads." };
        var asSchema = new Option<string?>("--as") { Description = "Build from mbo what Databento sells separately: trades, tbbo, mbp-1, mbp-10, bbo-1s or bbo-1m." };

        var command = new Command("export", "Write records or bars for trade dates, in CSV, Parquet, JSON Lines, DBN or NinjaTrader 8.")
        {
            schema, symbol, output, format, compress, zone, time, splitTime, price, sessionKind, hours, hoursZone,
            bars, asSchema, adjust, rollDays, single, partition, name, columns, noSpreads
        };

        range.AddTo(command);

        command.SetAction((parse, cancel) => session.RunAsync(parse, async archive =>
        {
            var request = Request(parse);

            if (!request.IsSuccess)
                return await session.FailAsync(request.Error!).ConfigureAwait(false);

            var r = request.Value;
            var clock = Stopwatch.StartNew();

            await session.Out.WriteLineAsync(Text.Invariant(
                $"Exporting {r.Symbol.Text} {r.Schema}{(r.Bars is { } b ? $" as {b.Text} bars" : r.As is { } a ? $" as {a}" : "")}, trade dates {Text.Day(r.TradeDates.From)} to {Text.Day(r.TradeDates.To)}...")).ConfigureAwait(false);

            var result = await new Exporter(archive).RunAsync(r, null, cancel).ConfigureAwait(false);

            if (!result.IsSuccess)
                return await session.FailAsync(result.Error!).ConfigureAwait(false);

            var summary = result.Value;

            foreach (var roll in summary.Rolls)
            {
                await session.Out.WriteLineAsync(Text.Invariant(
                    $"  rolled {Text.Day(roll.TradeDate)}: {roll.From.Symbol} to {roll.To.Symbol}{(roll.Gap is { } g ? $", gap {g:0.#########}" : "")}")).ConfigureAwait(false);
            }

            if (summary.MissingDays.Count > 0)
            {
                await session.Error.WriteLineAsync(Text.Invariant(
                    $"Not in the archive, so left out: {string.Join(", ", summary.MissingDays.Select(Text.Day))} (UTC days).")).ConfigureAwait(false);
            }

            await session.Out.WriteLineAsync(summary.Files.Count == 0
                ? "Nothing matched, so no files were written."
                : Text.Invariant($"Wrote {Text.Count(summary.Rows)} rows to {summary.Files.Count} files in {clock.Elapsed.TotalSeconds:0.0} s, in {Path.GetFullPath(r.Output)}")).ConfigureAwait(false);

            return Session.Ok;
        }, cancel));

        Result<ExportRequest> Request(ParseResult parse) => ExportCommand.Request(new ExportChoices
        {
            Schema = parse.GetValue(schema)!,
            Symbol = parse.GetValue(symbol)!,
            TradeDates = range.Resolve(parse),
            Output = parse.GetValue(output)!,
            Format = parse.GetValue(format)!,
            Compress = parse.GetValue(compress)!,
            Zone = parse.GetValue(zone)!,
            Time = parse.GetValue(time)!,
            SplitTime = parse.GetValue(splitTime),
            Price = parse.GetValue(price)!,
            Session = Given(parse, sessionKind) ? parse.GetValue(sessionKind) : null,
            Hours = parse.GetValue(hours),
            HoursZone = Given(parse, hoursZone) ? parse.GetValue(hoursZone) : null,
            Bars = parse.GetValue(bars),
            As = parse.GetValue(asSchema),
            Adjust = parse.GetValue(adjust)!,
            RollDays = parse.GetValue(rollDays),
            Single = parse.GetValue(single),
            Partition = parse.GetValue(partition),
            Name = parse.GetValue(name),
            Columns = parse.GetValue(columns),
            NoSpreads = parse.GetValue(noSpreads)
        });

        // True when the option was typed, not just left at its default.
        static bool Given(ParseResult parse, Option option) => parse.GetResult(option) is { Implicit: false };

        return command;
    }

    /// <summary>What an export asks for, as typed: the options of <c>dbnf export</c>, or the arguments of the MCP tool.</summary>
    internal sealed record ExportChoices
    {
        public required string Schema { get; init; }
        public required string Symbol { get; init; }
        public required Result<DateSpan> TradeDates { get; init; }
        public required string Output { get; init; }
        public string Format { get; init; } = "csv";
        public string Compress { get; init; } = "none";
        public string Zone { get; init; } = Zones.Default;
        public string Time { get; init; } = "iso";
        public bool SplitTime { get; init; }
        public string Price { get; init; } = "decimal";

        /// <summary>eth or rth; null when not given, which is eth.</summary>
        public string? Session { get; init; }

        public string? Hours { get; init; }

        /// <summary>The zone <see cref="Hours"/> is in; null when not given, which is Chicago.</summary>
        public string? HoursZone { get; init; }

        public string? Bars { get; init; }
        public string? As { get; init; }
        public string Adjust { get; init; } = "none";
        public int? RollDays { get; init; }
        public bool Single { get; init; }
        public bool Partition { get; init; }
        public string? Name { get; init; }
        public string? Columns { get; init; }
        public bool NoSpreads { get; init; }
    }

    /// <summary>The export the choices ask for, or the first thing wrong with them.</summary>
    internal static Result<ExportRequest> Request(ExportChoices c)
    {
        var spec = SymbolSpec.Parse(c.Symbol);
        var schemaName = Session.Schema(c.Schema);
        var span = c.TradeDates;
        var tz = Zones.Find(c.Zone);

        foreach (var error in new[] { spec.Error, schemaName.Error, span.Error, tz.Error })
        {
            if (error is not null)
                return error;
        }

        var kind = Pick(c.Session ?? "eth", "session", ("eth", SessionKind.Eth), ("rth", SessionKind.Rth));

        if (!kind.IsSuccess)
            return kind.Error!;

        (Hours, TimeZoneInfo)? custom = null;

        // An option that would be quietly ignored is refused, so nobody is misled about what they got.
        if (c.Hours is null && c.HoursZone is not null)
            return new Error("input.bad_session", "--hours-tz says which zone --hours is in, so it needs --hours.");

        if (c.Hours is not null && c.Session is not null)
            return new Error("input.bad_session", "--hours takes the place of --session, so give only one of them.");

        if (c.Hours is { } text)
        {
            var window = Hours.Parse(text);
            var windowZone = Zones.Find(c.HoursZone ?? Zones.Default);

            if (!window.IsSuccess)
                return window.Error!;

            if (!windowZone.IsSuccess)
                return windowZone.Error!;

            custom = (window.Value, windowZone.Value);
        }

        var sessions = SessionFilter.Create(span.Value, custom is null ? kind.Value : SessionKind.Custom, spec.Value.Root, custom);

        if (!sessions.IsSuccess)
            return sessions.Error!;

        BarSpec? barSpec = null;

        if (c.Bars is { } barText)
        {
            var parsed = BarSpec.Parse(barText);

            if (!parsed.IsSuccess)
                return parsed.Error!;

            barSpec = parsed.Value;
        }

        var outputFormat = Pick(c.Format, "format", ("csv", OutputFormat.Csv), ("parquet", OutputFormat.Parquet),
            ("jsonl", OutputFormat.JsonLines), ("dbn", OutputFormat.Dbn), ("nt8", OutputFormat.NinjaTrader));
        var compression = Pick(c.Compress, "compression", ("none", Compression.None), ("gzip", Compression.Gzip), ("zstd", Compression.Zstd));
        var timeMode = Pick(c.Time, "time", ("iso", TimeMode.Iso), ("unix-ns", TimeMode.UnixNanos),
            ("unix-ms", TimeMode.UnixMillis), ("excel", TimeMode.Excel));
        var priceMode = Pick(c.Price, "price", ("decimal", PriceMode.Decimal), ("ticks", PriceMode.Ticks), ("raw", PriceMode.Raw));
        var adjustment = Pick(c.Adjust, "adjustment", ("none", Adjustment.None), ("difference", Adjustment.Difference), ("ratio", Adjustment.Ratio));

        foreach (var error in new[] { outputFormat.Error, compression.Error, timeMode.Error, priceMode.Error, adjustment.Error })
        {
            if (error is not null)
                return error;
        }

        if (c.Single && c.Partition)
            return new Error("input.bad_layout", "--single and --partition cannot both be given.");

        if (c.Partition && c.Name is not null)
            return new Error("input.bad_layout", "--partition names the files itself, so --name cannot be given with it.");

        if (compression.Value != Compression.None && outputFormat.Value is OutputFormat.Parquet or OutputFormat.NinjaTrader)
        {
            return new Error("input.bad_compression", outputFormat.Value == OutputFormat.Parquet
                ? "--compress is for csv, jsonl and dbn. A Parquet file is always compressed inside."
                : "--compress is for csv, jsonl and dbn. NinjaTrader imports plain text only.");
        }

        if (c.SplitTime && timeMode.Value != TimeMode.Iso)
            return new Error("input.bad_time", "--split-time splits an iso time into a date and a time, so it cannot be given with another --time.");

        return new ExportRequest
        {
            Schema = schemaName.Value,
            Symbol = spec.Value,
            TradeDates = span.Value,
            Sessions = sessions.Value,
            Output = c.Output,
            Format = outputFormat.Value,
            Compression = compression.Value,
            Layout = c.Single ? FileLayout.Single : c.Partition ? FileLayout.Partitioned : FileLayout.PerDay,
            NameTemplate = c.Name,
            Zone = tz.Value,
            Time = timeMode.Value,
            SplitTime = c.SplitTime,
            Prices = priceMode.Value,
            Columns = c.Columns is { } list ? [.. PullCommand.Split(list, lower: true)] : [],
            Bars = barSpec,
            As = c.As?.ToLowerInvariant(),
            Adjustment = adjustment.Value,
            RollDays = c.RollDays,
            Spreads = !c.NoSpreads
        };
    }

    /// <summary>One of a few named choices, or an error listing them.</summary>
    private static Result<T> Pick<T>(string text, string what, params (string Name, T Value)[] choices)
    {
        foreach (var (choice, value) in choices)
        {
            if (string.Equals(text, choice, StringComparison.OrdinalIgnoreCase))
                return value;
        }

        return new Error($"input.bad_{what}", $"\"{text}\" is not a valid {what}; use {string.Join(", ", choices.Select(c => c.Name))}.");
    }
}
