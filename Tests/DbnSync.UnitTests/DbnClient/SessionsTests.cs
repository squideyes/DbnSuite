using DbnClient.Time;
using Shouldly;
using static DbnSync.UnitTests.TestData;

namespace DbnSync.UnitTests.ClientTests;

public sealed class SessionsTests
{
    private const string Es = "ES";

    /// <summary>A DBN time from a Chicago wall-clock time.</summary>
    private static ulong Chicago(int month, int day, int hour, int minute)
    {
        var zone = Zones.Find(Zones.Default).Value;
        var local = new DateTime(2026, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        var utc = new DateTimeOffset(local, zone.GetUtcOffset(local));

        return (ulong)((utc - DateTimeOffset.UnixEpoch).Ticks * 100);
    }

    [Theory]
    [InlineData(10, 27, 17, 0, 10, 28)]
    [InlineData(10, 28, 15, 59, 10, 28)]
    [InlineData(10, 28, 16, 59, 10, 28)]
    [InlineData(10, 25, 17, 0, 10, 26)]
    public void A_session_belongs_to_the_day_it_closes(int month, int day, int hour, int minute, int tradeMonth, int tradeDay)
    {
        Sessions.TradeDate(Chicago(month, day, hour, minute)).ShouldBe(Day(tradeMonth, tradeDay));
    }

    [Fact]
    public void A_trade_date_needs_the_utc_day_before_it_too()
    {
        Sessions.UtcDays(new DateSpan(Day(10, 28), Day(10, 30))).ShouldBe(new DateSpan(Day(10, 27), Day(10, 30)));
    }

    [Fact]
    public void Rth_keeps_the_regular_hours_only()
    {
        var filter = SessionFilter.Create(new DateSpan(Day(10, 28), Day(10, 28)), SessionKind.Rth, Es).Value;

        filter.Accept(Chicago(10, 28, 8, 29)).ShouldBeNull();
        filter.Accept(Chicago(10, 28, 8, 30)).ShouldBe(Day(10, 28));
        filter.Accept(Chicago(10, 28, 14, 59)).ShouldBe(Day(10, 28));
        filter.Accept(Chicago(10, 28, 15, 0)).ShouldBeNull();
    }

    [Fact]
    public void Eth_keeps_the_whole_session_and_nothing_outside_the_dates()
    {
        var filter = SessionFilter.Create(new DateSpan(Day(10, 28), Day(10, 28)), SessionKind.Eth, Es).Value;

        filter.Accept(Chicago(10, 27, 16, 30)).ShouldBeNull();
        filter.Accept(Chicago(10, 27, 17, 0)).ShouldBe(Day(10, 28));
        filter.Accept(Chicago(10, 28, 16, 59)).ShouldBe(Day(10, 28));
        filter.Accept(Chicago(10, 28, 17, 0)).ShouldBeNull();
    }

    [Fact]
    public void Custom_hours_are_read_in_their_own_zone()
    {
        var newYork = Zones.Find("America/New_York").Value;
        var filter = SessionFilter.Create(new DateSpan(Day(10, 28), Day(10, 28)), SessionKind.Custom, Es,
            (Hours.Parse("09:30-16:00").Value, newYork)).Value;

        filter.Accept(Chicago(10, 28, 8, 30)).ShouldBe(Day(10, 28));
        filter.Accept(Chicago(10, 28, 8, 29)).ShouldBeNull();
    }

    [Fact]
    public void A_root_without_regular_hours_must_be_given_some()
    {
        SessionFilter.Create(new DateSpan(Day(10, 28), Day(10, 28)), SessionKind.Rth, "ZN").IsSuccess.ShouldBeFalse();
    }

    [Theory]
    [InlineData("9:30-16")]
    [InlineData("09:30")]
    [InlineData("09:30-09:30")]
    [InlineData("xx:30-16:00")]
    [InlineData("09:30-16:00-17:00")]
    public void Hours_it_cannot_read_are_refused(string text)
    {
        Hours.Parse(text).Error!.Code.ShouldBe("input.bad_hours");
    }

    [Fact]
    public void Hours_are_read_as_a_start_and_an_end()
    {
        Hours.Parse(" 09:30 - 16:00 ").Value.ShouldBe(new Hours(new TimeOnly(9, 30), new TimeOnly(16, 0)));
    }

    [Theory]
    [InlineData(9, 29, false)]
    [InlineData(9, 30, true)]
    [InlineData(15, 59, true)]
    [InlineData(16, 0, false)]
    public void A_window_holds_its_start_but_not_its_end(int hour, int minute, bool inside)
    {
        new Hours(new TimeOnly(9, 30), new TimeOnly(16, 0)).Contains(new TimeOnly(hour, minute)).ShouldBe(inside);
    }

    [Theory]
    [InlineData(19, 59, false)]
    [InlineData(20, 0, true)]
    [InlineData(23, 59, true)]
    [InlineData(1, 59, true)]
    [InlineData(2, 0, false)]
    public void A_window_that_ends_before_it_starts_runs_past_midnight(int hour, int minute, bool inside)
    {
        new Hours(new TimeOnly(20, 0), new TimeOnly(2, 0)).Contains(new TimeOnly(hour, minute)).ShouldBe(inside);
    }

    [Theory]
    [InlineData("ES", 8, 30, 15, 0)]
    [InlineData("mes", 8, 30, 15, 0)]
    [InlineData("NQ", 8, 30, 15, 0)]
    [InlineData("MNQ", 8, 30, 15, 0)]
    [InlineData("YM", 8, 30, 15, 0)]
    [InlineData("MYM", 8, 30, 15, 0)]
    [InlineData("RTY", 8, 30, 15, 0)]
    [InlineData("M2K", 8, 30, 15, 0)]
    [InlineData("CL", 8, 0, 13, 30)]
    [InlineData("MCL", 8, 0, 13, 30)]
    [InlineData("GC", 7, 20, 12, 30)]
    [InlineData("MGC", 7, 20, 12, 30)]
    public void Each_root_has_its_own_regular_hours(string root, int startHour, int startMinute, int endHour, int endMinute)
    {
        Sessions.RegularHours(root).ShouldBe(new Hours(new TimeOnly(startHour, startMinute), new TimeOnly(endHour, endMinute)));
    }

    [Fact]
    public void A_root_without_regular_hours_has_none()
    {
        Sessions.RegularHours("ZN").ShouldBeNull();
    }

    [Fact]
    public void The_error_for_a_root_without_regular_hours_names_it()
    {
        var error = SessionFilter.Create(new DateSpan(Day(10, 28), Day(10, 28)), SessionKind.Rth, "ZN").Error!;

        error.Code.ShouldBe("input.no_rth");
        error.Message.ShouldContain("ZN");
    }

    [Fact]
    public void Custom_hours_must_be_given()
    {
        SessionFilter.Create(new DateSpan(Day(10, 28), Day(10, 28)), SessionKind.Custom, Es).Error!.Code.ShouldBe("input.no_hours");
    }

    [Fact]
    public void Only_the_dataset_with_a_known_trading_day_is_supported()
    {
        Sessions.Unsupported(Sessions.Dataset).ShouldBeNull();
        Sessions.Unsupported("XNAS.ITCH")!.Code.ShouldBe(DbnClient.Errors.UnsupportedDataset);
    }

    [Fact]
    public void Mondays_session_opens_on_sunday()
    {
        var sunday5pm = new DateTimeOffset(2026, 10, 25, 22, 0, 0, TimeSpan.Zero);

        Sessions.SessionStart(Day(10, 26)).ShouldBe(sunday5pm);
    }

    [Fact]
    public void Local_time_is_chicagos_wall_clock()
    {
        Sessions.Local(DbnBytes.Utc(2026, 10, 28, 13, 30)).ShouldBe(new DateTime(2026, 10, 28, 8, 30, 0));
    }

    [Fact]
    public void A_session_closes_at_four_in_chicago()
    {
        var fourPmDaylight = new DateTimeOffset(2026, 10, 28, 21, 0, 0, TimeSpan.Zero);

        Sessions.SessionEnd(Day(10, 28)).ShouldBe(fourPmDaylight);
    }

    // Clocks in Chicago: forward on 2026-03-08 at 02:00 (UTC-6 to UTC-5), back on 2026-11-01 at 02:00 (UTC-5 to UTC-6).

    [Theory]
    [InlineData(3, 7, 3, 6, 23)]
    [InlineData(3, 8, 3, 7, 23)]
    [InlineData(3, 9, 3, 8, 22)]
    [InlineData(3, 10, 3, 9, 22)]
    [InlineData(10, 31, 10, 30, 22)]
    [InlineData(11, 1, 10, 31, 22)]
    [InlineData(11, 2, 11, 1, 23)]
    [InlineData(11, 3, 11, 2, 23)]
    public void A_session_opens_at_five_in_chicago_across_the_clock_changes(int month, int day, int openMonth, int openDay, int utcHour)
    {
        Sessions.SessionStart(Day(month, day)).ShouldBe(new DateTimeOffset(2026, openMonth, openDay, utcHour, 0, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(3, 6, 22)]
    [InlineData(3, 9, 21)]
    [InlineData(10, 30, 21)]
    [InlineData(11, 2, 22)]
    public void A_session_closes_at_four_in_chicago_across_the_clock_changes(int month, int day, int utcHour)
    {
        Sessions.SessionEnd(Day(month, day)).ShouldBe(new DateTimeOffset(2026, month, day, utcHour, 0, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(3, 8, 21, 59, 3, 8)]
    [InlineData(3, 8, 22, 0, 3, 9)]
    [InlineData(11, 1, 22, 59, 11, 1)]
    [InlineData(11, 1, 23, 0, 11, 2)]
    public void A_trade_date_changes_at_five_in_chicago_on_the_days_clocks_change(int month, int day, int hour, int minute, int tradeMonth, int tradeDay)
    {
        Sessions.TradeDate(DbnBytes.Utc(2026, month, day, hour, minute)).ShouldBe(Day(tradeMonth, tradeDay));
    }

    [Theory]
    [InlineData(3, 6, 14)]
    [InlineData(3, 9, 13)]
    [InlineData(10, 30, 13)]
    [InlineData(11, 2, 14)]
    public void Rth_opens_at_half_past_eight_in_chicago_either_side_of_a_clock_change(int month, int day, int utcHour)
    {
        var filter = SessionFilter.Create(new DateSpan(Day(month, day), Day(month, day)), SessionKind.Rth, Es).Value;

        filter.Accept(DbnBytes.Utc(2026, month, day, utcHour, 29)).ShouldBeNull();
        filter.Accept(DbnBytes.Utc(2026, month, day, utcHour, 30)).ShouldBe(Day(month, day));
    }

    [Theory]
    [InlineData(3, 8, 23)]
    [InlineData(11, 1, 25)]
    public void The_session_over_a_clock_change_is_an_hour_shorter_or_longer(int month, int day, int hours)
    {
        var length = Sessions.SessionStart(Day(month, day).AddDays(1)) - Sessions.SessionStart(Day(month, day));

        length.ShouldBe(TimeSpan.FromHours(hours));
    }

    [Fact]
    public void A_filter_read_in_time_order_follows_the_clock_change()
    {
        var filter = SessionFilter.Create(new DateSpan(Day(3, 9), Day(3, 10)), SessionKind.Eth, Es).Value;

        filter.Accept(DbnBytes.Utc(2026, 3, 8, 21, 59)).ShouldBeNull();
        filter.Accept(DbnBytes.Utc(2026, 3, 8, 22)).ShouldBe(Day(3, 9));
        filter.Accept(DbnBytes.Utc(2026, 3, 9, 21, 59)).ShouldBe(Day(3, 9));
        filter.Accept(DbnBytes.Utc(2026, 3, 9, 22)).ShouldBe(Day(3, 10));
        filter.Accept(DbnBytes.Utc(2026, 3, 10, 22)).ShouldBeNull();
    }

    [Fact]
    public void A_filter_can_go_back_in_time()
    {
        var filter = SessionFilter.Create(new DateSpan(Day(10, 28), Day(10, 28)), SessionKind.Eth, Es).Value;
        var inside = Chicago(10, 28, 10, 0);

        filter.Accept(Chicago(10, 29, 10, 0)).ShouldBeNull();

        filter.Accept(inside).ShouldBe(Day(10, 28));
    }

    [Fact]
    public void Custom_hours_keep_their_wall_clock_across_a_clock_change()
    {
        var newYork = Zones.Find("America/New_York").Value;
        var filter = SessionFilter.Create(new DateSpan(Day(3, 6), Day(3, 9)), SessionKind.Custom, Es,
            (Hours.Parse("09:30-16:00").Value, newYork)).Value;

        filter.Accept(DbnBytes.Utc(2026, 3, 6, 14, 30)).ShouldBe(Day(3, 6));
        filter.Accept(DbnBytes.Utc(2026, 3, 9, 13, 30)).ShouldBe(Day(3, 9));
    }

    [Fact]
    public void A_window_past_midnight_opens_the_evening_before_and_closes_on_the_trade_date()
    {
        var chicago = Zones.Find(Zones.Default).Value;
        var filter = SessionFilter.Create(new DateSpan(Day(10, 28), Day(10, 28)), SessionKind.Custom, Es,
            (Hours.Parse("20:00-02:00").Value, chicago)).Value;

        filter.Accept(Chicago(10, 27, 19, 59)).ShouldBeNull();
        filter.Accept(Chicago(10, 27, 20, 0)).ShouldBe(Day(10, 28));
        filter.Accept(Chicago(10, 28, 1, 59)).ShouldBe(Day(10, 28));
        filter.Accept(Chicago(10, 28, 2, 0)).ShouldBeNull();
    }

    [Fact]
    public void Hours_in_the_evening_belong_to_the_trade_date_they_open()
    {
        var chicago = Zones.Find(Zones.Default).Value;
        var filter = SessionFilter.Create(new DateSpan(Day(10, 28), Day(10, 28)), SessionKind.Custom, Es,
            (Hours.Parse("18:00-23:00").Value, chicago)).Value;

        filter.Accept(Chicago(10, 27, 17, 59)).ShouldBeNull();
        filter.Accept(Chicago(10, 27, 18, 0)).ShouldBe(Day(10, 28));
        filter.Accept(Chicago(10, 27, 22, 59)).ShouldBe(Day(10, 28));
        filter.Accept(Chicago(10, 27, 23, 0)).ShouldBeNull();
        filter.Accept(Chicago(10, 28, 18, 0)).ShouldBeNull();
    }

    [Fact]
    public void Evening_hours_open_and_close_the_evening_before_the_trade_date()
    {
        var chicago = Zones.Find(Zones.Default).Value;
        var filter = SessionFilter.Create(new DateSpan(Day(10, 28), Day(10, 28)), SessionKind.Custom, Es,
            (Hours.Parse("17:00-20:00").Value, chicago)).Value;

        filter.Anchor(Day(10, 28)).ShouldBe(Sessions.SessionStart(Day(10, 28)));
        (filter.Close(Day(10, 28)) - filter.Anchor(Day(10, 28))).ShouldBe(TimeSpan.FromHours(3));
    }

    [Fact]
    public void Daytime_hours_in_a_zone_a_day_ahead_fall_in_the_right_session()
    {
        // 09:00 on the 29th in Auckland is 15:00 on the 28th in Chicago, inside the session of the 28th.
        var auckland = Zones.Find("Pacific/Auckland").Value;
        var filter = SessionFilter.Create(new DateSpan(Day(10, 28), Day(10, 28)), SessionKind.Custom, Es,
            (Hours.Parse("09:00-10:00").Value, auckland)).Value;

        filter.Accept(Chicago(10, 28, 15, 30)).ShouldBe(Day(10, 28));
        filter.Accept(Chicago(10, 28, 14, 59)).ShouldBeNull();
    }

    [Fact]
    public void Eth_bars_count_from_the_session_and_end_at_its_close()
    {
        var filter = SessionFilter.Create(new DateSpan(Day(10, 28), Day(10, 28)), SessionKind.Eth, Es).Value;

        filter.Anchor(Day(10, 28)).ShouldBe(Sessions.SessionStart(Day(10, 28)));
        filter.Close(Day(10, 28)).ShouldBe(Sessions.SessionEnd(Day(10, 28)));
    }

    [Fact]
    public void A_window_opening_in_the_hour_the_clocks_skip_opens_when_the_clock_reaches_it()
    {
        // 02:30 does not exist on 2026-03-08: at 02:00 CST the clock read 03:00 CDT, so an hour later than 02:30 CST is 03:30 CDT.
        var chicago = Zones.Find(Zones.Default).Value;
        var filter = SessionFilter.Create(new DateSpan(Day(3, 8), Day(3, 8)), SessionKind.Custom, Es,
            (Hours.Parse("02:30-04:00").Value, chicago)).Value;

        filter.Anchor(Day(3, 8)).ShouldBe(new DateTimeOffset(2026, 3, 8, 8, 30, 0, TimeSpan.Zero));
    }

    [Fact]
    public void A_window_opening_in_the_hour_the_clocks_repeat_opens_the_second_time_round()
    {
        // 01:30 happens twice on 2026-11-01; .NET reads an ambiguous time as standard time.
        var chicago = Zones.Find(Zones.Default).Value;
        var filter = SessionFilter.Create(new DateSpan(Day(11, 1), Day(11, 1)), SessionKind.Custom, Es,
            (Hours.Parse("01:30-04:00").Value, chicago)).Value;

        filter.Anchor(Day(11, 1)).ShouldBe(new DateTimeOffset(2026, 11, 1, 7, 30, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(10, 30, 10, 30)]
    [InlineData(10, 31, 11, 2)]
    [InlineData(11, 1, 11, 2)]
    [InlineData(11, 2, 11, 2)]
    public void A_weekend_has_no_trade_date_of_its_own_so_the_first_is_monday(int month, int day, int firstMonth, int firstDay)
    {
        Sessions.FirstTradeDate(Day(month, day)).ShouldBe(Day(firstMonth, firstDay));
    }
}
