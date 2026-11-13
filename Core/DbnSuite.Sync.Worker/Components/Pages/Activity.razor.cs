using System.Globalization;
using DbnSuite.Sync.Core;
using Microsoft.AspNetCore.Components;

namespace DbnSuite.Sync.Worker.Components.Pages;

public sealed partial class Activity : IDisposable
{
    private static readonly int[] RangeChoices = [1, 7, 30];

    [Inject] private IArchiveStore Store { get; set; } = null!;

    [Inject] private SyncStatus Status { get; set; } = null!;

    [Inject] private TimeProvider Clock { get; set; } = null!;

    private int Days { get; set; } = 7;

    private IssueLevel? Level { get; set; }

    private bool Loading { get; set; } = true;

    private string? LoadError { get; set; }

    private IReadOnlyList<PassSummary> Passes { get; set; } = [];

    private IReadOnlyList<Issue> Issues { get; set; } = [];

    /// <summary>The issue whose detail is showing; one at a time keeps the table scannable.</summary>
    private Issue? Open { get; set; }

    private IReadOnlyList<Issue> Shown => Level is { } level ? [.. Issues.Where(i => i.Level == level)] : Issues;

    protected override async Task OnInitializedAsync()
    {
        Status.Changed += OnPass;

        await LoadAsync();
    }

    public void Dispose() => Status.Changed -= OnPass;

    private int Count(IssueLevel level) => Issues.Count(i => i.Level == level);

    private void Toggle(Issue issue) => Open = issue == Open ? null : issue;

    private static string Took(PassSummary pass)
    {
        var took = pass.FinishedAt - pass.StartedAt;

        return took.TotalMinutes >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{took.TotalMinutes:0.#} min")
            : string.Create(CultureInfo.InvariantCulture, $"{took.TotalSeconds:0} s");
    }

    private async Task ShowAsync(int days)
    {
        Days = days;

        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        Loading = true;

        var now = Clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var since = now.AddDays(-Days);

        try
        {
            var passes = await Store.ListPassesAsync(DateOnly.FromDateTime(since.UtcDateTime), today,
                CancellationToken.None);

            Passes = [.. passes.Where(p => p.StartedAt >= since)];
            Issues = [.. Passes.SelectMany(p => p.Issues).OrderByDescending(i => i.At)];
            LoadError = null;
        }
        catch (Exception ex)
        {
            LoadError = ex.Message;
        }

        Loading = false;
    }

    /// <summary>A finished pass may have been recorded, so reload when one ends.</summary>
    private void OnPass()
    {
        if (!Status.Snapshot.Running)
        {
            _ = InvokeAsync(async () =>
            {
                await LoadAsync();
                StateHasChanged();
            });
        }
    }
}
