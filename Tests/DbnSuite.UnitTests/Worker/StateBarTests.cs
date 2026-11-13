using Bunit;
using DbnSuite.Sync.Core;
using DbnSuite.Sync.Worker.Components.Shared;
using Shouldly;
using static DbnSuite.UnitTests.TestData;

namespace DbnSuite.UnitTests.Worker;

public sealed class StateBarTests : BunitContext
{
    private const long FileBytes = 1_000;

    private static readonly DateOnly HistoryStart = new(2020, 1, 1);

    [Fact]
    public void One_segment_per_state_present_sized_by_its_days()
    {
        IReadOnlyList<DayRange> pending = [new(Day(10, 5), Day(10, 6))];
        IReadOnlyList<StoredDay> stored = [new(Day(10, 1), FileBytes, Empty: false), new(Day(10, 2), 0, Empty: true)];
        var series = Corpus.Build(Root, Spec(Mbo), stored, pending, Today, Day(10, 7), HistoryStart);

        var bar = Render<StateBar>(p => p.Add(b => b.Series, series));

        var segments = bar.FindAll(".segment");
        segments.Select(s => s.GetAttribute("title")).ShouldBe(
            ["Stored: 1 days", "No data: 1 days", "Pending: 2 days", "Owed: 3 days"]);
        segments[2].GetAttribute("style").ShouldBe("flex-grow: 2");
    }

    [Fact]
    public void Reads_as_words_for_a_screen_reader()
    {
        IReadOnlyList<StoredDay> stored = [new(Day(10, 1), FileBytes, Empty: false)];
        var series = Corpus.Build(Root, Spec(Mbo), stored, [], Today, Day(10, 2), HistoryStart);

        var bar = Render<StateBar>(p => p.Add(b => b.Series, series));

        bar.Find(".state-bar").GetAttribute("aria-label").ShouldBe("Stored 1, Owed 1");
    }
}
