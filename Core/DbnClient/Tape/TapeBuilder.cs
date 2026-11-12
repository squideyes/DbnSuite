using System.Buffers.Binary;
using System.Globalization;
using DbnClient.Archive;
using DbnClient.Dbn;

namespace DbnClient.Tape;

/// <summary>What became of one tape.</summary>
/// <param name="Path">Where it was saved, or null when it was not.</param>
/// <param name="Error">Why it was not saved, or null when it was.</param>
public sealed record TapeOutcome(string Symbol, TapeKind Kind, string? Path, long Events, long Bytes, Error? Error);

/// <summary>A contract whose top of book changed across a UTC file's opening snapshot.</summary>
public sealed record BoundaryChange(string Symbol, int Count);

/// <summary>What one trade date's build did.</summary>
/// <param name="Removed">
/// Tapes an earlier build left of a kind this one didn't make, deleted so the date never replays
/// another contract.
/// </param>
public sealed record BuildReport(DateOnly TradeDate, IReadOnlyList<TapeOutcome> Tapes, IReadOnlyList<BoundaryChange> Boundaries,
    IReadOnlyList<string>? Removed = null);

/// <summary>
/// Where a tape lives under a tape root. A trade date has one tape of each kind, so a date alone
/// finds it; the contract it holds is in its header.
/// </summary>
public static class TapePaths
{
    public static string For(string tapeRoot, string dataset, TapeKind kind, string root, DateOnly tradeDate)
    {
        var name = Name(kind);

        return Path.Combine(tapeRoot, dataset, name, root, tradeDate.Year.ToString(CultureInfo.InvariantCulture),
            $"{root}.{name}.{tradeDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.tape");
    }

    /// <summary>The dates a root has tapes of one kind for under a tape root, oldest first.</summary>
    public static IReadOnlyList<DateOnly> Dates(string tapeRoot, string dataset, TapeKind kind, string root)
    {
        var folder = KindFolder(tapeRoot, dataset, Name(kind), root);

        if (!Directory.Exists(folder))
            return [];

        return [.. Directory.EnumerateFiles(folder, $"{root}.{Name(kind)}.*.tape", SearchOption.AllDirectories)
            .Select(TradeDateOf).OfType<DateOnly>().Distinct().Order()];
    }

    /// <summary>The folder names of the tape kinds, as paths and store keys use them.</summary>
    public static IReadOnlyList<string> Kinds { get; } = ["s1", "trade", "l1"];

    /// <summary>A root's folder of one kind, or its key prefix in a store when <paramref name="tapeRoot"/> is empty.</summary>
    public static string KindFolder(string tapeRoot, string dataset, string kind, string root) =>
        tapeRoot.Length == 0 ? $"{dataset}/{kind}/{root}/" : Path.Combine(tapeRoot, dataset, kind, root);

    /// <summary>The trade date a tape's file name or store key carries, or null when it is not a tape's.</summary>
    public static DateOnly? TradeDateOf(string pathOrKey)
    {
        var name = pathOrKey[(pathOrKey.LastIndexOfAny(['/', '\\']) + 1)..];
        var parts = name.Split('.');

        return parts.Length == 4 && parts[3] == "tape"
            && DateOnly.TryParseExact(parts[2], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? date
                : null;
    }

    /// <summary>
    /// Deletes a root's tapes under a folder whose dates the catalog says are not full ETH days, so
    /// the folder holds only what the rules allow, whatever an older version left there. A date
    /// outside the catalog's span is left alone: an older catalog can't judge it.
    /// </summary>
    /// <returns>The files deleted.</returns>
    public static IReadOnlyList<string> RemoveExcluded(string tapeRoot, Catalog catalog)
    {
        var removed = new List<string>();

        if (catalog.Days.Count == 0)
            return removed;

        foreach (var kind in Kinds)
        {
            var folder = KindFolder(tapeRoot, catalog.Dataset, kind, catalog.Root);

            if (!Directory.Exists(folder))
                continue;

            foreach (var file in Directory.EnumerateFiles(folder, "*.tape", SearchOption.AllDirectories))
            {
                if (TradeDateOf(file) is { } date && catalog.Judges(date) && !catalog.IsFullDay(date))
                {
                    File.Delete(file);
                    removed.Add(file);
                }
            }
        }

        return removed;
    }

    private static string Name(TapeKind kind) => kind switch
    {
        TapeKind.S1 => "s1",
        TapeKind.Trade => "trade",
        _ => "l1"
    };
}

/// <summary>
/// Turns one trade date of a root into tapes: one of each kind, for the one contract the catalog
/// holds that date (<see cref="Catalog.HeldOn"/>). Each is a whole, checked file or nothing. A
/// trade date usually draws on two of the archive's UTC files.
/// </summary>
public sealed class TapeBuilder(DbnArchive archive, Catalog catalog, string tapeRoot)
{
    private const string BarSchema = "ohlcv-1s";
    private const string MboSchema = "mbo";
    private const long NanosPerSecond = 1_000_000_000;
    private const long MicrosPerSecond = 1_000_000;

