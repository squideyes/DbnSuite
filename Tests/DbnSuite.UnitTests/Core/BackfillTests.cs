using DbnSuite.Sync.Core;
using Shouldly;
using static DbnSuite.UnitTests.TestData;

namespace DbnSuite.UnitTests.Core;

public sealed class BackfillTests
{
    private const int DaysLeft = 120;

    [Fact]
    public void The_end_is_the_days_left_at_the_recent_rate()
    {
        PassSummary[] passes = [Pass(hoursAgo: 4, files: 30, empty: 10), Pass(hoursAgo: 1, files: 40, empty: 0)];

        var backfill = Backfill.Estimate(DaysLeft, passes, Now);

        backfill.DaysPerHour.ShouldBe(20);
        backfill.DoneAt.ShouldBe(Now.AddHours(6));
    }

    [Fact]
    public void Passes_older_than_the_window_do_not_count()
    {
        PassSummary[] passes = [Pass(hoursAgo: 30, files: 500, empty: 0), Pass(hoursAgo: 2, files: 40, empty: 0)];

        var backfill = Backfill.Estimate(DaysLeft, passes, Now);

        backfill.DaysPerHour.ShouldBe(20);
    }

    [Fact]
    public void Nothing_settled_lately_means_no_end_in_sight()
    {
        PassSummary[] passes = [Pass(hoursAgo: 2, files: 0, empty: 0)];

        var backfill = Backfill.Estimate(DaysLeft, passes, Now);

        backfill.DoneAt.ShouldBeNull();
        backfill.CaughtUp.ShouldBeFalse();
    }

    [Fact]
    public void No_days_left_is_caught_up_now()
    {
        var backfill = Backfill.Estimate(0, [], Now);

        backfill.CaughtUp.ShouldBeTrue();
        backfill.DoneAt.ShouldBe(Now);
    }

    private static PassSummary Pass(int hoursAgo, int files, int empty)
    {
        var started = Now.AddHours(-hoursAgo);

        return new(started, started.AddMinutes(5), 0, 0, 0, 0, files, 0, empty, 0, 0, null, []);
    }
}
