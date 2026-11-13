using Bunit;
using DbnSuite.Sync.Core;
using DbnSuite.Sync.Worker.Components.Pages;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using static DbnSuite.UnitTests.TestData;

namespace DbnSuite.UnitTests.Worker;

public sealed class CorpusPageTests : DashboardContext
{
    private const long FileBytes = 2_500_000;
    private const string ScanFailure = "the store is down";

    private static readonly DayRange Range = new(Day(10, 27), Day(10, 31));
    private static readonly DayRange October = new(HistoryStart, Day(10, 31));

    private static string Tile(IRenderedComponent<CorpusPage> page, string label) =>
        page.FindAll(".tile").First(t => t.QuerySelector(".label")!.TextContent == label).TextContent;

    private void Everything(bool empty = false) => Stored(
        [.. October.Each().Select(day => new StoredDay(day, empty ? 0 : FileBytes, empty))]);

    private void PayUpTo(decimal perJob, decimal perDay, decimal perMonth) => Options.Spend = new SpendPolicy
    {
        AllowPaidJobs = true,
        MaxPerJob = perJob,
        MaxPerDay = perDay,
        MaxPerMonth = perMonth
    };

    [Fact]
    public void Before_the_first_scan_it_says_one_is_running_and_shows_no_tiles()
    {
        var page = Render<CorpusPage>();

        page.Markup.ShouldContain("First scan running");
        page.FindAll(".tile").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_scanned_corpus_shows_its_totals_and_when_it_was_scanned()
    {
        Everything();
        await ScanAsync();

        var page = Render<CorpusPage>();

        page.Markup.ShouldContain($"Scanned now · {Dataset}");
        Tile(page, "Stored").ShouldContain("155 MB");
        Tile(page, "Stored").ShouldContain("62 day files");
        Tile(page, "Lost").ShouldContain("0");
    }

    [Fact]
    public async Task Every_root_and_schema_links_to_its_series_books_first()
    {
        Everything();
        await ScanAsync();

        var page = Render<CorpusPage>();

        page.FindAll("thead th[scope=col]").Select(h => h.TextContent).ShouldContain($"{Mbo} {Level.L3}");
        page.FindAll("a.cell-link").Select(a => a.GetAttribute("href"))
            .ShouldBe([$"series/{Mbo}/{Root}", $"series/{Definition}/{Root}"]);
    }

    [Fact]
    public async Task A_root_that_a_schema_leaves_out_reads_not_kept()
    {
        Options.Roots = $"{Root},{OtherRoot}";
        Options.RootsBySchema[Mbo] = Root;
        await ScanAsync();

        var page = Render<CorpusPage>();

        page.FindAll("td .muted").Select(s => s.TextContent).ShouldBe(["not kept"]);
    }

    [Fact]
    public async Task A_failed_scan_is_shown_above_the_last_good_one()
    {
        await ScanAsync();
        Store.ListPendingAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException(ScanFailure));
        await ScanAsync();

        var page = Render<CorpusPage>();

        page.Find("[role=alert]").TextContent.ShouldContain(ScanFailure);
        page.FindAll(".tile").ShouldNotBeEmpty();
    }

