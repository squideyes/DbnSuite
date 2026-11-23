using System.ComponentModel;
using DbnSuite.Client;
using DbnSuite.Client.Archive;
using DbnSuite.Client.Export;
using DbnSuite.Client.Time;
using DbnSuite.Fetch.Commands;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DbnSuite.Fetch.Mcp;

/// <summary>
/// The tools that write files, offered only when <c>dbnf mcp</c> is started with <c>--out</c>. Every file goes
/// inside that folder, and each tool returns paths, counts and sizes, never the data. Each weighs what it would
/// read or copy first, and refuses more than <c>--max-gb</c> unless the call confirms. None deletes anything.
/// A parameter's name is the argument's name in the protocol, so <c>dry_run</c> is in snake_case, as results are.
/// </summary>
internal sealed class FileTools(ToolHost host)
{
    /// <summary>The most paths a result lists; the counts cover the rest.</summary>
    private const int MostListed = 100;

    private const int Parallel = 4;

    private const string Confirm = "Only with the user's go-ahead, true to go ahead when the size is over the server's limit.";
    private const string DryRun = "Only weigh it up: say what would be read or copied, and write nothing.";
    private const string Subfolder = "A folder inside the server's --out folder, as a relative path; the --out folder itself by default.";

    [McpServerTool(Name = "export", Destructive = false, UseStructuredContent = true)]
    [Description("Write records or bars for a span of trade dates to files, as dbnf export does: CSV, Parquet, JSON Lines, DBN or "
        + "NinjaTrader 8, for a root, a contract or a continuous series (ES.v.0), optionally back-adjusted. It returns the files "
        + "written, their sizes and the row count, never the data. It first weighs the raw files it would read, and refuses "
        + "more than the server's limit unless confirm is true.")]
    public async Task<ExportResult> ExportAsync(
        [Description("The schema to read, such as ohlcv-1s, mbo or statistics.")] string schema,
        [Description("A root (ES), a contract (ESZ26) or a continuous name (ES.v.0, ES.c.0, ES.n.0).")] string symbol,
        [Description("The first trade date.")] DateOnly from,
        [Description("The last trade date, included; the same as from by default.")] DateOnly? to = null,
        [Description(Subfolder)] string? folder = null,
        [Description("csv, parquet, jsonl, dbn or nt8 (NinjaTrader 8).")] string format = "csv",
        [Description("none, gzip or zstd, for csv, jsonl and dbn.")] string compress = "none",
        [Description("Bars instead of records: 5s, 1m, 15m, 1h, 1d, 1w, 1mo, or from mbo 500t (trades), 1000v (volume).")] string? bars = null,
        [Description("Build from mbo what Databento sells separately: trades, tbbo, mbp-1, mbp-10, bbo-1s or bbo-1m.")] string? @as = null,
        [Description("eth (the whole Globex session) or rth (regular hours).")] string? session = null,
        [Description("Hours of your own instead of session, Chicago time, such as 09:30-16:00.")] string? hours = null,
        [Description("For a continuous series other than mbo: none, difference (back-adjusted) or ratio.")] string adjust = "none",
        [Description("One file for the whole range, not one per trade date.")] bool single = false,
        [Description("series=/date= folders that DuckDB and Polars read as one table.")] bool partition = false,
        [Description("Only these columns, in this order, comma-separated, such as ts_event,price,size.")] string? columns = null,
        [Description("decimal, ticks or raw (Databento's integers).")] string price = "decimal",
        [Description("iso, unix-ns, unix-ms or excel.")] string time = "iso",
        [Description("The IANA time zone times are written in, or UTC.")] string tz = Zones.Default,
        [Description("For a root: false leaves out spreads.")] bool spreads = true,
        [Description(DryRun)] bool dry_run = false,
        [Description(Confirm)] bool confirm = false,
        RequestContext<CallToolRequestParams>? call = null,
        CancellationToken cancel = default)
    {
        await using var progress = OrderedProgress.For(call);

        var output = Folder(folder);

        var request = ExportCommand.Request(new ExportCommand.ExportChoices
        {
            Schema = schema,
            Symbol = symbol,
            TradeDates = Range(from, to),
            Output = output,
            Format = format,
            Compress = compress,
            Zone = tz,
            Time = time,
            Price = price,
            Session = session,
            Hours = hours,
            Bars = bars,
            As = @as,
            Adjust = adjust,
            Single = single,
            Partition = partition,
            Columns = columns,
            NoSpreads = !spreads
        });

        if (!request.IsSuccess)
            throw host.Refuse(request.Error!);

        var r = request.Value;

        return await host.RunAsync<ExportResult>(async archive =>
        {
            var days = await archive.ListDaysAsync(r.Schema, r.Symbol.Root, cancel).ConfigureAwait(false);

            if (!days.IsSuccess)
                return days.Error!;

            var utc = Sessions.UtcDays(r.TradeDates);
            var sources = days.Value.Where(d => utc.Contains(d.Day) && !d.Empty).ToList();
            var sourceBytes = sources.Sum(d => d.Object.Size);

            if (dry_run)
                return new ExportResult(output, true, sourceBytes, sources.Count, null, 0, [], []);

            if (host.Limit(sourceBytes, confirm, "export reads") is { } refused)
                return refused;

            var exported = await new Exporter(archive).RunAsync(r, Report<DateOnly>(progress, utc.Days, d => $"read {Text.Day(d)}"), cancel)
                .ConfigureAwait(false);

            if (!exported.IsSuccess)
                return exported.Error!;

            var files = exported.Value.Files.Select(f => new WrittenFile(f, new FileInfo(f).Length)).ToList();

            return new ExportResult(output, false, sourceBytes, sources.Count, exported.Value.Rows, files.Sum(f => f.Bytes),
                files, exported.Value.MissingDays);
        }).ConfigureAwait(false);
    }

