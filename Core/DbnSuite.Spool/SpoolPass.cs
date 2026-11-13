using System.Globalization;
using System.IO.Hashing;
using System.Text;
using DbnSuite.Client.Tape;

namespace DbnSuite.Spool;

/// <summary>What one pass over one root did.</summary>
/// <param name="Skipped">True when the archive held nothing new.</param>
/// <param name="Failed">Dates or steps that failed, set-aside dates among them.</param>
/// <param name="Problem">The dates set aside, for someone to look at, or null.</param>
/// <param name="SetAside">How many of the failed dates were set aside.</param>
/// <param name="Trouble">What the first date that threw said, or null when none did.</param>
/// <param name="Shelf">What is wrong with the tape store itself, so the pass did nothing, or null.</param>
public sealed record RootResult(string Root, int Spooled, int Failed, bool Skipped, string? Problem = null, int SetAside = 0,
    string? Trouble = null, string? Shelf = null);

/// <summary>
/// One root's pass: each trade date the shelf lacks is built, proved, uploaded and
/// recorded. A date that fails anywhere puts nothing in the ledger, and is tried
/// again the next time the archive changes. A date that throws is tried again at the
/// next pass, and the dates after it still get done. A date that stops DbnSpool
/// itself, as running out of memory does, is set aside once it has done so
/// <see cref="SpoolOptions.MostAttempts"/> times, so the dates after it still get done.
/// </summary>
public sealed class SpoolPass(ISpoolEngine engine, ITapeShelf shelf, SpoolOptions options, ILogger<SpoolPass> logger)
{
    /// <summary>Dates in a row that may throw before the pass gives up: by then it is the archive or the disk, not the dates.</summary>
    public const int MostThrowsInARow = 3;

    /// <summary>
    /// The largest share of a root's shelved dates one pass may remove as not full ETH days. Short days
    /// are about 3.5% of trade dates, so more than this points at a broken catalog, not at the rules.
    /// </summary>
    public const double MostRemovedShare = 0.1;

    /// <summary>
    /// Names the rules the shelf is kept to. Changing it makes every root's next pass run, and so
    /// enforce them, even when the archive itself has not changed.
    /// </summary>
    private const string Rules = "one-contract";

    private const string MboSchema = "mbo";
    private const string DayFormat = "yyyy-MM-dd";

    private static readonly string[] Sources = ["ohlcv-1s", "definition", "statistics", "status"];

