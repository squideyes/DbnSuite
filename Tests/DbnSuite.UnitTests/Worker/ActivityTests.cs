using Bunit;
using DbnSuite.Sync.Core;
using DbnSuite.Sync.Worker.Components.Pages;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using static DbnSuite.UnitTests.TestData;

namespace DbnSuite.UnitTests.Worker;

public sealed class ActivityTests : DashboardContext
{
    private const string StoreDown = "the store is down";
    private const string Message = "the message";
    private const string ErrorsFilter = "Errors 1";
    private const string WarningsFilter = "Warnings 1";
    private const string AllFilter = "All 2";

    private static readonly DayRange Range = new(Day(10, 27), Day(10, 31));

    private static readonly Issue Failure = new(Now.AddHours(-1), IssueLevel.Error, Errors.Corrupt, Message, JobId, Mbo, Root, Range);
    private static readonly Issue Notice = new(Now.AddHours(-2), IssueLevel.Warning, Errors.OverLimit, Message);

    private static void Click(IRenderedComponent<Activity> page, string text) =>
        page.FindAll("button").First(b => b.TextContent.Trim() == text).Click();

    private IRenderedComponent<Activity> Page()
    {
        var page = Render<Activity>();

        page.WaitForAssertion(() => page.Markup.ShouldNotContain("Loading"));

        return page;
    }

    [Fact]
    public void An_empty_history_says_so()
    {
        var page = Page();

        page.Markup.ShouldContain("No issues in this range.");
        page.Markup.ShouldContain("No recorded passes in this range.");
    }

    [Fact]
    public void A_history_still_loading_says_so()
    {
        var never = new TaskCompletionSource<IReadOnlyList<PassSummary>>();
        Store.ListPassesAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).Returns(never.Task);

        var page = Render<Activity>();

        page.Markup.ShouldContain("Loading");
    }

    [Fact]
    public void Issues_read_newest_first_with_their_level_and_title()
    {
        Passes(Pass(Now.AddHours(-3), issues: [Notice, Failure]));

        var page = Page();

        var rows = page.FindAll("tr.issue-row");
        rows.Count.ShouldBe(2);
        rows[0].TextContent.ShouldContain(IssueGuide.For(Errors.Corrupt).Title);
        rows[0].QuerySelector(".chip")!.ClassList.ShouldContain("critical");
        rows[1].QuerySelector(".chip")!.ClassList.ShouldContain("warning");
    }

    [Fact]
    public void An_issue_links_its_series_and_its_job_when_it_has_them()
    {
        Passes(Pass(Now.AddHours(-3), issues: [Failure, Notice]));

        var page = Page();

        var rows = page.FindAll("tr.issue-row");
        rows[0].QuerySelectorAll("a").Select(a => a.GetAttribute("href")).ShouldBe([$"series/{Mbo}/{Root}", $"job/{JobId}"]);
        rows[1].QuerySelectorAll("a").ShouldBeEmpty();
    }

    [Fact]
    public void The_level_filter_narrows_the_table_and_all_brings_it_back()
    {
        Passes(Pass(Now.AddHours(-3), issues: [Failure, Notice]));
        var page = Page();

        Click(page, ErrorsFilter);
        var errors = page.FindAll("tr.issue-row").Count;
        Click(page, WarningsFilter);
        var warning = page.Find("tr.issue-row").TextContent;
        Click(page, AllFilter);

        errors.ShouldBe(1);
        warning.ShouldContain(Errors.OverLimit);
        page.FindAll("tr.issue-row").Count.ShouldBe(2);
    }

    [Fact]
    public void Clicking_an_issue_opens_its_detail_and_clicking_again_closes_it()
    {
        Passes(Pass(Now.AddHours(-3), issues: [Failure]));
        var page = Page();

        page.Find("tr.issue-row").Click();
        var opened = page.FindAll(".issue-detail").Count;
        var label = page.Find("button.toggle").GetAttribute("aria-label");
        page.Find("button.toggle").Click();

        opened.ShouldBe(1);
        label.ShouldBe("Hide detail");
        page.FindAll(".issue-detail").ShouldBeEmpty();
    }

    [Fact]
    public void One_detail_shows_at_a_time()
    {
        Passes(Pass(Now.AddHours(-3), issues: [Failure, Notice]));
        var page = Page();

        page.FindAll("tr.issue-row")[0].Click();
        page.FindAll("tr.issue-row")[1].Click();

        page.FindAll(".issue-detail").Count.ShouldBe(1);
        page.Find(".issue-detail").TextContent.ShouldContain(IssueGuide.For(Errors.OverLimit).Meaning);
    }

    [Fact]
    public void A_range_button_reads_that_much_history_and_marks_itself()
    {
        var page = Page();

        Click(page, "30 days");

        page.WaitForAssertion(() => page.FindAll("button.on").Select(b => b.TextContent).ShouldContain("30 days"));
        Store.Received().ListPassesAsync(Today.AddDays(-30), Today, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Passes_older_than_the_range_stay_out_though_their_day_was_listed()
    {
        Passes(Pass(Now.AddDays(-1).AddHours(-1), issues: [Failure]), Pass(Now.AddHours(-1), issues: [Notice]));
        var page = Page();

        Click(page, "24 h");

        page.WaitForAssertion(() => page.FindAll("tr.issue-row").Count.ShouldBe(1));
        page.Find("tr.issue-row").TextContent.ShouldContain(Errors.OverLimit);
    }

    [Theory]
    [InlineData(42, "42 s")]
    [InlineData(150, "2.5 min")]
    public void A_pass_shows_how_long_it_took(int seconds, string shown)
    {
        Passes(Pass(Now.AddHours(-1), TimeSpan.FromSeconds(seconds)));

        var page = Page();

        page.FindAll("td.num")[0].TextContent.ShouldBe(shown);
    }

    [Fact]
    public void A_pass_with_errors_is_marked()
    {
        Passes(Pass(Now.AddHours(-1), errors: 1, issues: [Failure]), Pass(Now.AddHours(-2)));

        var page = Page();

        page.FindAll("td.error-text").Count.ShouldBe(1);
    }

    [Fact]
    public void A_store_that_cannot_be_read_says_so_and_clears_when_it_can()
    {
        Store.ListPassesAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException(StoreDown));
        var page = Page();

        var alert = page.Find("[role=alert]").TextContent;
        Passes();
        Click(page, "24 h");

        alert.ShouldContain(StoreDown);
        page.WaitForAssertion(() => page.FindAll("[role=alert]").ShouldBeEmpty());
    }

    [Fact]
    public void A_finished_pass_reloads_the_history()
    {
        var page = Page();

        Passes(Pass(Now.AddMinutes(-1), issues: [Failure]));
        Status.Finished(null, Now.AddMinutes(5));

        page.WaitForAssertion(() => page.FindAll("tr.issue-row").Count.ShouldBe(1));
    }

    [Fact]
    public void A_pass_that_has_only_started_reloads_nothing()
    {
        Page();

        Store.ClearReceivedCalls();
        Status.Started();

        Store.DidNotReceive().ListPassesAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void A_page_that_has_gone_stops_listening()
    {
        var page = Page();

        page.Instance.Dispose();
        Store.ClearReceivedCalls();
        Status.Finished(null, Now);

        Store.DidNotReceive().ListPassesAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }
}