    [McpServerTool(Name = "pull", Destructive = false, UseStructuredContent = true)]
    [Description("Download raw .dbn.zst files exactly as stored, one UTC day each, as dbnf pull --out does, in the archive's own "
        + "folder layout, skipping files already there. It returns how many files and bytes, and their paths, never the data. "
        + "It first weighs what it would download, and refuses more than the server's limit unless confirm is true.")]
    public async Task<PullResult> PullAsync(
        [Description("One schema or several, comma-separated, such as mbo or ohlcv-1s,statistics.")] string schemas,
        [Description("One root or several, comma-separated, such as MNQ or ES,NQ.")] string roots,
        [Description("The first UTC day.")] DateOnly from,
        [Description("The last UTC day, included; the same as from by default.")] DateOnly? to = null,
        [Description(Subfolder)] string? folder = null,
        [Description("Also write the archive's .empty markers for days with no data, so the folder can be read as an archive.")] bool markers = false,
        [Description(DryRun)] bool dry_run = false,
        [Description(Confirm)] bool confirm = false,
        RequestContext<CallToolRequestParams>? call = null,
        CancellationToken cancel = default)
    {
        await using var progress = OrderedProgress.For(call);

        var output = Folder(folder);
        var span = Range(from, to);

        if (!span.IsSuccess)
            throw host.Refuse(span.Error!);

        return await host.RunAsync<PullResult>(async archive =>
        {
            var target = new LocalCache(output);
            var planned = await PullCommand.PlanAsync(archive, schemas, roots, span.Value, target, false, cancel).ConfigureAwait(false);

            if (!planned.IsSuccess)
                return planned.Error!;

            var plan = planned.Value;
            var paths = plan.Fetch.Take(MostListed).Select(d => target.PathOf(d.Object.Key)).ToList();

            if (dry_run)
                return new PullResult(output, true, plan.Wanted.Count, plan.Already, 0, plan.Empty.Count, plan.Bytes, paths);

            if (host.Limit(plan.Bytes, confirm, "pull downloads") is { } refused)
                return refused;

            if (markers)
                await PullCommand.WriteMarkersAsync(target, plan.Empty, cancel).ConfigureAwait(false);

            await PullCommand.FetchAsync(archive, target, plan, Parallel, false,
                (day, n, _) =>
                {
                    progress?.Report(new ProgressNotificationValue
                    {
                        Progress = n,
                        Total = plan.Fetch.Count,
                        Message = $"{Text.Day(day.Day)} {day.Object.Key[(day.Object.Key.LastIndexOf('/') + 1)..]}"
                    });

                    return Task.CompletedTask;
                },
                cancel).ConfigureAwait(false);

            return new PullResult(output, false, plan.Wanted.Count, plan.Already, plan.Fetch.Count, plan.Empty.Count, plan.Bytes, paths);
        }).ConfigureAwait(false);
    }

