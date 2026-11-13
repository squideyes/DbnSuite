using DbnSuite.Sync.Core;
using Shouldly;
using static DbnSuite.UnitTests.TestData;

namespace DbnSuite.UnitTests.Core;

public sealed class CorpusTests
{
    private const long FileBytes = 1_000;
    private static readonly DateOnly HistoryStart = new(2020, 1, 1);
    private static readonly IReadOnlyList<DayRange> NothingPending = [];

    [Fact]
    public void Every_day_in_the_free_window_gets_a_state()
    {
        IReadOnlyList<StoredDay> stored =
        [
            new(Day(10, 1), FileBytes, Empty: false),
            new(Day(10, 2), 0, Empty: true)
        ];
        IReadOnlyList<DayRange> pending = [new DayRange(Day(10, 3), Day(10, 4))];

        var series = Corpus.Build(Root, Spec(Mbo), stored, pending, Today, Day(10, 31), HistoryStart);

        series.Span.ShouldBe(new DayRange(Day(10, 1), Day(10, 31)));
        series.States[Day(10, 1)].ShouldBe(DayState.Held);
        series.States[Day(10, 2)].ShouldBe(DayState.Empty);
        series.States[Day(10, 3)].ShouldBe(DayState.Pending);
        series.States[Day(10, 5)].ShouldBe(DayState.Owed);
        series.TotalBytes.ShouldBe(FileBytes);
    }

    [Fact]
    public void A_day_with_both_a_file_and_an_empty_marker_counts_as_held()
    {
        IReadOnlyList<StoredDay> stored =
        [
            new(Day(10, 1), 0, Empty: true),
            new(Day(10, 1), FileBytes, Empty: false)
        ];

        var series = Corpus.Build(Root, Spec(Mbo), stored, NothingPending, Today, Day(10, 31), HistoryStart);

        series.States[Day(10, 1)].ShouldBe(DayState.Held);
        series.TotalBytes.ShouldBe(FileBytes);
    }

    [Fact]
    public void A_hole_that_aged_out_of_the_window_is_lost()
    {
        IReadOnlyList<StoredDay> stored =
        [
            new(Day(9, 1), FileBytes, Empty: false),
            new(Day(9, 3), FileBytes, Empty: false)
        ];

        var series = Corpus.Build(Root, Spec(Mbo), stored, NothingPending, Today, Day(10, 31), HistoryStart);

        series.Span!.Value.From.ShouldBe(Day(9, 1));
        series.States[Day(9, 2)].ShouldBe(DayState.Lost);
        series.Count(DayState.Lost).ShouldBe(series.FreeFrom.DayNumber - Day(9, 1).DayNumber - 2);
    }

    [Fact]
    public void L0_starts_at_the_history_start_and_never_loses_a_day()
    {
        var historyStart = Day(10, 20);

        var series = Corpus.Build(Root, Spec(Definition), [], NothingPending, Today, Day(10, 31), historyStart);

        series.Span.ShouldBe(new DayRange(historyStart, Day(10, 31)));
        series.Count(DayState.Owed).ShouldBe(12);
        series.Count(DayState.Lost).ShouldBe(0);
    }

    [Fact]
    public void Runs_group_consecutive_days_of_the_asked_states()
    {
        IReadOnlyList<StoredDay> stored = [new(Day(10, 29), FileBytes, Empty: false)];

        var series = Corpus.Build(Root, Spec(Definition), stored, NothingPending, Today, Day(10, 31), Day(10, 27));

        series.Runs(DayState.Owed).ShouldBe([
            (DayState.Owed, new DayRange(Day(10, 27), Day(10, 28))),
            (DayState.Owed, new DayRange(Day(10, 30), Day(10, 31)))
        ]);
    }

    [Fact]
    public void Days_reached_back_to_are_owed_and_older_ones_lost()
    {
        IReadOnlyList<StoredDay> stored = [new(Day(8, 1), FileBytes, Empty: false)];

        var series = Corpus.Build(Root, Spec(Mbo), stored, NothingPending, Today, Day(10, 31), HistoryStart,
            reachBackTo: Day(9, 1));

        series.Span.ShouldBe(new DayRange(Day(8, 1), Day(10, 31)));
        series.States[Day(8, 2)].ShouldBe(DayState.Lost);
        series.States[Day(9, 1)].ShouldBe(DayState.Owed);
        series.States[Day(9, 30)].ShouldBe(DayState.Owed);
    }

    [Fact]
    public void A_series_with_no_days_has_no_runs()
    {
        var series = Corpus.Build(Root, Spec(Mbo), [], [], Today, Day(9, 1), new DateOnly(2020, 1, 1));

        series.Span.ShouldBeNull();
        series.Runs(DayState.Owed).ShouldBeEmpty();
    }

    [Fact]
    public void Runs_leave_out_the_states_not_asked_for_right_to_the_last_day()
    {
        IReadOnlyList<StoredDay> stored = [new(Day(10, 2), 1, Empty: false)];

        var series = Corpus.Build(Root, Spec(Mbo), stored, [], Today, Day(10, 3), new DateOnly(2020, 1, 1));

        series.Runs(DayState.Owed).Select(r => r.Range).ShouldBe(
            [new DayRange(Day(10, 1), Day(10, 1)), new DayRange(Day(10, 3), Day(10, 3))]);
        series.Runs(DayState.Held).ShouldHaveSingleItem().Range.ShouldBe(new DayRange(Day(10, 2), Day(10, 2)));
    }

    [Fact]
    public void A_day_stored_past_the_dataset_s_end_still_shows()
    {
        IReadOnlyList<StoredDay> stored = [new(Day(10, 3), 1, Empty: false)];

        var series = Corpus.Build(Root, Spec(Mbo), stored, [], Today, Day(10, 2), new DateOnly(2020, 1, 1));

        series.Span!.Value.Upto.ShouldBe(Day(10, 3));
    }
}
