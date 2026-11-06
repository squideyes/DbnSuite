using DbnSpool;
using Shouldly;

namespace DbnSync.UnitTests.SpoolTests;

public sealed class SpoolPlanTests
{
    private static readonly DateOnly Monday = new(2026, 10, 26);
    private static readonly DateOnly Tuesday = Monday.AddDays(1);
    private static readonly DateOnly Wednesday = Monday.AddDays(2);
    private static readonly DateOnly Sunday = Monday.AddDays(-1);

    private static HashSet<DateOnly> Days(params DateOnly[] days) => [.. days];

    private static SpoolLedger Holding(DateOnly date, bool ticks)
    {
        var ledger = new SpoolLedger();

        ledger.Add(new SpooledDate(date, 1, ticks));

        return ledger;
    }

    [Fact]
    public void Through_is_the_last_day_every_schema_has_reached()
    {
        SpoolPlan.Through([Days(Monday, Tuesday), Days(Monday)]).ShouldBe(Monday);
    }

    [Fact]
    public void Through_is_nothing_while_a_schema_is_empty()
    {
        SpoolPlan.Through([Days(Monday), Days()]).ShouldBeNull();
    }

    [Fact]
    public void Dates_the_shelf_lacks_are_pending_oldest_first()
    {
        SpoolPlan.Pending([Tuesday, Monday], new SpoolLedger(), Days(), Tuesday).ShouldBe([Monday, Tuesday]);
    }

    [Fact]
    public void A_date_past_what_the_archive_has_reached_waits()
    {
        SpoolPlan.Pending([Monday, Tuesday], new SpoolLedger(), Days(), Monday).ShouldBe([Monday]);
    }

    [Fact]
    public void A_date_on_the_shelf_is_done()
    {
        SpoolPlan.Pending([Monday], Holding(Monday, ticks: true), Days(Sunday, Monday), Wednesday).ShouldBeEmpty();
    }

    [Fact]
    public void A_date_without_ticks_stays_done_while_mbo_is_missing()
    {
        SpoolPlan.Pending([Tuesday], Holding(Tuesday, ticks: false), Days(Tuesday), Wednesday).ShouldBeEmpty();
    }

    [Fact]
    public void A_date_without_ticks_comes_back_once_mbo_covers_it()
    {
        SpoolPlan.Pending([Tuesday], Holding(Tuesday, ticks: false), Days(Monday, Tuesday), Wednesday).ShouldBe([Tuesday]);
    }

    [Fact]
    public void Ticks_need_both_of_a_trade_dates_utc_files()
    {
        SpoolPlan.HasTicks(Monday, Days(Sunday, Monday)).ShouldBeTrue();
        SpoolPlan.HasTicks(Monday, Days(Monday)).ShouldBeFalse();
    }
}
