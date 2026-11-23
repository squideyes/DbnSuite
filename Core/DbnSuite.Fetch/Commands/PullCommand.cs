using System.CommandLine;
using System.Diagnostics;
using DbnSuite.Client.Archive;
using DbnSuite.Client.Time;

namespace DbnSuite.Fetch.Commands;

/// <summary>
/// <c>dbnf pull mbo MNQ --last 5d</c>: copies raw files, byte for byte, one
/// UTC day each, into the cache or a folder of your own.
/// </summary>
public static class PullCommand
{
    public static Command Create(Session session)
    {
        var schemas = new Argument<string>("schemas") { Description = "One schema or several, such as mbo or ohlcv-1s,statistics." };
        var roots = new Argument<string>("roots") { Description = "One root or several, such as MNQ or ES,NQ." };
        var range = new RangeOptions();
        var output = new Option<string?>("--out") { Description = "Save into this folder, in the archive's own layout. The cache by default." };
        var flat = new Option<bool>("--flat") { Description = "With --out, put every file in the folder itself." };
        var markers = new Option<bool>("--markers") { Description = "With --out, also write the archive's .empty files for days with no data." };
        var parallel = new Option<int>("--parallel") { Description = "Files to download at once.", DefaultValueFactory = _ => 4 };
        var force = new Option<bool>("--force") { Description = "Download again even when the file is already here." };
        var dryRun = new Option<bool>("--dry-run") { Description = "Show what would be downloaded, and how much, without downloading." };

        var command = new Command("pull", "Download raw .dbn.zst files exactly as stored, one UTC day each.")
        {
            schemas, roots, output, flat, markers, parallel, force, dryRun
        };

        range.AddTo(command);

        command.SetAction((parse, cancel) => session.RunAsync(parse, async archive =>
        {
            var span = range.Resolve(parse);

            if (!span.IsSuccess)
                return await session.FailAsync(span.Error!).ConfigureAwait(false);

            var target = parse.GetValue(output) is { } folder
                ? new LocalCache(folder, parse.GetValue(flat))
                : archive.Cache;

            if (target is null)
                return await session.FailAsync(new DbnSuite.Client.Error("input.no_out", "This archive is a folder already; give --out to copy from it.")).ConfigureAwait(false);

            var planned = await PlanAsync(archive, parse.GetValue(schemas)!, parse.GetValue(roots)!, span.Value, target,
                parse.GetValue(force), cancel).ConfigureAwait(false);

            if (!planned.IsSuccess)
                return await session.FailAsync(planned.Error!).ConfigureAwait(false);

            var plan = planned.Value;

            await session.Out.WriteLineAsync(Text.Invariant(
                $"{Text.Count(plan.Wanted.Count)} files from {Text.Day(span.Value.From)} to {Text.Day(span.Value.To)}: {Text.Count(plan.Already)} already here, {Text.Count(plan.Fetch.Count)} to download ({Text.Bytes(plan.Bytes)}); {Text.Count(plan.Empty.Count)} days had no data.")).ConfigureAwait(false);

            if (parse.GetValue(markers) && parse.GetValue(output) is not null && !parse.GetValue(dryRun))
                await WriteMarkersAsync(target, plan.Empty, cancel).ConfigureAwait(false);

            if (parse.GetValue(dryRun) || plan.Fetch.Count == 0)
            {
                await session.Out.WriteLineAsync($"Files are in {target.Root}").ConfigureAwait(false);

                return Session.Ok;
            }

            var clock = Stopwatch.StartNew();

            await FetchAsync(archive, target, plan, parse.GetValue(parallel), parse.GetValue(force),
                (day, n, seconds) => session.Out.WriteLineAsync(Text.Invariant(
                    $"[{n}/{plan.Fetch.Count}] {Text.Day(day.Day)}  {Name(day.Object.Key),-36} {Text.Bytes(day.Object.Size),9}  {Text.Bytes((long)(day.Object.Size / seconds))}/s")),
                cancel).ConfigureAwait(false);

            await session.Out.WriteLineAsync(Text.Invariant(
                $"Downloaded {Text.Count(plan.Fetch.Count)} files, {Text.Bytes(plan.Bytes)}, in {clock.Elapsed.TotalSeconds:0} s, into {target.Root}")).ConfigureAwait(false);

            return Session.Ok;
        }, cancel));

        return command;
    }