    private readonly SessionCalendar calendar = new(catalog);

    /// <param name="tradeDate">The date to build.</param>
    /// <param name="compress">Wrap the bodies in zstd.</param>
    /// <param name="cancel">Stops the build.</param>
    public async Task<Result<BuildReport>> BuildAsync(DateOnly tradeDate, bool compress, CancellationToken cancel)
    {
        try
        {
            var outcomes = new List<TapeOutcome>();
            var boundaries = new List<BoundaryChange>();

            if (catalog.HeldOn(tradeDate) is { } held)
            {
                var bars = await BuildBarsAsync(tradeDate, compress, held, outcomes, cancel).ConfigureAwait(false);

                if (bars is not null)
                    return bars;

                var ticks = await BuildTicksAsync(tradeDate, compress, held, outcomes, boundaries, cancel).ConfigureAwait(false);

                if (ticks is not null)
                    return ticks;
            }

            return new BuildReport(tradeDate, outcomes, boundaries, RemoveUnmade(tradeDate, outcomes));
        }
        catch (ArchiveException e)
        {
            return e.Error;
        }
    }

    /// <summary>Deletes the date's tapes of every kind this build didn't save, whatever made them.</summary>
    private List<string> RemoveUnmade(DateOnly tradeDate, List<TapeOutcome> outcomes)
    {
        var removed = new List<string>();

        foreach (var kind in Enum.GetValues<TapeKind>().Where(k => !outcomes.Any(o => o.Kind == k && o.Path is not null)))
        {
            var path = TapePaths.For(tapeRoot, catalog.Dataset, kind, catalog.Root, tradeDate);

            if (File.Exists(path))
            {
                File.Delete(path);
                removed.Add(path);
            }
        }

        return removed;
    }

    private async Task<Error?> BuildBarsAsync(DateOnly tradeDate, bool compress, CatalogContract held,
        List<TapeOutcome> outcomes, CancellationToken cancel)
    {
        var bars = new List<Bar>();

        foreach (var day in new[] { tradeDate.AddDays(-1), tradeDate })
        {
            var opened = await archive.OpenAsync(BarSchema, catalog.Root, day, cancel).ConfigureAwait(false);

            if (!opened.IsSuccess)
            {
                if (opened.Error!.Code == Errors.NoData)
                    continue;

                return opened.Error;
            }

            var reader = opened.Value;

            await using (reader.ConfigureAwait(false))
            {
                await foreach (var raw in reader.ReadRawAsync(cancel).ConfigureAwait(false))
                {
                    if (raw.Span[1] != OhlcvRecord.FirstType)
                        continue;

                    if (DbnRecord.InstrumentOf(raw.Span) == held.InstrumentId)
                        bars.Add(Bar.Read(raw.Span));
                }
            }
        }

        if (calendar.For(held.Symbol, tradeDate) is not { } session)
            return null;

        var open = session.Open / NanosPerSecond;
        var close = session.Close / NanosPerSecond;
        var inside = bars.Where(b => b.Second >= open && b.Second <= close).ToList();

        if (inside.Count > 0)
            outcomes.Add(await SaveBarsAsync(held, session, inside, compress, cancel).ConfigureAwait(false));

        return null;
    }