    [Fact]
    public async Task The_free_policy_shows_no_ledger_and_never_reads_it()
    {
        await ScanAsync();

        var page = Render<CorpusPage>();

        page.Find(".spend").TextContent.ShouldContain(Options.Spend.Describe());
        page.Find(".spend").ClassList.ShouldNotContain("paid");
        page.Find(".spend").TextContent.ShouldNotContain("Quoted so far");
        await Store.DidNotReceive().ListSpendAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_paid_policy_shows_today_and_the_month_from_the_ledger()
    {
        PayUpTo(5m, 10m, 100m);
        Store.ListSpendAsync(Today, Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<SpendEntry>>(
        [
            new("a", Now, Root, Mbo, Range, 1.5m, JobId),
            new("b", Now.AddHours(-20), Root, Mbo, Range, 40m, OtherJobId)
        ]));
        await ScanAsync();

        var page = Render<CorpusPage>();

        page.WaitForAssertion(() => page.Find(".spend").TextContent.ShouldContain("$1.50 today, $41.50 this month"));
        page.Find(".spend").ClassList.ShouldContain("paid");
    }

    [Fact]
    public async Task Pending_jobs_are_listed_with_links_to_their_pages()
    {
        var job = new PendingJob(JobId, Root, Mbo, Range, Now.AddHours(-2));
        Pending(job);
        await ScanAsync();

        var page = Render<CorpusPage>();

        page.Find($"a[href='job/{JobId}']").TextContent.ShouldBe(JobId);
        page.Markup.ShouldContain("2 h ago");
        Tile(page, "Pending jobs").ShouldContain($"{Range.Days} days in flight");
    }

    [Fact]
    public async Task With_nothing_pending_it_says_so()
    {
        await ScanAsync();

        var page = Render<CorpusPage>();

        page.Markup.ShouldContain("None in flight.");
    }

    [Fact]
    public async Task With_nothing_left_the_eta_is_caught_up()
    {
        Everything();
        await ScanAsync();

        var page = Render<CorpusPage>();

        Tile(page, "ETA").ShouldContain("Caught up");
        Tile(page, "ETA").ShouldContain("every free day stored");
    }

    [Fact]
    public async Task With_days_owed_and_none_stored_lately_the_eta_is_unknown()
    {
        await ScanAsync();

        var page = Render<CorpusPage>();

        Tile(page, "ETA").ShouldContain("Unknown");
        Tile(page, "ETA").ShouldContain("none stored in 24 h");
    }

    [Fact]
    public async Task With_days_owed_and_a_recent_rate_the_eta_is_a_time_and_a_rate()
    {
        Passes(Pass(Now.AddHours(-2), TimeSpan.FromMinutes(1)));
        await ScanAsync();

        var page = Render<CorpusPage>();

        page.WaitForAssertion(() => Tile(page, "ETA").ShouldContain("/h"));
        Tile(page, "ETA").ShouldContain(" h");
    }

    [Fact]
    public async Task The_issues_tile_counts_the_last_day_and_links_to_activity()
    {
        var recent = new Issue(Now.AddHours(-1), IssueLevel.Error, Errors.Corrupt, Definition);
        var old = new Issue(Now.AddHours(-30), IssueLevel.Warning, Errors.Unreachable, Definition);
        Passes(Pass(Now.AddHours(-1), issues: [recent]), Pass(Now.AddHours(-30), issues: [old]));
        await ScanAsync();

        var page = Render<CorpusPage>();

        page.WaitForAssertion(() => Tile(page, "Issues, 24 h").ShouldContain("1 errors"));
        page.Find("a.tile-link").GetAttribute("href").ShouldBe("activity");
    }

    [Fact]
    public async Task A_history_that_cannot_be_read_leaves_the_page_standing()
    {
        Store.ListPassesAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException(ScanFailure));
        await ScanAsync();

        var page = Render<CorpusPage>();

        page.FindAll(".tile").ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Rescan_scans_and_rereads_the_history()
    {
        await ScanAsync();
        var page = Render<CorpusPage>();
        Everything();
        Store.ClearReceivedCalls();

        page.Find(".page-head button").Click();

        page.WaitForAssertion(() => page.FindAll(".tile .value")[2].TextContent.ShouldBe("0"));
        await Store.Received().ListPassesAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_button_says_scanning_while_a_scan_runs()
    {
        await ScanAsync();
        var page = Render<CorpusPage>();
        var slow = new TaskCompletionSource<IReadOnlyList<PendingJob>>();
        Store.ListPendingAsync(Arg.Any<CancellationToken>()).Returns(slow.Task);

        var scan = ScanAsync();

        page.WaitForAssertion(() => page.Find(".page-head button").TextContent.ShouldBe("Scanning…"));
        page.Find(".page-head button").HasAttribute("disabled").ShouldBeTrue();
        slow.SetResult([]);
        await scan;
        page.WaitForAssertion(() => page.Find(".page-head button").TextContent.ShouldBe("Rescan"));
    }

    [Fact]
    public async Task A_page_that_has_gone_stops_listening()
    {
        await ScanAsync();
        var page = Render<CorpusPage>();

        page.Instance.Dispose();
        Store.ClearReceivedCalls();
        await ScanAsync();

        await Store.DidNotReceive().ListPassesAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }
}
