using DbnSync.Core;
using Shouldly;
using static DbnSync.UnitTests.TestData;

namespace DbnSync.UnitTests.Core;

public sealed class PlannerTests
{
    private static readonly IReadOnlyList<DayRange> NothingPending = [];

    [Fact]
    public void Missing_skips_held_and_pending_days()
    {
        var window = new DayRange(Day(10, 1), Day(10, 10));
        var held = new HashSet<DateOnly> { Day(10, 3), Day(10, 4) };
        IReadOnlyList<DayRange> pending = [new DayRange(Day(10, 7), Day(10, 8))];

        var missing = Planner.Missing(window, held, pending);

        missing.ShouldBe([
            new DayRange(Day(10, 1), Day(10, 2)),
            new DayRange(Day(10, 5), Day(10, 6)),
            new DayRange(Day(10, 9), Day(10, 10))
        ]);
    }

    [Fact]
    public void A_hole_inside_held_days_is_planned_again()
    {
        var window = new DayRange(Day(10, 1), Day(10, 3));
        var held = new HashSet<DateOnly> { Day(10, 1), Day(10, 3) };

        var missing = Planner.Missing(window, held, NothingPending);

        missing.ShouldBe([new DayRange(Day(10, 2), Day(10, 2))]);
    }

    [Fact]
    public void Chunk_splits_a_run_with_a_short_tail()
    {
        var run = new DayRange(Day(10, 1), Day(10, 12));

        var chunks = Planner.Chunk(run, 5).ToList();

        chunks.ShouldBe([
            new DayRange(Day(10, 1), Day(10, 5)),
            new DayRange(Day(10, 6), Day(10, 10)),
            new DayRange(Day(10, 11), Day(10, 12))
        ]);
    }

    [Fact]
    public void Plan_puts_the_book_first_then_oldest_days()
    {
        var definition = new Coverage(Root, Spec(Definition),
            new DayRange(Day(1, 1), Day(1, 2)), new HashSet<DateOnly>(), NothingPending);
        var trades = new Coverage(Root, Spec(Trades),
            new DayRange(Day(10, 1), Day(10, 2)), new HashSet<DateOnly>(), NothingPending);
        var mbo = new Coverage(OtherRoot, Spec(Mbo),
            new DayRange(Day(10, 20), Day(10, 21)), new HashSet<DateOnly>(), NothingPending);

        var plan = Planner.Plan([definition, trades, mbo]);

        plan.Select(j => j.Schema.Name).ShouldBe([Mbo, Trades, Definition]);
    }

    [Fact]
    public void Plan_owes_nothing_when_everything_is_held()
    {
        var window = new DayRange(Day(10, 1), Day(10, 2));
        var held = new HashSet<DateOnly>(window.Each());

        var plan = Planner.Plan([new Coverage(Root, Spec(Mbo), window, held, NothingPending)]);

        plan.ShouldBeEmpty();
    }

    [Fact]
    public void Days_before_the_plan_come_after_every_included_day_newest_first()
    {
        var none = new HashSet<DateOnly>();

        var plan = Planner.Plan(
        [
            new Coverage(Root, Spec(Mbo), new DayRange(Day(9, 1), Day(9, 10)), none, NothingPending, BeyondPlan: true),
            new Coverage(Root, Spec(Definition), new DayRange(Day(10, 1), Day(10, 2)), none, NothingPending),
            new Coverage(Root, Spec(Mbo), new DayRange(Day(10, 1), Day(10, 5)), none, NothingPending)
        ]);

        plan.Select(j => (j.Schema.Name, j.Range.From, j.BeyondPlan)).ShouldBe([
            (Mbo, Day(10, 1), false),
            (Definition, Day(10, 1), false),
            (Mbo, Day(9, 6), true),
            (Mbo, Day(9, 1), true)
        ]);
    }
}