    private async Task<TapeOutcome> SaveBarsAsync(CatalogContract contract, SessionDay session, List<Bar> bars, bool compress, CancellationToken cancel)
    {
        var tick = contract.TickRaw;
        var first = bars[0];

        if (bars.Find(b => !b.OnTicks(tick)) is { Second: not 0 } off)
            return Refused(contract, TapeKind.S1, $"The bar at {off.Second} is not on whole ticks of {tick}.");

        if (bars.Find(b => b.Volume > uint.MaxValue) is { Second: not 0 } large)
            return Refused(contract, TapeKind.S1, $"The bar at {large.Second} has a volume too large to hold.");

        var writer = new S1Writer(Info(contract, session, compress) with { BasePrice = (int)(first.Open / tick) });
        var rthOpen = session.RthOpen is { } start ? start / NanosPerSecond : long.MaxValue;
        var rthClose = session.RthClose is { } end ? end / NanosPerSecond : long.MaxValue;
        var started = false;
        var ended = false;

        foreach (var bar in bars)
        {
            if (!started && bar.Second >= rthOpen && bar.Second < rthClose)
            {
                started = true;
                writer.MarkRthStart();
            }

            if (started && !ended && bar.Second >= rthClose)
            {
                ended = true;
                writer.MarkRthEnd();
            }

            writer.Add(bar.Second * MicrosPerSecond, (int)(bar.Open / tick), (int)(bar.High / tick), (int)(bar.Low / tick), (int)(bar.Close / tick), (uint)bar.Volume);
        }

        return await SaveAsync(contract, TapeKind.S1, session.TradeDate, writer, cancel).ConfigureAwait(false);
    }

    private async Task<Error?> BuildTicksAsync(DateOnly tradeDate, bool compress, CatalogContract held,
        List<TapeOutcome> outcomes, List<BoundaryChange> boundaries, CancellationToken cancel)
    {
        var files = await MboDaysHeldAsync(tradeDate, cancel).ConfigureAwait(false);

        if (!files.IsSuccess)
            return files.Error;

        // Before the archive's first mbo day a date simply has S1 tapes, and the same goes after its last.
        if (files.Value == 0)
            return null;

        if (files.Value == 1)
        {
            outcomes.Add(new TapeOutcome(held.Symbol, TapeKind.L1, null, 0, 0,
                new Error(TapeErrors.NotHeld, "mbo covers only one side of this trade date.")));

            return null;
        }

        var status = await StatusLog.ReadAsync(archive, catalog.Root, tradeDate, cancel).ConfigureAwait(false);

        if (!status.IsSuccess)
            return status.Error;

        if (Start(held, tradeDate, compress, status.Value) is not { } run)
            return null;

        var seen = false;

        foreach (var day in new[] { tradeDate.AddDays(-1), tradeDate })
        {
            var opened = await archive.OpenAsync(MboSchema, catalog.Root, day, cancel).ConfigureAwait(false);

            if (!opened.IsSuccess)
                return opened.Error;

            var reader = opened.Value;

            await using (reader.ConfigureAwait(false))
            {
                await foreach (var raw in reader.ReadRawAsync(cancel).ConfigureAwait(false))
                {
                    if (raw.Span[1] != MboRecord.Type || DbnRecord.InstrumentOf(raw.Span) != held.InstrumentId)
                        continue;

                    seen = true;
                    run.Feed(raw.Span);
                }
            }

            run.NextFile();
        }

        // mbo is kept for the open-interest front and second, so the volume front is almost always there.
        if (!seen)
        {
            outcomes.Add(new TapeOutcome(held.Symbol, TapeKind.L1, null, 0, 0,
                new Error(TapeErrors.NotHeld, "mbo doesn't hold the day's contract.")));

            return null;
        }

        run.Finish();

        if (run.FilesHeld < 2)
        {
            outcomes.Add(new TapeOutcome(held.Symbol, TapeKind.L1, null, 0, 0,
                new Error(TapeErrors.NotHeld, "mbo holds this contract on only one side of the trade date.")));

            return null;
        }

        if (run.Refusal is { } refusal)
        {
            outcomes.Add(new TapeOutcome(held.Symbol, TapeKind.L1, null, 0, 0, refusal));

            return null;
        }

        if (run.L1 is not { } l1 || run.Trades is not { } trades)
            return null;

        outcomes.Add(await SaveAsync(held, TapeKind.L1, tradeDate, l1, cancel).ConfigureAwait(false));

        if (trades.EventCount > 0)
            outcomes.Add(await SaveAsync(held, TapeKind.Trade, tradeDate, trades, cancel).ConfigureAwait(false));

        if (run.BoundaryChanges > 0)
            boundaries.Add(new BoundaryChange(held.Symbol, run.BoundaryChanges));

        return null;
    }

