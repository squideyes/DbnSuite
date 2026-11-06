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
public sealed record BuildReport(DateOnly TradeDate, IReadOnlyList<TapeOutcome> Tapes, IReadOnlyList<BoundaryChange> Boundaries);

/// <summary>Where a tape lives under a tape root.</summary>
public static class TapePaths
{
    public static string For(string tapeRoot, string dataset, TapeKind kind, string root, string symbol, DateOnly tradeDate)
    {
        var name = Name(kind);

        return Path.Combine(tapeRoot, dataset, name, root, tradeDate.Year.ToString(CultureInfo.InvariantCulture),
            $"{symbol}.{name}.{tradeDate:yyyyMMdd}.tape");
    }

    private static string Name(TapeKind kind) => kind switch
    {
        TapeKind.S1 => "s1",
        TapeKind.Trade => "trade",
        _ => "l1"
    };
}

/// <summary>
/// Turns one trade date of a root into tapes, and for each tape either saves a whole,
/// checked file or nothing. A trade date usually draws on two of the archive's UTC files.
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
    /// <param name="symbols">The contracts to build, or null for all of them.</param>
    /// <param name="cancel">Stops the build.</param>
    public async Task<Result<BuildReport>> BuildAsync(DateOnly tradeDate, bool compress, IReadOnlySet<string>? symbols, CancellationToken cancel)
    {
        try
        {
            var outcomes = new List<TapeOutcome>();
            var boundaries = new List<BoundaryChange>();

            var bars = await BuildBarsAsync(tradeDate, compress, symbols, outcomes, cancel).ConfigureAwait(false);

            if (bars is not null)
                return bars;

            var ticks = await BuildTicksAsync(tradeDate, compress, symbols, outcomes, boundaries, cancel).ConfigureAwait(false);

            if (ticks is not null)
                return ticks;

            return new BuildReport(tradeDate, outcomes, boundaries);
        }
        catch (ArchiveException e)
        {
            return e.Error;
        }
    }

    private async Task<Error?> BuildBarsAsync(DateOnly tradeDate, bool compress, IReadOnlySet<string>? symbols,
        List<TapeOutcome> outcomes, CancellationToken cancel)
    {
        var bars = new Dictionary<uint, List<Bar>>();

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

                    var id = DbnRecord.InstrumentOf(raw.Span);

                    if (!bars.TryGetValue(id, out var list))
                        bars[id] = list = [];

                    list.Add(Bar.Read(raw.Span));
                }
            }
        }

        foreach (var (id, list) in bars.OrderBy(b => b.Key))
        {
            if (catalog.Contract(id) is not { } contract || !Wanted(contract, symbols) || !IsTheNameOn(tradeDate, contract))
                continue;

            if (calendar.For(contract.Symbol, tradeDate) is not { } session)
                continue;

            var open = session.Open / NanosPerSecond;
            var close = session.Close / NanosPerSecond;
            var inside = list.Where(b => b.Second >= open && b.Second <= close).ToList();

            if (inside.Count == 0)
                continue;

            outcomes.Add(await SaveBarsAsync(contract, session, inside, compress, cancel).ConfigureAwait(false));
        }

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

    private async Task<Error?> BuildTicksAsync(DateOnly tradeDate, bool compress, IReadOnlySet<string>? symbols,
        List<TapeOutcome> outcomes, List<BoundaryChange> boundaries, CancellationToken cancel)
    {
        var held = await MboDaysHeldAsync(tradeDate, cancel).ConfigureAwait(false);

        if (!held.IsSuccess)
            return held.Error;

        // Before the archive's first mbo day a date simply has S1 tapes, and the same goes after its last.
        if (held.Value == 0)
            return null;

        if (held.Value == 1)
        {
            outcomes.Add(new TapeOutcome(catalog.Root, TapeKind.L1, null, 0, 0,
                new Error(TapeErrors.NotHeld, "mbo covers only one side of this trade date.")));

            return null;
        }

        var status = await StatusLog.ReadAsync(archive, catalog.Root, tradeDate, cancel).ConfigureAwait(false);

        if (!status.IsSuccess)
            return status.Error;

        var runs = new Dictionary<uint, ContractRun?>();

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
                    if (raw.Span[1] != MboRecord.Type)
                        continue;

                    var id = DbnRecord.InstrumentOf(raw.Span);

                    if (!runs.TryGetValue(id, out var run))
                        runs[id] = run = Start(id, tradeDate, compress, symbols, status.Value);

                    run?.Feed(raw.Span);
                }
            }

            foreach (var run in runs.Values)
                run?.NextFile();
        }

        foreach (var (id, run) in runs.OrderBy(r => r.Key))
        {
            if (run is null)
                continue;

            var contract = catalog.Contract(id)!;

            run.Finish();

            if (run.FilesHeld < 2)
            {
                outcomes.Add(new TapeOutcome(contract.Symbol, TapeKind.L1, null, 0, 0,
                    new Error(TapeErrors.NotHeld, "mbo holds this contract on only one side of the trade date.")));
                continue;
            }

            if (run.Refusal is { } refusal)
            {
                outcomes.Add(new TapeOutcome(contract.Symbol, TapeKind.L1, null, 0, 0, refusal));
                continue;
            }

            if (run.L1 is not { } l1 || run.Trades is not { } trades)
                continue;

            outcomes.Add(await SaveAsync(contract, TapeKind.L1, tradeDate, l1, cancel).ConfigureAwait(false));

            if (trades.EventCount > 0)
                outcomes.Add(await SaveAsync(contract, TapeKind.Trade, tradeDate, trades, cancel).ConfigureAwait(false));

            if (run.BoundaryChanges > 0)
                boundaries.Add(new BoundaryChange(contract.Symbol, run.BoundaryChanges));
        }

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

    private ContractRun? Start(uint id, DateOnly tradeDate, bool compress, IReadOnlySet<string>? symbols, Dictionary<uint, List<StatusPoint>> status)
    {
        if (catalog.Contract(id) is not { } contract || !Wanted(contract, symbols) || !IsTheNameOn(tradeDate, contract))
            return null;

        if (calendar.For(contract.Symbol, tradeDate) is not { } session)
            return null;

        var states = SessionRules.States(status.GetValueOrDefault(id) ?? [], session.Open, session.Close);

        return new ContractRun(Info(contract, session, compress), session, states);
    }

    /// <summary>Tapes are named by symbol, so only one contract of a name may have a tape on a date.</summary>
    private bool IsTheNameOn(DateOnly tradeDate, CatalogContract contract) =>
        catalog.Contract(contract.Symbol, tradeDate)?.InstrumentId == contract.InstrumentId;

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
        var path = TapePaths.For(tapeRoot, catalog.Dataset, kind, catalog.Root, contract.Symbol, tradeDate);
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

    private static bool Wanted(CatalogContract contract, IReadOnlySet<string>? symbols) =>
        symbols is null || symbols.Contains(contract.Symbol);

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
