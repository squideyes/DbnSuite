using System.CommandLine;
using System.Diagnostics;
using DbnClient;
using DbnClient.Contracts;
using DbnClient.Export;
using DbnClient.Time;

namespace DbnFetch.Commands;

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
        var rollDays = new Option<int>("--roll-days") { Description = "For .c. series: roll this many days before expiry.", DefaultValueFactory = _ => RollSchedule.DefaultRollDays };
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

        Result<ExportRequest> Request(ParseResult parse)
        {
            var spec = SymbolSpec.Parse(parse.GetValue(symbol)!);
            var schemaName = Session.Schema(parse.GetValue(schema)!);
            var span = range.Resolve(parse);
            var tz = Zones.Find(parse.GetValue(zone)!);

            foreach (var error in new[] { spec.Error, schemaName.Error, span.Error, tz.Error })
            {
                if (error is not null)
                    return error;
            }

            var kind = Pick(parse.GetValue(sessionKind)!, "session", ("eth", SessionKind.Eth), ("rth", SessionKind.Rth));

            if (!kind.IsSuccess)
                return kind.Error!;

            (Hours, TimeZoneInfo)? custom = null;

            // An option that would be quietly ignored is refused, so nobody is misled about what they got.
            if (parse.GetValue(hours) is null && Given(hoursZone))
                return new Error("input.bad_session", "--hours-tz says which zone --hours is in, so it needs --hours.");

            if (parse.GetValue(hours) is not null && Given(sessionKind))
                return new Error("input.bad_session", "--hours takes the place of --session, so give only one of them.");

            if (parse.GetValue(hours) is { } text)
            {
                var window = Hours.Parse(text);
                var windowZone = Zones.Find(parse.GetValue(hoursZone)!);

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

            if (parse.GetValue(bars) is { } barText)
            {
                var parsed = BarSpec.Parse(barText);

                if (!parsed.IsSuccess)
                    return parsed.Error!;

                barSpec = parsed.Value;
            }

            var outputFormat = Pick(parse.GetValue(format)!, "format", ("csv", OutputFormat.Csv), ("parquet", OutputFormat.Parquet),
                ("jsonl", OutputFormat.JsonLines), ("dbn", OutputFormat.Dbn), ("nt8", OutputFormat.NinjaTrader));
            var compression = Pick(parse.GetValue(compress)!, "compression", ("none", Compression.None), ("gzip", Compression.Gzip), ("zstd", Compression.Zstd));
            var timeMode = Pick(parse.GetValue(time)!, "time", ("iso", TimeMode.Iso), ("unix-ns", TimeMode.UnixNanos),
                ("unix-ms", TimeMode.UnixMillis), ("excel", TimeMode.Excel));
            var priceMode = Pick(parse.GetValue(price)!, "price", ("decimal", PriceMode.Decimal), ("ticks", PriceMode.Ticks), ("raw", PriceMode.Raw));
            var adjustment = Pick(parse.GetValue(adjust)!, "adjustment", ("none", Adjustment.None), ("difference", Adjustment.Difference), ("ratio", Adjustment.Ratio));

            foreach (var error in new[] { outputFormat.Error, compression.Error, timeMode.Error, priceMode.Error, adjustment.Error })
            {
                if (error is not null)
                    return error;
            }

            if (parse.GetValue(single) && parse.GetValue(partition))
                return new Error("input.bad_layout", "--single and --partition cannot both be given.");

            if (parse.GetValue(partition) && parse.GetValue(name) is not null)
                return new Error("input.bad_layout", "--partition names the files itself, so --name cannot be given with it.");

            if (compression.Value != Compression.None && outputFormat.Value is OutputFormat.Parquet or OutputFormat.NinjaTrader)
            {
                return new Error("input.bad_compression", outputFormat.Value == OutputFormat.Parquet
                    ? "--compress is for csv, jsonl and dbn. A Parquet file is always compressed inside."
                    : "--compress is for csv, jsonl and dbn. NinjaTrader imports plain text only.");
            }

            if (parse.GetValue(splitTime) && timeMode.Value != TimeMode.Iso)
                return new Error("input.bad_time", "--split-time splits an iso time into a date and a time, so it cannot be given with another --time.");

            return new ExportRequest
            {
                Schema = schemaName.Value,
                Symbol = spec.Value,
                TradeDates = span.Value,
                Sessions = sessions.Value,
                Output = parse.GetValue(output)!,
                Format = outputFormat.Value,
                Compression = compression.Value,
                Layout = parse.GetValue(single) ? FileLayout.Single : parse.GetValue(partition) ? FileLayout.Partitioned : FileLayout.PerDay,
                NameTemplate = parse.GetValue(name),
                Zone = tz.Value,
                Time = timeMode.Value,
                SplitTime = parse.GetValue(splitTime),
                Prices = priceMode.Value,
                Columns = parse.GetValue(columns) is { } list ? [.. PullCommand.Split(list, lower: true)] : [],
                Bars = barSpec,
                As = parse.GetValue(asSchema)?.ToLowerInvariant(),
                Adjustment = adjustment.Value,
                RollDays = parse.GetValue(rollDays),
                Spreads = !parse.GetValue(noSpreads)
            };

            // True when the option was typed, not just left at its default.
            bool Given(Option option) => parse.GetResult(option) is { Implicit: false };
        }

        return command;
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
