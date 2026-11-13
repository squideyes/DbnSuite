using DbnSuite.Sync.Core;

namespace DbnSuite.Sync.Worker;

/// <summary>Every series as the store holds it, as of one scan.</summary>
public sealed record CorpusSnapshot(
    DateTimeOffset ScannedAt,
    IReadOnlyList<Series> Series,
    IReadOnlyList<PendingJob> Pending,
    string? Failure)
{
    public long TotalBytes => Series.Sum(s => s.TotalBytes);

    public int Count(DayState state) => Series.Sum(s => s.Count(state));
}

/// <summary>
/// Scans the store into a <see cref="CorpusSnapshot"/> after every pass and
/// on demand. A scan lists every key of every series, which is cheap on a LAN
/// but not something to repeat for each page view, so pages read the snapshot.
/// </summary>
public sealed class CorpusIndex(
    IServiceScopeFactory scopes,
    IArchiveStore store,
    SyncOptions options,
    [FromKeyedServices(SyncClock.Key)] TimeProvider clock,
    ILogger<CorpusIndex> logger)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    private volatile CorpusSnapshot? snapshot;

    public CorpusSnapshot? Snapshot => snapshot;

    public bool Scanning => gate.CurrentCount == 0;

    /// <summary>Raised when a scan starts or finishes.</summary>
    public event Action? Changed;

    /// <summary>Scans now, or waits for the scan already running.</summary>
    public async Task RefreshAsync(CancellationToken cancel)
    {
        if (!await gate.WaitAsync(0, cancel).ConfigureAwait(false))
        {
            await gate.WaitAsync(cancel).ConfigureAwait(false);
            gate.Release();

            return;
        }

        await ScanHoldingGateAsync(cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// Scans once any scan already running has finished. A scan that began
    /// before a pass cannot show what the pass changed, so the worker uses
    /// this, not <see cref="RefreshAsync"/>, after each pass.
    /// </summary>
    public async Task RescanAsync(CancellationToken cancel)
    {
        await gate.WaitAsync(cancel).ConfigureAwait(false);

        await ScanHoldingGateAsync(cancel).ConfigureAwait(false);
    }

    private async Task ScanHoldingGateAsync(CancellationToken cancel)
    {
        try
        {
            // Inside the try: a listener that throws must not leave the gate held, or no scan would ever run again.
            Changed?.Invoke();

            snapshot = await ScanAsync(cancel).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancel.IsCancellationRequested)
        {
            logger.LogError(ex, "Corpus scan failed");

            snapshot = (snapshot ?? new CorpusSnapshot(clock.GetUtcNow(), [], [], null))
                with
            { Failure = $"{ex.GetType().Name}: {ex.Message}" };
        }
        finally
        {
            gate.Release();
        }

        Changed?.Invoke();
    }

    private async Task<CorpusSnapshot> ScanAsync(CancellationToken cancel)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        IReadOnlyDictionary<string, DateTimeOffset> ends;

        await using (var scope = scopes.CreateAsyncScope())
        {
            var range = await scope.ServiceProvider.GetRequiredService<IDatabento>()
                .GetAvailableEndsAsync(options.Dataset, cancel).ConfigureAwait(false);

            ends = range.IsSuccess ? range.Value : new Dictionary<string, DateTimeOffset>();
        }

        var pending = await store.ListPendingAsync(cancel).ConfigureAwait(false);
        var wanted = options.Wanted().ToList();
        var series = new Series[wanted.Count];

        await Parallel.ForAsync(0, wanted.Count,
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancel },
            async (i, token) =>
            {
                var (root, schema) = wanted[i];

                var days = await store.ListDaysAsync(schema.Name, root, token).ConfigureAwait(false);

                var lastComplete = ends.TryGetValue(schema.Name, out var end)
                    ? Entitlement.LastCompleteDay(end)
                    : today.AddDays(-1);

                var inFlight = pending
                    .Where(p => p.Root == root && p.Schema == schema.Name)
                    .Select(p => p.Range)
                    .ToList();

                series[i] = Corpus.Build(root, schema, days, inFlight, today, lastComplete,
                    options.HistoryStart, options.ReachBackTo);
            }).ConfigureAwait(false);

        return new CorpusSnapshot(clock.GetUtcNow(), series, pending, null);
    }
}