    [McpServerTool(Name = "tape_pull", Destructive = false, UseStructuredContent = true)]
    [Description("Copy a root's tapes and catalog down from a tape store, as dbnf tape pull does, skipping those already there. "
        + "The server's login must be the tape store's. Only full ETH days have tapes. It returns how many files and bytes, and "
        + "their paths, never the data; unlike dbnf tape pull it never removes a tape. It first weighs what it would copy, and "
        + "refuses more than the server's limit unless confirm is true. The catalog, a small file, is copied even on a dry run, "
        + "since it says which dates have tapes.")]
    public async Task<TapePullResult> TapePullAsync(
        [Description("A root, such as ES.")] string root,
        [Description("The first trade date; every date by default.")] DateOnly? from = null,
        [Description("The last trade date, included.")] DateOnly? to = null,
        [Description("Only these kinds: s1, trade or l1. Every kind by default.")] string[]? kinds = null,
        [Description(Subfolder)] string? folder = null,
        [Description(DryRun)] bool dry_run = false,
        [Description(Confirm)] bool confirm = false,
        RequestContext<CallToolRequestParams>? call = null,
        CancellationToken cancel = default)
    {
        await using var progress = OrderedProgress.For(call);

        var output = Folder(folder);
        var name = root.Trim().ToUpperInvariant();
        var wanted = kinds is { Length: > 0 } named ? named.Select(k => k.Trim().ToLowerInvariant()).ToArray() : TapePullCommand.Kinds;

        if (wanted.FirstOrDefault(k => !TapePullCommand.Kinds.Contains(k)) is { } bad)
            throw host.Refuse(new Error("input.bad_tape_kind", $"\"{bad}\" is not a kind. A kind is s1, trade or l1."));

        return await host.RunAsync<TapePullResult>(async archive =>
        {
            var planned = await TapePullCommand.PlanAsync(archive, name, output, from, to, wanted, cancel).ConfigureAwait(false);

            if (!planned.IsSuccess)
                return planned.Error!;

            var plan = planned.Value;
            var paths = plan.Copy.Take(MostListed).Select(s => TapePullCommand.TargetOf(output, s.Key)).ToList();

            if (dry_run)
                return new TapePullResult(output, true, plan.CatalogCopied, 0, plan.Skipped, plan.Bytes, paths);

            if (host.Limit(plan.Bytes, confirm, "tape pull copies") is { } refused)
                return refused;

            await TapePullCommand.CopyAllAsync(archive.Store, plan, output,
                Report<StoredObject>(progress, plan.Copy.Count, s => s.Key[(s.Key.LastIndexOf('/') + 1)..]), cancel)
                .ConfigureAwait(false);

            return new TapePullResult(output, false, plan.CatalogCopied, plan.Copy.Count, plan.Skipped, plan.Bytes, paths);
        }).ConfigureAwait(false);
    }

    /// <summary>The folder a tool writes into, made if need be; a path that would leave <c>--out</c> is refused.</summary>
    private string Folder(string? folder)
    {
        var inside = host.Inside(folder);

        if (!inside.IsSuccess)
            throw host.Refuse(inside.Error!);

        Directory.CreateDirectory(inside.Value);

        return inside.Value;
    }

    private static Result<DateSpan> Range(DateOnly from, DateOnly? to) =>
        DateSpan.Resolve(from, to, null, null, DateOnly.FromDateTime(DateTime.UtcNow));

    /// <summary>Turns a tool's own progress into the client's, counting each step against <paramref name="total"/>.</summary>
    private static Steps<T>? Report<T>(IProgress<ProgressNotificationValue>? progress, int total, Func<T, string> say) =>
        progress is null ? null : new Steps<T>(progress, total, say);

    /// <summary>Reports each step as it happens, on the caller's thread, so the count never runs backwards.</summary>
    private sealed class Steps<T>(IProgress<ProgressNotificationValue> progress, int total, Func<T, string> say) : IProgress<T>
    {
        private int done;

        public void Report(T value) => progress.Report(new ProgressNotificationValue
        {
            Progress = Interlocked.Increment(ref done),
            Total = total,
            Message = say(value)
        });
    }
}
