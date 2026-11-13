using DbnSuite.Alerts;
using DbnSuite.Sync.Core;
using DbnSuite.Sync.Data;

namespace DbnSuite.Sync.Worker;

/// <summary>
/// Runs a pass at once, then again after <see cref="SyncOptions.BusyInterval"/>
/// while jobs are pending or owed, and after <see cref="SyncOptions.Interval"/>
/// once caught up. Each pass gets its own scope, so the typed HTTP client
/// behind it is never held for the life of the process. A pass that changed
/// something or met a problem is recorded in the store for the dashboard.
/// </summary>
public sealed class SyncWorker(
    IServiceScopeFactory scopes,
    S3ArchiveStore store,
    CorpusIndex corpus,
    SyncOptions options,
    SyncStatus status,
    AlertMail alerts,
    TimeProvider clock,
    ILogger<SyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await store.EnsureBucketAsync(stoppingToken).ConfigureAwait(false);

        var profile = options.Profile();

        if (profile.Status == ProfileStatus.Verified)
        {
            logger.LogInformation("Dataset {Dataset}: verified profile, maintained by {Maintainer}",
                profile.Dataset, profile.Maintainer);
        }
        else
        {
            logger.LogWarning("Dataset {Dataset}: contributed profile, maintained by {Maintainer} and never run by " +
                "DbnSuite's maintainer; used at your own risk", profile.Dataset, profile.Maintainer);
        }

        if (options.Spend.Paid)
        {
            logger.LogWarning("Spend policy: {Policy}. Paid jobs are billed by Databento to your account, " +
                "at your own risk; DbnSync is provided as is, with no warranty", options.Spend.Describe());
        }
        else
        {
            logger.LogInformation("Spend policy: {Policy}", options.Spend.Describe());
        }

        logger.LogInformation("Alerts: {Alerts}", alerts.Describe());

        // The dashboard should show the archive at once, not after a first
        // pass that may spend minutes submitting. The scan handles its own errors.
        _ = corpus.RefreshAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            status.Started();

            var startedAt = clock.GetUtcNow();

            PassSummary pass;

            try
            {
                await using var scope = scopes.CreateAsyncScope();

                pass = await scope.ServiceProvider.GetRequiredService<SyncRunner>()
                    .RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Pass failed; retrying after {Delay}", options.BusyInterval);

                pass = PassSummary.Crashed(startedAt, clock.GetUtcNow(), ex);
            }

            await RecordAsync(pass, stoppingToken).ConfigureAwait(false);

            await alerts.RaiseAsync("DbnSync", PassAlerts.For(pass), stoppingToken).ConfigureAwait(false);

            var delay = pass is { Busy: false, Failure: null } ? options.Interval : options.BusyInterval;

            status.Finished(pass, clock.GetUtcNow() + delay);

            await corpus.RefreshAsync(stoppingToken).ConfigureAwait(false);

            try
            {
                await Task.Delay(delay, clock, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RecordAsync(PassSummary pass, CancellationToken cancel)
    {
        if (!pass.Eventful)
            return;

        try
        {
            await store.SavePassAsync(pass, cancel).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancel.IsCancellationRequested)
        {
            logger.LogError(ex, "Pass from {StartedAt} could not be recorded", pass.StartedAt);
        }
    }
}
