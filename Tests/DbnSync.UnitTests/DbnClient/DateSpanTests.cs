using DbnClient.Time;
using Shouldly;
using static DbnSync.UnitTests.TestData;

namespace DbnSync.UnitTests.ClientTests;

public sealed class DateSpanTests
{
    private const string BadRange = "input.bad_range";

    [Fact]
    public void From_and_to_include_both_ends()
    {
        var span = DateSpan.Resolve(Day(10, 1), Day(10, 31), null, null, Today).Value;

        span.ShouldBe(new DateSpan(Day(10, 1), Day(10, 31)));
        span.Days.ShouldBe(31);
    }

    [Fact]
    public void From_alone_is_that_one_day()
    {
        DateSpan.Resolve(Day(10, 28), null, null, null, Today).Value.ShouldBe(new DateSpan(Day(10, 28), Day(10, 28)));
    }

    [Theory]
    [InlineData("1d", 11, 1)]
    [InlineData("30d", 10, 3)]
    [InlineData("2w", 10, 19)]
    [InlineData("1m", 10, 2)]
    public void Last_ends_today(string last, int month, int day)
    {
        DateSpan.Resolve(null, null, last, null, Today).Value.ShouldBe(new DateSpan(Day(month, day), Today));
    }

    [Fact]
    public void Since_runs_to_today()
    {
        DateSpan.Resolve(null, null, null, Day(9, 1), Today).Value.ShouldBe(new DateSpan(Day(9, 1), Today));
    }

    [Fact]
    public void A_range_that_ends_before_it_starts_is_refused()
    {
        DateSpan.Resolve(Day(10, 31), Day(10, 1), null, null, Today).Error!.Code.ShouldBe(BadRange);
    }

    [Fact]
    public void Two_ways_at_once_is_refused()
    {
        DateSpan.Resolve(Day(10, 1), null, "5d", null, Today).Error!.Code.ShouldBe(BadRange);
    }

    [Theory]
    [InlineData("5")]
    [InlineData("0d")]
    [InlineData("5q")]
    [InlineData("xd")]
    [InlineData("99999999d")]
    [InlineData("100001w")]
    [InlineData("5000y")]
    [InlineData("30000m")]
    public void A_length_it_cannot_read_is_refused(string last)
    {
        DateSpan.Resolve(null, null, last, null, Today).Error!.Code.ShouldBe(BadRange);
    }

    [Fact]
    public void No_way_at_all_is_refused()
    {
        DateSpan.Resolve(null, null, null, null, Today).Error!.Code.ShouldBe(BadRange);
    }

    [Fact]
    public void To_without_from_is_refused()
    {
        DateSpan.Resolve(null, Day(10, 1), "5d", null, Today).Error!.Code.ShouldBe(BadRange);
    }

    [Fact]
    public void A_year_is_counted_back_to_the_day_after()
    {
        DateSpan.Resolve(null, null, "1Y", null, Today).Value.From.ShouldBe(new DateOnly(2025, 11, 2));
    }

    [Fact]
    public void Since_a_day_after_today_is_refused()
    {
        DateSpan.Resolve(null, null, null, Today.AddDays(1), Today).Error!.Code.ShouldBe(BadRange);
    }

    [Fact]
    public void Both_ends_are_inside_a_span()
    {
        var span = new DateSpan(Day(10, 1), Day(10, 31));

        span.Contains(Day(9, 30)).ShouldBeFalse();
        span.Contains(Day(10, 1)).ShouldBeTrue();
        span.Contains(Day(10, 31)).ShouldBeTrue();
        span.Contains(Day(11, 1)).ShouldBeFalse();
    }
}
