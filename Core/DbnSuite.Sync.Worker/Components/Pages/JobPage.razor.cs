using DbnSuite.Sync.Core;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;

namespace DbnSuite.Sync.Worker.Components.Pages;

public sealed partial class JobPage : IDisposable
{
    /// <summary>How far back to look for the job's issues; the Activity page's longest range.</summary>
    private const int HistoryDays = 30;

    private readonly CancellationTokenSource leaving = new();

    [Parameter] public string Id { get; set; } = "";

    [Inject] private IArchiveStore Store { get; set; } = null!;

    [Inject] private IDatabento Databento { get; set; } = null!;

    [Inject] private CorpusIndex Index { get; set; } = null!;

    [Inject] private TimeProvider Clock { get; set; } = null!;

    [Inject] private IOptions<DashboardOptions> Dashboard { get; set; } = null!;

    private bool Loading { get; set; } = true;

    private string? LoadError { get; set; }

    private PendingJob? Pending { get; set; }

    private Result<JobState>? State { get; set; }

    /// <summary>Every issue this job met, newest first.</summary>
    private IReadOnlyList<Issue> Issues { get; set; } = [];

    private string? Schema => Pending?.Schema ?? Issues.FirstOrDefault(i => i.Schema is not null)?.Schema;

    private string? Root => Pending?.Root ?? Issues.FirstOrDefault(i => i.Root is not null)?.Root;

    private DayRange? Range => Pending?.Range ?? Issues.FirstOrDefault(i => i.Range is not null)?.Range;

    /// <summary>The job's days as the archive holds them now.</summary>
    private Series? Days =>
        Index.Snapshot?.Series.FirstOrDefault(s => s.Schema.Name == Schema && s.Root == Root) is { } series
            && Range is { } range
            ? series with
            {
                Span = range,
                States = series.States.Where(p => range.Contains(p.Key)).ToDictionary(),
                Bytes = series.Bytes.Where(p => range.Contains(p.Key)).ToDictionary()
            }
            : null;

    /// <summary>One entry per code, latest occurrence first, so a retry every five minutes reads as one problem.</summary>
    private IReadOnlyList<(Issue Latest, int Count, DateTimeOffset First)> Problems =>
    [
        .. Issues.GroupBy(i => i.Code)
            .Select(g => (g.First(), g.Count(), g.Last().At))
    ];

    private string? LogsUrl => Dashboard.Value.LogsForJob(Id);

    protected override async Task OnParametersSetAsync()
    {
        Loading = true;

        var today = DateOnly.FromDateTime(Clock.GetUtcNow().UtcDateTime);

        try
        {
            var pending = Store.ListPendingAsync(leaving.Token);
            var passes = Store.ListPassesAsync(today.AddDays(-HistoryDays), today, leaving.Token);

            Pending = (await pending).FirstOrDefault(j => j.Id == Id);
            Issues = [.. (await passes).SelectMany(p => p.Issues).Where(i => i.JobId == Id).OrderByDescending(i => i.At)];
            State = null;

            if (Pending is { } job)
                State = await Databento.GetStateAsync(job.Id, job.SubmittedAt, leaving.Token);
            LoadError = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LoadError = ex.Message;
        }

        Loading = false;
    }

    public void Dispose()
    {
        leaving.Cancel();
        leaving.Dispose();
    }

    private string Verdict(Series? days)
    {
        if (Pending is not null)
        {
            if (State is not { } state)
                return "In flight.";

            if (!state.IsSuccess)
                return $"In flight. Databento's view is unavailable: {state.Error!.Message}";

            return state.Value switch
            {
                JobState.Working => "In flight: Databento is still preparing it.",
                JobState.Done => "In flight: Databento has it ready, and the next pass collects it.",
                _ => "In flight, but Databento no longer knows it: the next pass drops it and plans its days again."
            };
        }

        if (days is not null && days.States.Count > 0
            && days.States.Values.All(s => s is DayState.Held or DayState.Empty))
            return "Stored: every day it asked for is in the archive.";

        if (Issues.Count > 0)
            return "No longer in flight. Days it did not store are planned again; the series shows where they stand.";

        return $"No record of this job. A job that stored cleanly leaves only log lines, and issues are kept {HistoryDays} days.";
    }
}
