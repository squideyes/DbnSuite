using Bunit;
using DbnSync.Core;
using DbnSync.Worker.Components.Pages;
using Shouldly;
using static DbnSync.UnitTests.TestData;

namespace DbnSync.UnitTests.Worker;

public sealed class SeriesPageTests : DashboardContext
{
    private const long FileBytes = 1_000;

    private static readonly DayRange October = new(HistoryStart, Day(10, 31));

    private IRenderedComponent<SeriesPage> Page(string schema = Mbo, string root = Root) => Render<SeriesPage>(p => p
        .Add(s => s.Schema, schema)
        .Add(s => s.Root, root));

    [Fact]
    public void Before_the_first_scan_it_says_one_is_running()
    {
        var page = Page();

        page.Markup.ShouldContain("First scan running");
    }

    [Fact]
    public async Task A_series_not_kept_says_so()
    {
        await ScanAsync();

        var page = Page(root: OtherRoot);

        page.Markup.ShouldContain($"{OtherRoot} {Mbo} is not kept.");
    }

    [Fact]
    public async Task The_address_may_spell_the_series_in_any_case()
    {
        await ScanAsync();

        var page = Page(Mbo.ToUpperInvariant(), Root.ToLowerInvariant());

        page.FindAll(".tile").ShouldNotBeEmpty();
    }

    [Fact]
    public async Task The_tiles_count_each_state()
    {
        Stored(new StoredDay(Day(10, 1), FileBytes, Empty: false), new StoredDay(Day(10, 3), 0, Empty: true));
        await ScanAsync();

        var page = Page();

        var tiles = page.FindAll(".tile").ToDictionary(t => t.QuerySelector(".label")!.TextContent,
            t => t.QuerySelector(".value")!.TextContent);
        tiles["Stored"].ShouldBe("1 KB");
        tiles["No data"].ShouldBe("1");
        tiles["Owed"].ShouldBe((October.Days - 2).ToString(System.Globalization.CultureInfo.InvariantCulture));
        tiles["Free from"].ShouldBe("2026-10-01");
    }

    [Fact]
    public async Task Every_gap_is_listed_newest_first_with_its_state()
    {
        Stored([.. October.Each().Where(d => d != Day(10, 10) && d != Day(10, 20) && d != Day(10, 21))
            .Select(d => new StoredDay(d, FileBytes, Empty: false))]);
        await ScanAsync();

        var page = Page();

        var rows = page.FindAll("section tbody tr").Where(r => r.QuerySelector(".chip") is not null).ToList();
        rows.Count.ShouldBe(2);
        rows[0].TextContent.ShouldContain("2026-10-20");
        rows[0].QuerySelector(".num")!.TextContent.ShouldBe("2");
        rows[1].TextContent.ShouldContain("2026-10-10");
    }

    [Fact]
    public async Task A_series_with_no_gaps_says_so()
    {
        Stored([.. October.Each().Select(d => new StoredDay(d, FileBytes, Empty: false))]);
        await ScanAsync();

        var page = Page();

        page.Markup.ShouldContain("None: every day is stored or marked empty.");
    }

    [Fact]
    public async Task A_scan_updates_the_page()
    {
        var page = Page();

        await ScanAsync();

        page.WaitForAssertion(() => page.FindAll(".tile").ShouldNotBeEmpty());
    }

    [Fact]
    public async Task A_page_that_has_gone_stops_listening()
    {
        var page = Page();

        page.Instance.Dispose();
        await ScanAsync();

        page.FindAll(".tile").ShouldBeEmpty();
    }
}
