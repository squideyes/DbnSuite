using Bunit;
using DbnSync.Core;
using DbnSync.Worker.Components.Shared;
using Shouldly;
using static DbnSync.UnitTests.TestData;

namespace DbnSync.UnitTests.Worker;

public sealed class DayGridTests : BunitContext
{
    private const long Bytes = 2_000;
    private const int DayColumns = 31;

    private static readonly DateOnly HistoryStart = new(2020, 1, 1);
    private static readonly IReadOnlyList<DayRange> NothingPending = [];

    private static Series Series(params StoredDay[] stored) =>
        Corpus.Build(Root, Spec(Mbo), stored, NothingPending, Today, Day(10, 31), HistoryStart);

    private IRenderedComponent<DayGrid> Grid(Series series) => Render<DayGrid>(p => p.Add(g => g.Series, series));

    [Fact]
    public void A_series_with_no_span_says_why()
    {
        var series = Corpus.Build(Root, Spec(Mbo), [], NothingPending, Today, Day(9, 1), HistoryStart);

        var grid = Grid(series);

        grid.Markup.ShouldContain("No days yet");
    }

    [Fact]
    public void Months_run_newest_first_one_row_each()
    {
        var grid = Grid(Series(new StoredDay(Day(9, 15), Bytes, Empty: false)));

        grid.FindAll("tbody th").Select(h => h.TextContent).ShouldBe(["2026-10", "2026-09"]);
    }

    [Fact]
    public void A_short_month_leaves_its_last_cells_empty()
    {
        var grid = Grid(Series(new StoredDay(Day(9, 15), Bytes, Empty: false)));

        var september = grid.FindAll("tbody tr")[1];
        september.QuerySelectorAll("td").Length.ShouldBe(DayColumns + 1);
        september.QuerySelectorAll("td")[30].QuerySelector(".cell").ShouldBeNull();
    }

    [Fact]
    public void Days_before_the_series_begins_have_no_cell()
    {
        var grid = Grid(Series(new StoredDay(Day(9, 15), Bytes, Empty: false)));

        grid.FindAll("tbody tr")[1].QuerySelectorAll("td")[13].QuerySelector(".cell").ShouldBeNull();
    }

    [Fact]
    public void A_stored_day_says_its_size_and_other_days_only_their_state()
    {
        var grid = Grid(Series(new StoredDay(Day(10, 1), Bytes, Empty: false), new StoredDay(Day(10, 2), 0, Empty: true)));

        var cells = grid.FindAll("tbody .cell");
        cells[0].GetAttribute("title").ShouldBe("2026-10-01 · Stored · 2 KB");
        cells[0].ClassList.ShouldContain("state-held");
        cells[1].GetAttribute("title").ShouldBe("2026-10-02 · No data");
    }

    [Fact]
    public void Each_month_totals_its_bytes()
    {
        var grid = Grid(Series(new StoredDay(Day(9, 30), Bytes, Empty: false),
            new StoredDay(Day(10, 1), Bytes, Empty: false), new StoredDay(Day(10, 2), Bytes, Empty: false)));

        grid.FindAll("tbody td.num").Select(c => c.TextContent).ShouldBe(["4 KB", "2 KB"]);
    }

    [Fact]
    public void The_header_marks_every_fifth_day()
    {
        var grid = Grid(Series());

        var labels = grid.FindAll("th.dom").Select(h => h.TextContent).Where(t => t.Length > 0);
        labels.ShouldBe(["1", "5", "10", "15", "20", "25", "30"]);
    }
}