    /// <summary>How many of a trade date's two UTC files the archive holds mbo for.</summary>
    private async Task<Result<int>> MboDaysHeldAsync(DateOnly tradeDate, CancellationToken cancel)
    {
        var held = 0;

        foreach (var day in new[] { tradeDate.AddDays(-1), tradeDate })
        {
            var found = await archive.FetchAsync(MboSchema, catalog.Root, day, null, cancel).ConfigureAwait(false);

            if (found.IsSuccess)
                held++;
            else if (found.Error!.Code is not (Errors.NotStored or Errors.NoData))
                return found.Error;
        }

        return held;
    }

    private ContractRun? Start(CatalogContract contract, DateOnly tradeDate, bool compress, Dictionary<uint, List<StatusPoint>> status)
    {
        if (calendar.For(contract.Symbol, tradeDate) is not { } session)
            return null;

        var states = SessionRules.States(status.GetValueOrDefault(contract.InstrumentId) ?? [], session.Open, session.Close);

        return new ContractRun(Info(contract, session, compress), session, states);
    }

    private TapeInfo Info(CatalogContract contract, SessionDay session, bool compress) => new()
    {
        Dataset = catalog.Dataset,
        Symbol = contract.Symbol,
        Root = catalog.Root,
        TradeDate = session.TradeDate,
        TickRaw = contract.TickRaw,
        OpenTime = session.Open,
        CloseTime = session.Close,
        BasePrice = 0,
        InstrumentId = contract.InstrumentId,
        Compress = compress
    };

    /// <summary>Saves a tape, then decodes it back and compares, so a writer bug leaves no file.</summary>
    private async Task<TapeOutcome> SaveAsync(CatalogContract contract, TapeKind kind, DateOnly tradeDate, TapeWriter writer, CancellationToken cancel)
    {
        var path = TapePaths.For(tapeRoot, catalog.Dataset, kind, catalog.Root, tradeDate);
        var saved = await writer.SaveAsync(path, cancel).ConfigureAwait(false);

        if (!saved.IsSuccess)
            return new TapeOutcome(contract.Symbol, kind, null, 0, 0, saved.Error);

        var loaded = await Tape.LoadAsync(path, cancel).ConfigureAwait(false);

        if (!loaded.IsSuccess || TapeChecksum.Of(loaded.Value) != writer.Checksum)
        {
            File.Delete(path);

            return Refused(contract, kind, "Decoding the new tape gave a different checksum.");
        }

        return new TapeOutcome(contract.Symbol, kind, path, writer.EventCount, TapeHeader.Size + saved.Value.StoredLength, null);
    }

    private static TapeOutcome Refused(CatalogContract contract, TapeKind kind, string message) =>
        new(contract.Symbol, kind, null, 0, 0, new Error(TapeErrors.Refused, message));

    /// <summary>An ohlcv-1s record, with its second and raw prices.</summary>
    private readonly record struct Bar(long Second, long Open, long High, long Low, long Close, ulong Volume)
    {
        public static Bar Read(ReadOnlySpan<byte> raw) => new(
            (long)(BinaryPrimitives.ReadUInt64LittleEndian(raw[8..]) / NanosPerSecond),
            BinaryPrimitives.ReadInt64LittleEndian(raw[16..]),
            BinaryPrimitives.ReadInt64LittleEndian(raw[24..]),
            BinaryPrimitives.ReadInt64LittleEndian(raw[32..]),
            BinaryPrimitives.ReadInt64LittleEndian(raw[40..]),
            BinaryPrimitives.ReadUInt64LittleEndian(raw[48..]));

        public bool OnTicks(long tick) => Open % tick == 0 && High % tick == 0 && Low % tick == 0 && Close % tick == 0;
    }
}
