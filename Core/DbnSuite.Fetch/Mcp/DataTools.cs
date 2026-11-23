using System.ComponentModel;
using System.Text.Json;
using DbnSuite.Client;
using DbnSuite.Client.Export;
using DbnSuite.Client.Time;
using DbnSuite.Fetch.Commands;
using ModelContextProtocol.Server;

namespace DbnSuite.Fetch.Mcp;

/// <summary>
/// The tools that return market data, offered only when <c>dbnf mcp</c> is started with <c>--allow-data</c>:
/// what they return goes to the AI model's provider, which the user's Databento agreement must allow.
/// </summary>
internal sealed class DataTools(ToolHost host)
{
    /// <summary>The most rows a tool returns, so a result stays a glance, never a download.</summary>
    public const int MostRows = 200;

    private const string Licence = " Its result is market data, which goes to the AI model's provider: the user turned this on with "
        + "--allow-data, and their Databento agreement decides whether that is allowed.";

    [McpServerTool(Name = "show_records", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("A few of one UTC day's records of a schema, a line each, like dbnf show: for a root (ES), a contract (ESZ26), "
        + "or for mbo a continuous name such as ES.n.0. At most 200 records." + Licence)]
    public Task<RecordsResult> ShowRecordsAsync(
        [Description("A schema, such as mbo, ohlcv-1s, definition, statistics or status.")] string schema,
        [Description("A root (ES), a contract (ESZ26), or for mbo ES.n.0.")] string symbol,
        [Description("The UTC day.")] DateOnly day,
        [Description("How many records to show, from 1 to 200.")] int limit = 20,
        [Description("The IANA time zone times are shown in, or UTC.")] string tz = Zones.Default,
        CancellationToken cancel = default)
    {
        if (Rows(limit) is { } bad)
            throw host.Refuse(bad);

        return host.RunAsync<RecordsResult>(async archive =>
        {
            var heading = "";
            var lines = new List<string>();

            var shown = await ShowCommand.ReadAsync(archive, new ShowCommand.ShowRequest(schema, symbol, day, limit, tz),
                text =>
                {
                    heading = text;
                    return Task.CompletedTask;
                },
                line =>
                {
                    lines.Add(line);
                    return Task.CompletedTask;
                },
                cancel).ConfigureAwait(false);

            if (!shown.IsSuccess)
                return shown.Error!;

            var s = shown.Value;

            return new RecordsResult(schema.Trim().ToLowerInvariant(), s.Symbol, day, heading, lines, s.Shown, s.Matched, s.Total);
        });
    }

    [McpServerTool(Name = "bars", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("A short run of OHLCV bars for one trade date, built as dbnf export --bars builds them: for a root, a contract or a "
        + "continuous name (ES.v.0). Each row has the export's columns, with times in the zone asked for. At most 200 rows; the "
        + "result says how many the day has." + Licence)]
    public Task<BarsResult> BarsAsync(
        [Description("A root (ES), a contract (ESZ26) or a continuous name (ES.v.0).")] string symbol,
        [Description("The trade date: its session opens at 17:00 Chicago time the evening before.")] DateOnly date,
        [Description("The bar size: 5s, 1m, 15m, 1h or 1d; or from mbo, 500t (trades) or 1000v (volume).")] string bars = "1m",
        [Description("The schema bars are built from: ohlcv-1s, or mbo for trade and volume bars.")] string schema = "ohlcv-1s",
        [Description("eth (the whole Globex session) or rth (regular hours).")] string session = "eth",
        [Description("How many bars to return, from 1 to 200.")] int limit = MostRows,
        [Description("The IANA time zone times are written in, or UTC.")] string tz = Zones.Default,
        CancellationToken cancel = default)
    {
        if (Rows(limit) is { } bad)
            throw host.Refuse(bad);

        return host.RunAsync<BarsResult>(async archive =>
        {
            var scratch = Path.Combine(Path.GetTempPath(), "dbnf-mcp", Path.GetRandomFileName());

            try
            {
                var request = ExportCommand.Request(new ExportCommand.ExportChoices
                {
                    Schema = schema,
                    Symbol = symbol,
                    TradeDates = new DateSpan(date, date),
                    Output = scratch,
                    Format = "jsonl",
                    Zone = tz,
                    Session = session,
                    Bars = bars,
                    Single = true
                });

                if (!request.IsSuccess)
                    return request.Error!;

                var exported = await new Exporter(archive).RunAsync(request.Value, null, cancel).ConfigureAwait(false);

                if (!exported.IsSuccess)
                    return exported.Error!;

                var rows = new List<JsonElement>();
                var total = 0;

                foreach (var file in exported.Value.Files)
                {
                    foreach (var line in await File.ReadAllLinesAsync(file, cancel).ConfigureAwait(false))
                    {
                        if (line.Length == 0)
                            continue;

                        total++;

                        if (rows.Count < limit)
                        {
                            using var parsed = JsonDocument.Parse(line);

                            rows.Add(parsed.RootElement.Clone());
                        }
                    }
                }

                return new BarsResult(request.Value.Symbol.Text, date, request.Value.Bars!.Text, request.Value.Schema,
                    session.Trim().ToLowerInvariant(), rows, total, total > rows.Count, exported.Value.MissingDays);
            }
            finally
            {
                Forget(scratch);
            }
        });
    }

    /// <summary>A row limit outside 1 to <see cref="MostRows"/> is refused, not quietly changed.</summary>
    private static Error? Rows(int limit) =>
        limit is >= 1 and <= MostRows
            ? null
            : new Error("input.bad_limit", Text.Invariant($"limit is {limit}; it must be from 1 to {MostRows}. For more, export to a file."));

    /// <summary>Removes a scratch folder of this server's own; one left behind by a locked file is only clutter in the temp folder.</summary>
    private static void Forget(string scratch)
    {
        try
        {
            if (Directory.Exists(scratch))
                Directory.Delete(scratch, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
