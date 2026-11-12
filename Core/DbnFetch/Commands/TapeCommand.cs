using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using DbnClient;
using DbnClient.Contracts;
using DbnClient.Tape;
using DbnClient.Time;

namespace DbnFetch.Commands;

/// <summary><c>dbnf tape build ES --tapes D:\Tapes --from 2026-10-29</c>: compact tapes for backtesting, and the tools around them.</summary>
public static class TapeCommand
{
    private const string TimeFormat = "MM-dd HH:mm";

    public static Command Create(Session session) => new("tape", "Build, inspect and prove compact tapes for backtesting.")
    {
        BuildCatalog(session),
        Build(session),
        Verify(session),
        Info(session),
        Schema(session),
        TapePullCommand.Create(session),
        TapeBenchCommand.Create(session)
    };

    private static Command BuildCatalog(Session session)
    {
        var options = new TapeOptions();
        var command = new Command("catalog", "Build a root's catalog from the archive. Tapes need one, and it is rebuilt when new days arrive.");

        options.AddTo(command);

        command.SetAction((parse, cancel) => session.RunAsync(parse, async archive =>
        {
            LowerPriority();

            var root = options.RootOf(parse);
            var started = Stopwatch.GetTimestamp();
            var built = await Catalog.BuildAsync(archive, root, new Progress<string>(session.Out.WriteLine), cancel).ConfigureAwait(false);

            if (!built.IsSuccess)
                return await session.FailAsync(built.Error!).ConfigureAwait(false);

            var saved = await built.Value.SaveAsync(parse.GetValue(options.Tapes)!, cancel).ConfigureAwait(false);

            if (!saved.IsSuccess)
                return await session.FailAsync(saved.Error!).ConfigureAwait(false);

            await session.Out.WriteLineAsync(Text.Invariant(
                $"{root}: {Text.Count(built.Value.Contracts.Count)} contracts, {Text.Count(built.Value.Days.Count)} days, {Text.Bytes(new FileInfo(saved.Value).Length)}, {Stopwatch.GetElapsedTime(started).TotalSeconds:F0} s")).ConfigureAwait(false);

            return Session.Ok;
        }, cancel));

        return command;
    }

    private static Command Build(Session session)
    {
        var options = new TapeOptions();
        var dates = new TapeDates();
        var symbols = new Option<string[]>("--symbol") { Description = "Only these contracts.", AllowMultipleArgumentsPerToken = true };
        var zstd = new Option<bool>("--zstd") { Description = "Compress the bodies, for storage. Replay is fastest without it." };
        var command = new Command("build", "Build tapes for a span of trade dates: S1 for every contract, Trade and L1 for those mbo holds.") { symbols, zstd };

        options.AddTo(command);
        dates.AddTo(command);

        command.SetAction((parse, cancel) => session.RunAsync(parse, async archive =>
        {
            LowerPriority();

            var folder = parse.GetValue(options.Tapes)!;
            var loaded = await Catalog.LoadAsync(Catalog.PathOf(folder, archive.Dataset, options.RootOf(parse)), cancel).ConfigureAwait(false);

            if (!loaded.IsSuccess)
                return await session.FailAsync(loaded.Error!).ConfigureAwait(false);

            var span = dates.Span(parse, loaded.Value);

            if (!span.IsSuccess)
                return await session.FailAsync(span.Error!).ConfigureAwait(false);

            var only = TapeDates.Symbols(parse.GetValue(symbols), loaded.Value);

            if (!only.IsSuccess)
                return await session.FailAsync(only.Error!).ConfigureAwait(false);

            var builder = new TapeBuilder(archive, loaded.Value, folder);
            var failed = 0;
            var days = TapeDates.In(span.Value, loaded.Value).ToList();

            if (days.Count == 0)
                await session.Out.WriteLineAsync($"No trade date from {Text.Day(span.Value.From)} to {Text.Day(span.Value.To)}, so there was nothing to build.").ConfigureAwait(false);

            foreach (var date in days)
            {
                var started = Stopwatch.GetTimestamp();
                var built = await builder.BuildAsync(date, parse.GetValue(zstd), only.Value, cancel).ConfigureAwait(false);

                if (!built.IsSuccess)
                {
                    await session.Out.WriteLineAsync($"{Text.Day(date)}: refused: {built.Error}").ConfigureAwait(false);
                    failed++;
                    continue;
                }

                await session.WriteAsync(Report(date, built.Value)).ConfigureAwait(false);

                var bars = built.Value.Tapes.Count(t => t.Kind == TapeKind.S1 && t.Error is null);

                failed += built.Value.Tapes.Count(t => t.Error is { Code: not TapeErrors.NotHeld });

                await session.Out.WriteLineAsync(Text.Invariant(
                    $"{Text.Day(date)}: {Text.Count(bars)} S1 tapes, {Stopwatch.GetElapsedTime(started).TotalSeconds:F1} s")).ConfigureAwait(false);
            }

            return failed == 0 ? Session.Ok : Session.Failed;
        }, cancel));

        return command;
    }

