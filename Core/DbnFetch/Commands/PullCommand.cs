using System.CommandLine;
using System.Diagnostics;
using DbnClient.Archive;
using DbnClient.Time;

namespace DbnFetch.Commands;

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
                return await session.FailAsync(new DbnClient.Error("input.no_out", "This archive is a folder already; give --out to copy from it.")).ConfigureAwait(false);

            var wanted = new List<StoredDay>();
            var empty = new List<StoredDay>();

            foreach (var named in Split(parse.GetValue(schemas)!, lower: true))
            {
                if (Session.Schema(named) is { IsSuccess: false } bad)
                    return await session.FailAsync(bad.Error!).ConfigureAwait(false);
            }

            foreach (var schema in Split(parse.GetValue(schemas)!, lower: true))
            foreach (var root in Split(parse.GetValue(roots)!, lower: false))
            {
                var days = await archive.ListDaysAsync(schema, root, cancel).ConfigureAwait(false);

                if (!days.IsSuccess)
                    return await session.FailAsync(days.Error!).ConfigureAwait(false);

                foreach (var day in days.Value.Where(d => span.Value.Contains(d.Day)))
                    (day.Empty ? empty : wanted).Add(day);
            }

            var already = parse.GetValue(force) ? [] : wanted.Where(d => target.Holds(d.Object)).ToHashSet();
            var fetch = wanted.Where(d => !already.Contains(d)).ToList();
            var bytes = fetch.Sum(d => d.Object.Size);

            await session.Out.WriteLineAsync(Text.Invariant(
                $"{Text.Count(wanted.Count)} files from {Text.Day(span.Value.From)} to {Text.Day(span.Value.To)}: {Text.Count(already.Count)} already here, {Text.Count(fetch.Count)} to download ({Text.Bytes(bytes)}); {Text.Count(empty.Count)} days had no data.")).ConfigureAwait(false);

            if (parse.GetValue(markers) && parse.GetValue(output) is not null && !parse.GetValue(dryRun))
            {
                foreach (var day in empty)
                {
                    var path = target.PathOf(day.Object.Key);

                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllBytesAsync(path, [], cancel).ConfigureAwait(false);
                }
            }

            if (parse.GetValue(dryRun) || fetch.Count == 0)
            {
                await session.Out.WriteLineAsync($"Files are in {target.Root}").ConfigureAwait(false);

                return Session.Ok;
            }

            var clock = Stopwatch.StartNew();
            var done = 0;

            await Parallel.ForEachAsync(fetch,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parse.GetValue(parallel)), CancellationToken = cancel },
                async (day, token) =>
                {
                    var started = clock.Elapsed;

                    if (parse.GetValue(force))
                        target.Remove(day.Object.Key);

                    await target.FetchAsync(archive.Store, day.Object, null, token).ConfigureAwait(false);

                    var seconds = Math.Max((clock.Elapsed - started).TotalSeconds, 0.001);
                    var n = Interlocked.Increment(ref done);

                    await session.Out.WriteLineAsync(Text.Invariant(
                        $"[{n}/{fetch.Count}] {Text.Day(day.Day)}  {Name(day.Object.Key),-36} {Text.Bytes(day.Object.Size),9}  {Text.Bytes((long)(day.Object.Size / seconds))}/s")).ConfigureAwait(false);
                }).ConfigureAwait(false);

            await session.Out.WriteLineAsync(Text.Invariant(
                $"Downloaded {Text.Count(fetch.Count)} files, {Text.Bytes(bytes)}, in {clock.Elapsed.TotalSeconds:0} s, into {target.Root}")).ConfigureAwait(false);

            return Session.Ok;
        }, cancel));

        return command;
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
    public Option<DateOnly?> From { get; } = new("--from") { Description = "The first day, as yyyy-MM-dd." };
    public Option<DateOnly?> To { get; } = new("--to") { Description = "The last day, included; --from alone means that one day." };
    public Option<string?> Last { get; } = new("--last") { Description = "The most recent stretch: 30d, 8w, 6m or 1y." };
    public Option<DateOnly?> Since { get; } = new("--since") { Description = "From this day to the latest." };

    public void AddTo(Command command)
    {
        command.Options.Add(From);
        command.Options.Add(To);
        command.Options.Add(Last);
        command.Options.Add(Since);
    }

    public DbnClient.Result<DateSpan> Resolve(ParseResult parse) =>
        DateSpan.Resolve(parse.GetValue(From), parse.GetValue(To), parse.GetValue(Last), parse.GetValue(Since),
            DateOnly.FromDateTime(DateTime.UtcNow));
}