    public async Task<RootResult> RunAsync(string root, CancellationToken cancel)
    {
        var mboFiles = await FilesOf(MboSchema, root, cancel).ConfigureAwait(false);
        var mbo = mboFiles.Keys.ToHashSet();
        var schemas = new List<IReadOnlyCollection<DateOnly>>();

        foreach (var schema in Sources)
            schemas.Add(await DaysOf(schema, root, cancel).ConfigureAwait(false));

        // A deployment that never collects mbo still spools S1, and so does one whose mbo has stopped arriving.
        if (mbo.Count > 0)
        {
            if (SpoolPlan.Through(schemas) is not { } reached || SpoolPlan.StillComing(mbo, reached))
            {
                schemas.Add((IReadOnlyCollection<DateOnly>)mbo);
            }
            else
            {
                logger.LogWarning("{Root}: no mbo has arrived since {Last}, so later dates get bar tapes only until it does",
                    root, mbo.Max());
            }
        }

        if (SpoolPlan.Through(schemas) is not { } through)
        {
            logger.LogInformation("{Root}: the archive holds no complete day yet", root);

            return new RootResult(root, 0, 0, Skipped: true);
        }

        var key = SpoolLedger.KeyOf(engine.Dataset, root);
        var ledger = SpoolLedger.From(await shelf.ReadAsync(key, cancel).ConfigureAwait(false));

        // mbo's tags are in it, so fetching an mbo day again makes the next pass run.
        var seen = $"{through.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}/{mbo.Count}/{Fingerprint(mboFiles)}/{Rules}";

        if (ledger.Seen == seen)
        {
            // Nothing new to do, but dates still set aside are still a problem, and are reported for as long as they are.
            var still = ledger.Attempts.Where(a => ledger.IsSetAside(a.Date, engine.Build, options.MostAttempts)).Select(a => a.Date).ToList();

            return new RootResult(root, 0, still.Count, Skipped: true, SetAsideProblem(root, still), still.Count);
        }

        var (anyShelved, old) = await ShelvedAsync(root, cancel).ConfigureAwait(false);

        // A ledger that lists dates the shelf no longer holds, because the tapes were deleted, would keep them from being made again.
        if (!anyShelved && ledger.Dates.Count > 0)
        {
            logger.LogWarning("{Root}: the ledger lists {Dates} dates but the shelf holds no tapes, so it starts again", root, ledger.Dates.Count);

            ledger = new SpoolLedger();
        }

        if (old is not null)
        {
            var problem = $"{root}: the tape store holds tapes in the old format, one per contract, such as {old}. " +
                $"Stop DbnSpool, delete everything under {engine.Dataset}/ in the tape store (the tapes, catalog/ and spool/), " +
                "and start it again: it then builds every date in the new format";

            logger.LogError("{Problem}", problem);

            return new RootResult(root, 0, 0, Skipped: false, Shelf: problem);
        }

        var catalog = await engine.CatalogAsync(root, cancel).ConfigureAwait(false);

        if (!catalog.IsSuccess)
        {
            logger.LogError("{Root}: no catalog: {Problem}", root, catalog.Error!.Message);

            return new RootResult(root, 0, 1, Skipped: false);
        }

        var spooled = 0;
        var failed = 0;
        var removal = await RemoveExcludedAsync(catalog.Value, ledger, cancel).ConfigureAwait(false);

        await shelf.PutBytesAsync(key, ledger.ToBytes(), cancel).ConfigureAwait(false);

        // Only full ETH days get tapes, so holidays and early closes are never pending.
        var pending = SpoolPlan.Pending(catalog.Value.FullDays.Select(d => d.TradeDate), ledger, mbo, through,
            date => StampOf(mboFiles, date));
        var threw = 0;
        var throwsInARow = 0;
        string? trouble = null;
        var setAside = new List<DateOnly>();

        logger.LogInformation("{Root}: {Dates} trade dates to spool, through {Through}", root, pending.Count, through);

        foreach (var date in pending)
        {
            if (ledger.IsSetAside(date, engine.Build, options.MostAttempts))
            {
                logger.LogError("{Root} {Date}: set aside, because it has stopped DbnSpool {Count} times; it is tried again once DbnSpool's code or its memory limit changes",
                    root, date, ledger.Attempt(date)!.Count);

                setAside.Add(date);
                failed++;

                continue;
            }

            // Saved before the work starts, so a stop that ends the process is still counted.
            ledger.Begin(date, engine.Build);

            await shelf.PutBytesAsync(key, ledger.ToBytes(), cancel).ConfigureAwait(false);

            bool done;

            try
            {
                done = await SpoolAsync(catalog.Value, date, SpoolPlan.HasTicks(date, mbo), StampOf(mboFiles, date), ledger, cancel)
                    .ConfigureAwait(false);
                throwsInARow = 0;
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                // A shutdown is not the date's fault: only a stop the date itself causes counts.
                ledger.End(date);

                await TrySaveAsync(key, ledger).ConfigureAwait(false);

                throw;
            }
            catch (OutOfMemoryException)
            {
                // Running out of memory is the stop the count exists for, whether it ends the process or only
                // this pass. The attempt stays counted, so the date is set aside once it has done this often enough.
                throw;
            }
            catch (Exception e)
            {
                // Anything else is not counted against the date, but must not hold up the dates after it.
                ledger.End(date);

                await TrySaveAsync(key, ledger).ConfigureAwait(false);

                // The same failure on date after date is the archive or the disk, so the pass stops and says so.
                if (++throwsInARow >= MostThrowsInARow)
                    throw;

                logger.LogError(e, "{Root} {Date}: failed, and is tried again at the next pass", root, date);

                failed++;
                threw++;
                trouble ??= $"{date.ToString(DayFormat, CultureInfo.InvariantCulture)}: {e.Message}";

                Clear();
                engine.Release();

                continue;
            }

            ledger.End(date);

            // Not cancellable: the tapes are on the shelf, so the ledger must say so even during a shutdown.
            await shelf.PutBytesAsync(key, ledger.ToBytes(), CancellationToken.None).ConfigureAwait(false);

            if (done)
                spooled++;
            else
                failed++;

            Clear();
            engine.Release();
        }

        var catalogSaved = await SaveCatalogAsync(catalog.Value, cancel).ConfigureAwait(false);

        if (!catalogSaved)
            failed++;

        // A date that threw, or a catalog that was not saved, is tried again at the next pass, not only when
        // the archive next changes.
        if (threw == 0 && catalogSaved)
            ledger.Seen = seen;

        await shelf.PutBytesAsync(key, ledger.ToBytes(), cancel).ConfigureAwait(false);

        return new RootResult(root, spooled, failed, Skipped: false, SetAsideProblem(root, setAside), setAside.Count, trouble, removal);
    }