    private static IEnumerable<string> Report(DateOnly date, BuildReport built)
    {
        foreach (var tape in built.Tapes.Where(t => t.Kind != TapeKind.S1 || t.Error is not null))
        {
            yield return tape.Error is { } error
                ? $"{Text.Day(date)} {tape.Symbol} {tape.Kind}: {(error.Code == TapeErrors.NotHeld ? "not held" : "refused")}: {error}"
                : $"{Text.Day(date)} {tape.Symbol} {tape.Kind}: {Text.Count(tape.Events)} events, {Text.Bytes(tape.Bytes)}";
        }

        foreach (var boundary in built.Boundaries)
            yield return $"{Text.Day(date)} {boundary.Symbol}: the top of book changed across a file boundary {Text.Count(boundary.Count)} time(s)";
    }

    private static Command Verify(Session session)
    {
        var options = new TapeOptions();
        var dates = new TapeDates();
        var command = new Command("verify", "Prove built tapes against the archive, on your own machine. It writes nothing.");

        options.AddTo(command);
        dates.AddTo(command);

        command.SetAction((parse, cancel) => session.RunAsync(parse, async archive =>
        {
            LowerPriority();

            var folder = parse.GetValue(options.Tapes)!;
            var root = options.RootOf(parse);
            var loaded = await Catalog.LoadAsync(Catalog.PathOf(folder, archive.Dataset, root), cancel).ConfigureAwait(false);

            if (!loaded.IsSuccess)
                return await session.FailAsync(loaded.Error!).ConfigureAwait(false);

            var span = dates.Span(parse, loaded.Value);

            if (!span.IsSuccess)
                return await session.FailAsync(span.Error!).ConfigureAwait(false);

            var proof = new TapeProof(archive, loaded.Value, folder);
            var failed = 0;
            var passed = 0;

            foreach (var date in TapeDates.Between(span.Value))
            {
                var results = await proof.RunAsync(date, cancel).ConfigureAwait(false);

                passed += results.Count(r => r.Passed);
                failed += results.Count(r => !r.Passed);

                foreach (var result in results.Where(r => !r.Passed || r.Detail is not null))
                    await session.Out.WriteLineAsync($"{Text.Day(date)} {result.Symbol} {result.Check}: {(result.Passed ? "pass" : "FAIL")} {result.Detail}").ConfigureAwait(false);

                await session.Out.WriteLineAsync($"{Text.Day(date)}: {results.Count(r => r.Passed)} passed, {results.Count(r => !r.Passed)} failed").ConfigureAwait(false);
            }

            await session.Out.WriteLineAsync($"{root}: {Text.Count(passed)} checks passed, {Text.Count(failed)} failed").ConfigureAwait(false);

            return failed == 0 ? Session.Ok : Session.Failed;
        }, cancel));

        return command;
    }

    private static Command Info(Session session)
    {
        var file = new Argument<string>("file") { Description = "A tape." };
        var command = new Command("info", "Show what a tape holds.") { file };

        command.SetAction(async (parse, cancel) =>
        {
            var loaded = await Tape.LoadAsync(parse.GetValue(file)!, cancel).ConfigureAwait(false);

            if (!loaded.IsSuccess)
                return await session.FailAsync(loaded.Error!).ConfigureAwait(false);

            await session.WriteAsync(Describe(loaded.Value.Header)).ConfigureAwait(false);

            return Session.Ok;
        });

        return command;
    }

    /// <summary>A tape's header in a few lines, with its session in Chicago time.</summary>
    internal static IEnumerable<string> Describe(TapeHeader header)
    {
        var zone = Zones.Find(Zones.Default).Value;

        string Local(long nanos) => TimeZoneInfo.ConvertTime(DateTimeOffset.UnixEpoch.AddTicks(nanos / 100), zone).ToString(TimeFormat, CultureInfo.InvariantCulture);

        yield return $"{header.Symbol}  {header.Kind} v{TapeHeader.CurrentVersion}  {header.Dataset}  trade date {Text.Day(header.TradeDate)}";
        yield return $"session  {Local(header.OpenTime)} to {Local(header.CloseTime)} Chicago";
        yield return header.HasRth ? $"rth      events {Text.Count(header.RthStart.EventIndex)} to {Text.Count(header.RthEndIndex)}" : "rth      none";
        yield return $"events   {Text.Count(header.EventCount)}, {Text.Bytes(header.BodyLength)}{(header.IsCompressed ? $", {Text.Bytes(header.StoredLength)} in zstd" : "")}";
    }

    private static Command Schema(Session session)
    {
        var kind = new Argument<string>("kind") { Description = "s1, trade or l1." };
        var command = new Command("schema", "Show a tape kind's fields.") { kind };

        command.SetAction(async (parse, cancel) =>
        {
            var fields = Fields(parse.GetValue(kind)!);

            if (fields is null)
                return await session.FailAsync(new Error("input.bad_tape_kind", "A kind is s1, trade or l1.")).ConfigureAwait(false);

            await session.WriteAsync(fields).ConfigureAwait(false);

            return Session.Ok;
        });

        return command;
    }

