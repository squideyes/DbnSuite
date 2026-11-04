using DbnSync.Core;
using Microsoft.AspNetCore.Components;

namespace DbnSync.Worker.Components.Pages;

public sealed partial class CorpusPage : IDisposable
{
    [Inject] private CorpusIndex Index { get; set; } = null!;

    [Inject] private IArchiveStore Store { get; set; } = null!;

    [Inject] private SyncOptions Options { get; set; } = null!;

    [Inject] private TimeProvider Clock { get; set; } = null!;

    [Inject] private ILogger<CorpusPage> Logger { get; set; } = null!;

    private CorpusSnapshot? Snapshot => Index.Snapshot;

    private IReadOnlyList<PassSummary> RecentPasses { get; set; } = [];

    private IReadOnlyList<Issue> RecentIssues { get; set; } = [];

    /// <summary>What the spend ledger says paid jobs cost today and this month; read only under a paid policy.</summary>
    private (decimal Today, decimal Month) Spent { get; set; }

    private IReadOnlyList<string> Roots => Options.RootList;

    private IReadOnlyList<SchemaSpec> Schemas =>
    [
        .. Options.SchemaList
            .Where(Options.Profile().Schemas.ContainsKey)
            .Select(name => Options.Profile().Schemas[name])
            .OrderByDescending(s => s.Level)
    ];

    protected override async Task OnInitializedAsync()
    {
        Index.Changed += Refresh;

        await LoadIssuesAsync();
    }

    public void Dispose() => Index.Changed -= Refresh;

    private static string Money(decimal amount) =>
        amount.ToString("$#,##0.00", System.Globalization.CultureInfo.InvariantCulture);

    private static Series? Find(CorpusSnapshot corpus, string schema, string root) =>
        corpus.Series.FirstOrDefault(s => s.Schema.Name == schema && s.Root == root);

    private async Task RescanAsync()
    {
        await Index.RefreshAsync(CancellationToken.None);
        await LoadIssuesAsync();
    }

    private async Task LoadIssuesAsync()
    {
        var now = Clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        try
        {
            var passes = await Store.ListPassesAsync(today.AddDays(-1), today, CancellationToken.None);

            RecentPasses = passes;
            RecentIssues = [.. passes.SelectMany(p => p.Issues).Where(i => now - i.At <= TimeSpan.FromDays(1))];

            if (Options.Spend.Paid)
                Spent = SpendLedger.Totals(await Store.ListSpendAsync(today, CancellationToken.None), today);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Pass history unavailable");
        }
    }

    private Backfill Estimate(CorpusSnapshot corpus) => Backfill.Estimate(
        corpus.Count(DayState.Owed) + corpus.Count(DayState.Pending), RecentPasses, Clock.GetUtcNow());

    private string EtaValue(Backfill backfill) => backfill switch
    {
        { CaughtUp: true } => "Caught up",
        { DoneAt: { } done } => Format.Relative(done, Clock.GetUtcNow()),
        _ => "Unknown"
    };

    private static string EtaNote(Backfill backfill) => backfill switch
    {
        { CaughtUp: true } => "every free day stored",
        { DoneAt: null } => $"{Format.Count(backfill.DaysLeft)} days left, none stored in 24 h",
        _ => $"{Format.Count(backfill.DaysLeft)} days left at {Format.Count((int)Math.Round(backfill.DaysPerHour))}/h"
    };

    /// <summary>A rescan follows every pass, so the pass history is read again with it.</summary>
    private void Refresh() => InvokeAsync(async () =>
    {
        await LoadIssuesAsync();
        StateHasChanged();
    });
}