    /// <summary>
    /// Takes off the shelf every tape, and out of the ledger every date, that the catalog says is not
    /// a full ETH day, so the shelf holds only what the rules allow, whatever an older version put there.
    /// A catalog that would remove more than <see cref="MostRemovedShare"/> of the shelf's dates is not
    /// trusted: nothing is removed, and the pass says so.
    /// </summary>
    /// <returns>Null, or the problem that stopped the removal.</returns>
    private async Task<string?> RemoveExcludedAsync(Catalog catalog, SpoolLedger ledger, CancellationToken cancel)
    {
        var keys = new List<string>();
        var dates = new SortedSet<DateOnly>();
        var shelved = new HashSet<DateOnly>();

        foreach (var kind in TapePaths.Kinds)
        {
            await foreach (var key in shelf.KeysAsync(TapePaths.KindFolder("", engine.Dataset, kind, catalog.Root), cancel).ConfigureAwait(false))
            {
                if (TapePaths.TradeDateOf(key) is not { } date)
                    continue;

                shelved.Add(date);

                if (catalog.Judges(date) && !catalog.IsFullDay(date))
                {
                    keys.Add(key);
                    dates.Add(date);
                }
            }
        }

        foreach (var spooled in ledger.Dates.Where(d => catalog.Judges(d.Date) && !catalog.IsFullDay(d.Date)))
            dates.Add(spooled.Date);

        if (dates.Count == 0)
            return null;

        if (dates.Count > Math.Max(1, shelved.Count * MostRemovedShare))
        {
            var problem = $"{catalog.Root}: the catalog would take {dates.Count} of the shelf's {shelved.Count} dates off it as not full ETH days, " +
                "which is more than a catalog should; nothing was removed";

            logger.LogError("{Problem}", problem);

            return problem;
        }

        foreach (var key in keys)
            await shelf.DeleteAsync(key, cancel).ConfigureAwait(false);

        foreach (var date in dates)
        {
            ledger.Remove(date);
            logger.LogInformation("{Root} {Date}: tapes removed, because {Reason}", catalog.Root, date, catalog.Excluded(date));
        }

        logger.LogInformation("{Root}: {Tapes} tapes of {Dates} dates that are not full ETH days removed from the shelf", catalog.Root, keys.Count, dates.Count);

        return null;
    }

    private string? SetAsideProblem(string root, IReadOnlyList<DateOnly> dates) =>
        dates.Count == 0
            ? null
            : $"{root}: {string.Join(", ", dates.Select(d => d.ToString(DayFormat, CultureInfo.InvariantCulture)))} set aside after stopping DbnSpool {options.MostAttempts} times";