    /// <summary>What a pull would do: the files it wants, those already here, those to download, and the days with no data.</summary>
    internal sealed record PullPlan(IReadOnlyList<StoredDay> Wanted, int Already, IReadOnlyList<StoredDay> Fetch, IReadOnlyList<StoredDay> Empty)
    {
        /// <summary>How much there is to download.</summary>
        public long Bytes => Fetch.Sum(d => d.Object.Size);
    }

    /// <summary>
    /// Works out which stored days of the schemas and roots, each a comma-separated list, fall in the span,
    /// and which of those <paramref name="target"/> lacks; all of them when <paramref name="force"/>.
    /// </summary>
    internal static async Task<DbnSuite.Client.Result<PullPlan>> PlanAsync(DbnArchive archive, string schemas, string roots,
        DateSpan span, LocalCache target, bool force, CancellationToken cancel)
    {
        var wanted = new List<StoredDay>();
        var empty = new List<StoredDay>();

        foreach (var named in Split(schemas, lower: true))
        {
            if (Session.Schema(named) is { IsSuccess: false } bad)
                return bad.Error!;
        }

        foreach (var schema in Split(schemas, lower: true))
        foreach (var root in Split(roots, lower: false))
        {
            var days = await archive.ListDaysAsync(schema, root, cancel).ConfigureAwait(false);

            if (!days.IsSuccess)
                return days.Error!;

            foreach (var day in days.Value.Where(d => span.Contains(d.Day)))
                (day.Empty ? empty : wanted).Add(day);
        }

        var already = force ? [] : wanted.Where(d => target.Holds(d.Object)).ToHashSet();

        return new PullPlan(wanted, already.Count, [.. wanted.Where(d => !already.Contains(d))], empty);
    }

    /// <summary>Writes the archive's <c>.empty</c> marker for each day that had no data.</summary>
    internal static async Task WriteMarkersAsync(LocalCache target, IEnumerable<StoredDay> empty, CancellationToken cancel)
    {
        foreach (var day in empty)
        {
            var path = target.PathOf(day.Object.Key);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, [], cancel).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Downloads a plan's files, <paramref name="parallel"/> at once, telling <paramref name="done"/> each
    /// day as it lands, with how many have landed and how many seconds it took. With <paramref name="force"/>,
    /// a copy already here is removed first.
    /// </summary>
    internal static Task FetchAsync(DbnArchive archive, LocalCache target, PullPlan plan, int parallel, bool force,
        Func<StoredDay, int, double, Task> done, CancellationToken cancel)
    {
        var clock = Stopwatch.StartNew();
        var count = 0;

        return Parallel.ForEachAsync(plan.Fetch,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallel), CancellationToken = cancel },
            async (day, token) =>
            {
                var started = clock.Elapsed;

                if (force)
                    target.Remove(day.Object.Key);

                await target.FetchAsync(archive.Store, day.Object, null, token).ConfigureAwait(false);

                var seconds = Math.Max((clock.Elapsed - started).TotalSeconds, 0.001);

                await done(day, Interlocked.Increment(ref count), seconds).ConfigureAwait(false);
            });
    }

    internal static IEnumerable<string> Split(string list, bool lower) =>
        list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => lower ? s.ToLowerInvariant() : s.ToUpperInvariant())
            .Distinct();

    private static string Name(string key) => key[(key.LastIndexOf('/') + 1)..];
}

/// <summary>The date options commands share: --from and --to, --last, or --since.</summary>
public sealed class RangeOptions
{
    public Option<DateOnly?> From { get; } = Dates.Optional("--from", "The first day, as yyyy-MM-dd.");
    public Option<DateOnly?> To { get; } = Dates.Optional("--to", "The last day, included; --from alone means that one day.");
    public Option<string?> Last { get; } = new("--last") { Description = "The most recent stretch: 30d, 8w, 6m or 1y." };
    public Option<DateOnly?> Since { get; } = Dates.Optional("--since", "From this day to the latest.");

    public void AddTo(Command command)
    {
        command.Options.Add(From);
        command.Options.Add(To);
        command.Options.Add(Last);
        command.Options.Add(Since);
    }

    public DbnSuite.Client.Result<DateSpan> Resolve(ParseResult parse) =>
        DateSpan.Resolve(parse.GetValue(From), parse.GetValue(To), parse.GetValue(Last), parse.GetValue(Since),
            DateOnly.FromDateTime(DateTime.UtcNow));
}
