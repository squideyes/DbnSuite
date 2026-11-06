using System.Diagnostics;

namespace DbnSpool;

/// <summary>
/// Passes over every root in turn, then waits. It only reads the archive, so nothing
/// that goes wrong here can hold up DbnSync. A root that fails leaves the rest running.
/// </summary>
public sealed class SpoolWorker(
    SpoolPass pass,
    ITapeShelf shelf,
    SpoolOptions options,
    SpoolStatus status,
    TimeProvider clock,
    ILogger<SpoolWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using (var process = Process.GetCurrentProcess())
            process.PriorityClass = ProcessPriorityClass.BelowNormal;

        await shelf.EnsureReadyAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            status.Started();

            var results = new List<RootResult>();
            string? problem = null;

            foreach (var root in options.RootList)
            {
                try
                {
                    results.Add(await pass.RunAsync(root, stoppingToken).ConfigureAwait(false));
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    logger.LogError(e, "{Root}: the pass failed", root);
                    problem = $"{root}: {e.Message}";
                }
            }

            status.Finished(clock.GetUtcNow(), results, problem);

            await Task.Delay(options.Interval, clock, stoppingToken).ConfigureAwait(false);
        }
    }
}
