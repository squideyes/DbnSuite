using System.Diagnostics;
using DbnSuite.Alerts;

namespace DbnSuite.Spool;

/// <summary>
/// Passes over every root in turn, then waits. It only reads the archive, so nothing
/// that goes wrong here can hold up DbnSync. A root that fails leaves the rest running.
/// </summary>
public sealed class SpoolWorker(
    SpoolPass pass,
    ITapeShelf shelf,
    SpoolOptions options,
    SpoolStatus status,
    AlertMail alerts,
    TimeProvider clock,
    ILogger<SpoolWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using (var process = Process.GetCurrentProcess())
            process.PriorityClass = ProcessPriorityClass.BelowNormal;

        logger.LogInformation("Alerts: {Alerts}", alerts.Describe());

        await shelf.EnsureReadyAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            status.Started();

            var results = new List<RootResult>();
            var errors = new List<RootError>();
            var problems = new List<string>();

            foreach (var root in options.RootList)
            {
                try
                {
                    var result = await pass.RunAsync(root, stoppingToken).ConfigureAwait(false);

                    results.Add(result);
                    status.RootDone(result);

                    if (result.Problem is { } problem)
                        problems.Add(problem);

                    if (result.Shelf is { } shelf)
                        problems.Add(shelf);

                    if (result.Trouble is { } trouble)
                        problems.Add($"{root} {trouble}");
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    logger.LogError(e, "{Root}: the pass failed", root);

                    errors.Add(new RootError(root, e.Message));
                    problems.Add($"{root}: {e.Message}");
                    status.Problem(problems[^1]);
                }
            }

            status.Finished(clock.GetUtcNow(), results, problems.Count == 0 ? null : string.Join("; ", problems));

            await alerts.RaiseAsync("DbnSpool", SpoolAlerts.For(results, errors), stoppingToken).ConfigureAwait(false);

            await Task.Delay(options.Interval, clock, stoppingToken).ConfigureAwait(false);
        }
    }
}