    private async Task TrySaveAsync(string key, SpoolLedger ledger)
    {
        try
        {
            await shelf.PutBytesAsync(key, ledger.ToBytes(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "The ledger at {Key} was not saved", key);
        }
    }

    /// <summary>
    /// Whether the shelf holds any of the root's tapes, and a key of one named the old way, by contract
    /// rather than by root, or null when there is none.
    /// </summary>
    private async Task<(bool Any, string? Old)> ShelvedAsync(string root, CancellationToken cancel)
    {
        var any = false;

        foreach (var kind in TapePaths.Kinds)
        {
            await foreach (var key in shelf.KeysAsync(TapePaths.KindFolder("", engine.Dataset, kind, root), cancel).ConfigureAwait(false))
            {
                if (TapePaths.TradeDateOf(key) is null)
                    continue;

                any = true;

                if (!key[(key.LastIndexOf('/') + 1)..].StartsWith($"{root}.", StringComparison.Ordinal))
                    return (true, key);
            }
        }

        return (any, null);
    }

    private async Task<bool> SpoolAsync(Catalog catalog, DateOnly date, bool ticks, string? stamp, SpoolLedger ledger, CancellationToken cancel)
    {
        var built = await engine.BuildAsync(catalog, date, cancel).ConfigureAwait(false);

        if (!built.IsSuccess)
        {
            logger.LogError("{Root} {Date}: refused: {Problem}", catalog.Root, date, built.Error!.Message);

            return false;
        }

        var refused = built.Value.Tapes.Where(t => t.Error is { Code: not TapeErrors.NotHeld }).ToList();

        foreach (var tape in refused)
            logger.LogError("{Root} {Date}: {Symbol} {Kind} refused: {Problem}", catalog.Root, date, tape.Symbol, tape.Kind, tape.Error!.Message);

        if (refused.Count > 0)
            return false;

        if (options.Prove)
        {
            var results = await engine.ProveAsync(catalog, date, cancel).ConfigureAwait(false);

            foreach (var result in results.Where(r => !r.Passed))
                logger.LogError("{Root} {Date}: {Symbol} failed {Check}: {Detail}", catalog.Root, date, result.Symbol, result.Check, result.Detail);

            if (results.Any(r => !r.Passed))
                return false;
        }

        var tapes = built.Value.Tapes.Where(t => t.Path is not null).ToList();

        foreach (var tape in tapes)
            await shelf.PutFileAsync(KeyOf(tape.Path!), tape.Path!, cancel).ConfigureAwait(false);

        // A kind not made this time must not keep a tape from an earlier build, which may hold another contract.
        foreach (var kind in Enum.GetValues<TapeKind>().Where(k => tapes.All(t => t.Kind != k)))
            await shelf.DeleteAsync(KeyOf(TapePaths.For(options.TapeFolder, engine.Dataset, kind, catalog.Root, date)), cancel).ConfigureAwait(false);

        var made = tapes.Any(t => t.Kind == TapeKind.L1);

        if (ticks && !made)
        {
            var why = built.Value.Tapes.FirstOrDefault(t => t.Kind == TapeKind.L1 && t.Error is not null)?.Error?.Message ?? "no L1 tape was made";

            logger.LogWarning("{Root} {Date}: bar tapes only, because {Why} It is tried again if its mbo is fetched again",
                catalog.Root, date, why);
        }

        ledger.Add(new SpooledDate(date, tapes.Count, made, ticks && !made ? stamp : null));

        logger.LogInformation("{Root} {Date}: {Tapes} tapes on the shelf", catalog.Root, date, tapes.Count);

        return true;
    }

    private async Task<bool> SaveCatalogAsync(Catalog catalog, CancellationToken cancel)
    {
        var saved = await catalog.SaveAsync(options.TapeFolder, cancel).ConfigureAwait(false);

        if (saved.IsSuccess)
            await shelf.PutFileAsync(KeyOf(saved.Value), saved.Value, cancel).ConfigureAwait(false);
        else
            logger.LogError("{Root}: catalog not saved: {Problem}", catalog.Root, saved.Error!.Message);

        return saved.IsSuccess;
    }

    private string KeyOf(string path) => Path.GetRelativePath(options.TapeFolder, path).Replace('\\', '/');

    private void Clear()
    {
        if (Directory.Exists(options.TapeFolder))
            Directory.Delete(options.TapeFolder, recursive: true);
    }

    private async Task<HashSet<DateOnly>> DaysOf(string schema, string root, CancellationToken cancel) =>
        (await FilesOf(schema, root, cancel).ConfigureAwait(false)).Keys.ToHashSet();

    /// <summary>Each day the archive holds a schema for, with its file's tag; mbo's empty days are left out.</summary>
    private async Task<Dictionary<DateOnly, string>> FilesOf(string schema, string root, CancellationToken cancel)
    {
        var days = await engine.DaysAsync(schema, root, cancel).ConfigureAwait(false);

        if (!days.IsSuccess)
            throw new InvalidOperationException($"{root} {schema}: {days.Error!.Message}");

        return days.Value.Where(d => schema != MboSchema || !d.Empty).ToDictionary(d => d.Day, d => d.Object.ETag);
    }

    /// <summary>The tags of a trade date's two mbo files, or null when either is missing.</summary>
    private static string? StampOf(Dictionary<DateOnly, string> mbo, DateOnly tradeDate) =>
        mbo.TryGetValue(tradeDate.AddDays(-1), out var before) && mbo.TryGetValue(tradeDate, out var on) ? $"{before}|{on}" : null;

    private static string Fingerprint(Dictionary<DateOnly, string> files) =>
        XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(string.Join(',', files.OrderBy(f => f.Key).Select(f => $"{f.Key:yyyyMMdd}={f.Value}"))))
            .ToString("x16", CultureInfo.InvariantCulture);
}
