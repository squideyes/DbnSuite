using System.Globalization;
using DbnClient.Tape;

namespace DbnSpool;

/// <summary>What one pass over one root did.</summary>
/// <param name="Skipped">True when the archive held nothing new.</param>
/// <param name="Failed">Dates or steps that failed, set-aside dates among them.</param>
/// <param name="Problem">The dates set aside, for someone to look at, or null.</param>
/// <param name="SetAside">How many of the failed dates were set aside.</param>
/// <param name="Trouble">What the first date that threw said, or null when none did.</param>
public sealed record RootResult(string Root, int Spooled, int Failed, bool Skipped, string? Problem = null, int SetAside = 0,
    string? Trouble = null);

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

    private const string MboSchema = "mbo";
    private const string DayFormat = "yyyy-MM-dd";

    private static readonly string[] Sources = ["ohlcv-1s", "definition", "statistics", "status"];

    public async Task<RootResult> RunAsync(string root, CancellationToken cancel)
    {
        var mbo = await DaysOf(MboSchema, root, cancel).ConfigureAwait(false);
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
        var seen = $"{through.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}/{mbo.Count}";

        if (ledger.Seen == seen)
        {
            // Nothing new to do, but dates still set aside are still a problem, and are reported for as long as they are.
            var still = ledger.Attempts.Where(a => ledger.IsSetAside(a.Date, engine.Build, options.MostAttempts)).Select(a => a.Date).ToList();

            return new RootResult(root, 0, still.Count, Skipped: true, SetAsideProblem(root, still), still.Count);
        }

        var catalog = await engine.CatalogAsync(root, cancel).ConfigureAwait(false);

        if (!catalog.IsSuccess)
        {
            logger.LogError("{Root}: no catalog: {Problem}", root, catalog.Error!.Message);

            return new RootResult(root, 0, 1, Skipped: false);
        }

        var pending = SpoolPlan.Pending(catalog.Value.Days.Select(d => d.TradeDate), ledger, mbo, through);
        var spooled = 0;
        var failed = 0;
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
                done = await SpoolAsync(catalog.Value, date, SpoolPlan.HasTicks(date, mbo), ledger, cancel).ConfigureAwait(false);
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

        return new RootResult(root, spooled, failed, Skipped: false, SetAsideProblem(root, setAside), setAside.Count, trouble);
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

    private async Task<bool> SpoolAsync(Catalog catalog, DateOnly date, bool ticks, SpoolLedger ledger, CancellationToken cancel)
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

        ledger.Add(new SpooledDate(date, tapes.Count, ticks));

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

    private async Task<HashSet<DateOnly>> DaysOf(string schema, string root, CancellationToken cancel)
    {
        var days = await engine.DaysAsync(schema, root, cancel).ConfigureAwait(false);

        if (!days.IsSuccess)
            throw new InvalidOperationException($"{root} {schema}: {days.Error!.Message}");

        return days.Value.Where(d => schema != MboSchema || !d.Empty).Select(d => d.Day).ToHashSet();
    }
}