    private static string[]? Fields(string kind) => kind.ToLowerInvariant() switch
    {
        "s1" => ["bar    time  open  high  low  close  volume"],
        "trade" => ["trade  time  price  size  side"],
        "l1" => ["quote  time  bid  ask  bid_size  ask_size", "trade  time  price  size  side", "state  time  action"],
        _ => null
    };

    /// <summary>These runs can take hours, so the machine stays usable while they do.</summary>
    private static void LowerPriority()
    {
        using var process = Process.GetCurrentProcess();

        process.PriorityClass = ProcessPriorityClass.BelowNormal;
    }
}

/// <summary>What every tape command that reads the archive needs: a root and the tape folder.</summary>
internal sealed class TapeOptions
{
    public Argument<string> Root { get; } = new("root") { Description = "A root, such as ES." };

    public Option<string> Tapes { get; } = new("--tapes") { Description = "The folder tapes and catalogs live in.", Required = true };

    public void AddTo(Command command)
    {
        command.Add(Root);
        command.Add(Tapes);
    }

    public string RootOf(ParseResult parse) => parse.GetValue(Root)!.ToUpperInvariant();
}

/// <summary>The span of trade dates a tape command covers.</summary>
internal sealed class TapeDates
{
    /// <summary>The span lies outside what the catalog covers, so there is nothing to work on.</summary>
    public const string OutsideCatalog = "tape.outside_catalog";

    public Option<DateOnly> From { get; } = Dates.Required("--from", "The first trade date, as yyyy-MM-dd.");

    public Option<DateOnly?> To { get; } = Dates.Optional("--to", "The last trade date, included; --from alone means that one date.");

    public void AddTo(Command command)
    {
        command.Add(From);
        command.Add(To);
    }

    /// <summary>
    /// The span asked for, cut to what the catalog covers. A span back to front is bad usage, and one
    /// the catalog does not reach at all is an error: doing nothing and reporting success would let a
    /// script carry on as if tapes had been built or proved.
    /// </summary>
    public Result<DateSpan> Span(ParseResult parse, Catalog catalog) =>
        Span(parse.GetValue(From), parse.GetValue(To), catalog);

    internal static Result<DateSpan> Span(DateOnly from, DateOnly? until, Catalog catalog)
    {
        var to = until ?? from;

        if (from > to)
            return new Error("input.bad_range", $"The range starts {Text.Day(from)}, after it ends ({Text.Day(to)}).");

        if (catalog.Days.Count == 0)
            return new Error(OutsideCatalog, $"The catalog for {catalog.Root} holds no trade dates. Run dbnf tape catalog {catalog.Root} to rebuild it.");

        var first = catalog.Days[0].TradeDate;
        var last = catalog.Days[^1].TradeDate;

        if (from > last || to < first)
        {
            return new Error(OutsideCatalog,
                $"The catalog for {catalog.Root} covers {Text.Day(first)} to {Text.Day(last)}, which leaves nothing from {Text.Day(from)} to {Text.Day(to)}. "
                + $"Run dbnf tape catalog {catalog.Root} to bring it up to date.");
        }

        return new DateSpan(from < first ? first : from, to > last ? last : to);
    }

    /// <summary>The span's dates that have a session.</summary>
    public static IEnumerable<DateOnly> In(DateSpan span, Catalog catalog) =>
        catalog.Days.Select(d => d.TradeDate).Where(span.Contains);

    /// <summary>Every date in the span, because a date the catalog lacks must have had no trading.</summary>
    public static IEnumerable<DateOnly> Between(DateSpan span)
    {
        for (var date = span.From; date <= span.To; date = date.AddDays(1))
            yield return date;
    }

    /// <summary>
    /// The CME names of the contracts <paramref name="named"/> picks, each given as ESZ6 or ESZ26 in
    /// either case; null when none was named, which means every contract. A name the catalog lacks is
    /// an error, since building nothing for it would look like success.
    /// </summary>
    internal static Result<HashSet<string>?> Symbols(string[]? named, Catalog catalog)
    {
        if (named is not { Length: > 0 })
            return (HashSet<string>?)null;

        HashSet<string> symbols = [with(StringComparer.Ordinal)];

        foreach (var name in named.Select(n => n.Trim()))
        {
            var matched = catalog.Contracts.Where(c => ContractSymbol.Matches(name, c.Symbol, c.Year)).Select(c => c.Symbol).ToList();

            if (matched.Count == 0)
                return new Error(Errors.BadSymbol, $"The catalog for {catalog.Root} has no contract {name}. dbnf contracts {catalog.Root} lists them.");

            symbols.UnionWith(matched);
        }

        return symbols;
    }
}
