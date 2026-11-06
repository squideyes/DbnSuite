using DbnClient.Tape;

namespace DbnSpool;

/// <summary>What one pass over one root did.</summary>
/// <param name="Skipped">True when the archive held nothing new.</param>
public sealed record RootResult(string Root, int Spooled, int Failed, bool Skipped);

/// <summary>
/// One root's pass: each trade date the shelf lacks is built, proved, uploaded and
/// recorded. A date that fails anywhere puts nothing in the ledger, and is tried
/// again the next time the archive changes.
/// </summary>
public sealed class SpoolPass(ISpoolEngine engine, ITapeShelf shelf, SpoolOptions options, ILogger<SpoolPass> logger)
{
    private const string MboSchema = "mbo";

    private static readonly string[] Sources = ["ohlcv-1s", "definition", "statistics", "status"];

    public async Task<RootResult> RunAsync(string root, CancellationToken cancel)
    {
        var mbo = await DaysOf(MboSchema, root, cancel).ConfigureAwait(false);
        var schemas = new List<IReadOnlyCollection<DateOnly>>();

        foreach (var schema in Sources)
            schemas.Add(await DaysOf(schema, root, cancel).ConfigureAwait(false));

        // A deployment that never collects mbo still spools S1.
        if (mbo.Count > 0)
            schemas.Add((IReadOnlyCollection<DateOnly>)mbo);

        if (SpoolPlan.Through(schemas) is not { } through)
        {
            logger.LogInformation("{Root}: the archive holds no complete day yet", root);

            return new RootResult(root, 0, 0, Skipped: true);
        }

        var key = SpoolLedger.KeyOf(engine.Dataset, root);
        var ledger = SpoolLedger.From(await shelf.ReadAsync(key, cancel).ConfigureAwait(false));
        var seen = $"{through:yyyyMMdd}/{mbo.Count}";

        if (ledger.Seen == seen)
            return new RootResult(root, 0, 0, Skipped: true);

        var catalog = await engine.CatalogAsync(root, cancel).ConfigureAwait(false);

        if (!catalog.IsSuccess)
        {
            logger.LogError("{Root}: no catalog: {Problem}", root, catalog.Error!.Message);

            return new RootResult(root, 0, 1, Skipped: false);
        }

        var pending = SpoolPlan.Pending(catalog.Value.Days.Select(d => d.TradeDate), ledger, mbo, through);
        var spooled = 0;
        var failed = 0;

        logger.LogInformation("{Root}: {Dates} trade dates to spool, through {Through}", root, pending.Count, through);

        foreach (var date in pending)
        {
            if (await SpoolAsync(catalog.Value, date, SpoolPlan.HasTicks(date, mbo), ledger, cancel).ConfigureAwait(false))
            {
                await shelf.PutBytesAsync(key, ledger.ToBytes(), cancel).ConfigureAwait(false);
                spooled++;
            }
            else
            {
                failed++;
            }

            Clear();
            engine.Release();
        }

        await SaveCatalogAsync(catalog.Value, cancel).ConfigureAwait(false);

        ledger.Seen = seen;

        await shelf.PutBytesAsync(key, ledger.ToBytes(), cancel).ConfigureAwait(false);

        return new RootResult(root, spooled, failed, Skipped: false);
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

    private async Task SaveCatalogAsync(Catalog catalog, CancellationToken cancel)
    {
        var saved = await catalog.SaveAsync(options.TapeFolder, cancel).ConfigureAwait(false);

        if (saved.IsSuccess)
            await shelf.PutFileAsync(KeyOf(saved.Value), saved.Value, cancel).ConfigureAwait(false);
        else
            logger.LogError("{Root}: catalog not saved: {Problem}", catalog.Root, saved.Error!.Message);
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
