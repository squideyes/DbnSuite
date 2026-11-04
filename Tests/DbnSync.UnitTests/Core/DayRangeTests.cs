using DbnSync.Core;
using Shouldly;
using static DbnSync.UnitTests.TestData;

namespace DbnSync.UnitTests.Core;

public sealed class DayRangeTests
{
    [Fact]
    public void Days_counts_both_ends()
    {
        var range = new DayRange(Day(10, 1), Day(10, 5));

        var days = range.Each().ToList();

        range.Days.ShouldBe(5);
        days.ShouldBe([Day(10, 1), Day(10, 2), Day(10, 3), Day(10, 4), Day(10, 5)]);
    }

    [Fact]
    public void An_end_before_the_start_is_refused()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new DayRange(Day(10, 5), Day(10, 1)));
    }

    [Fact]
    public void WithoutFirstDay_sheds_the_oldest_day_and_then_nothing()
    {
        var range = new DayRange(Day(10, 1), Day(10, 2));

        var shorter = range.WithoutFirstDay();

        shorter.ShouldBe(new DayRange(Day(10, 2), Day(10, 2)));
        shorter!.Value.WithoutFirstDay().ShouldBeNull();
    }
}
