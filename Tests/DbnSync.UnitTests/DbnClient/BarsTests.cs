using DbnClient.Export;
using DbnClient.Time;
using Shouldly;
using static DbnSync.UnitTests.TestData;

namespace DbnSync.UnitTests.ClientTests;

public sealed class BarsTests
{
    private const string Key = "ESZ26";
    private const string Next = "ESH27";
    private const string Series = "ES.c.0";
    private const ulong Second = 1_000_000_000;

    private static readonly SessionFilter Rth =
        SessionFilter.Create(new DateSpan(Day(10, 28), Day(10, 28)), SessionKind.Rth, "ES").Value;

    /// <summary>08:30 Chicago on 2026-10-28, the RTH open.</summary>
    private static readonly ulong Open = (ulong)((Rth.Anchor(Day(10, 28)) - DateTimeOffset.UnixEpoch).Ticks * 100);

    [Theory]
    [InlineData("5s", BarUnit.Seconds, 5)]
    [InlineData("15m", BarUnit.Seconds, 900)]
    [InlineData("4h", BarUnit.Seconds, 14400)]
    [InlineData("1d", BarUnit.Day, 1)]
    [InlineData("1mo", BarUnit.Month, 1)]
    [InlineData("500t", BarUnit.Ticks, 500)]
    [InlineData("1000v", BarUnit.Volume, 1000)]
    public void Sizes_read_as_their_unit(string text, BarUnit unit, long size)
    {
        var spec = BarSpec.Parse(text).Value;

        spec.Unit.ShouldBe(unit);
        spec.Size.ShouldBe(size);
    }

    [Theory]
    [InlineData("2d")]
    [InlineData("5x")]
    [InlineData("0m")]
    public void Sizes_it_cannot_build_are_refused(string text)
    {
        BarSpec.Parse(text).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void Time_bars_count_from_the_sessions_open_and_aggregate()
    {
        var bars = new List<Bar>();
        var builder = new BarBuilder(BarSpec.Parse("1m").Value, Rth, bars.Add);

        builder.Add(Key, Key, null, Day(10, 28), Open, 10m, 12m, 9m, 11m, 5, 0);
        builder.Add(Key, Key, null, Day(10, 28), Open + 30 * Second, 11m, 15m, 8m, 14m, 7, 0);
        builder.Add(Key, Key, null, Day(10, 28), Open + 60 * Second, 14m, 14m, 13m, 13m, 2, 0);
        builder.Flush();

        bars.Count.ShouldBe(2);
        bars[0].ShouldBe(new Bar(Key, Key, null, Day(10, 28), Open, Open + 60 * Second, 10m, 15m, 8m, 14m, 12, 0));
        bars[1].Start.ShouldBe(Open + 60 * Second);
        bars[1].Volume.ShouldBe(2);
    }

    [Fact]
    public void Tick_bars_close_on_their_count()
    {
        var bars = new List<Bar>();
        var builder = new BarBuilder(BarSpec.Parse("2t").Value, Rth, bars.Add);

        builder.AddTrade(Key, Key, null, Day(10, 28), Open, 10m, 1);
        builder.AddTrade(Key, Key, null, Day(10, 28), Open + Second, 12m, 3);
        builder.AddTrade(Key, Key, null, Day(10, 28), Open + 2 * Second, 11m, 1);
        builder.Flush();

        bars.Count.ShouldBe(2);
        bars[0].Trades.ShouldBe(2);
        bars[0].Close.ShouldBe(12m);
        bars[0].Volume.ShouldBe(4);
        bars[1].Trades.ShouldBe(1);
    }

    [Fact]
    public void A_day_week_or_month_comes_one_at_a_time()
    {
        BarSpec.Parse("2w").Error!.Message.ShouldContain("one at a time");
        BarSpec.Parse("2mo").Error!.Code.ShouldBe("input.bad_bars");
    }

    [Fact]
    public void A_size_is_read_whatever_its_case_and_spacing()
    {
        BarSpec.Parse(" 5M ").Value.Size.ShouldBe(300);
    }

    [Theory]
    [InlineData("500t", true)]
    [InlineData("1000v", true)]
    [InlineData("1m", false)]
    [InlineData("1d", false)]
    public void Only_activity_bars_come_from_trades(string text, bool fromTrades)
    {
        BarSpec.Parse(text).Value.FromTrades.ShouldBe(fromTrades);
    }

    [Fact]
    public void A_time_bar_knows_its_length_in_nanoseconds()
    {
        BarSpec.Parse("1m").Value.Nanos.ShouldBe(60_000_000_000);
    }

    [Theory]
    [InlineData(false, false, "ts_event,ts_end,symbol,open,high,low,close,volume")]
    [InlineData(false, true, "ts_event,ts_end,symbol,open,high,low,close,volume,trades")]
    [InlineData(true, false, "ts_event,ts_end,symbol,open,high,low,close,volume,series")]
    [InlineData(true, true, "ts_event,ts_end,symbol,open,high,low,close,volume,trades,series")]
    public void Columns_add_trades_then_the_series(bool series, bool trades, string columns)
    {
        string.Join(',', BarBuilder.Columns(series, trades).Select(c => c.Name)).ShouldBe(columns);
    }

    [Fact]
    public void A_bar_fills_its_cells_through_the_price_function()
    {
        var bar = new Bar(Series, Key, Series, Day(10, 28), Open, Open + 60 * Second, 10m, 15m, 8m, 14m, 12, 3);
        var cells = new Cell[10];

        BarBuilder.Fill(bar, trades: true, p => p + 1, cells);

        cells[0].Number.ShouldBe((long)Open);
        cells[1].Number.ShouldBe((long)(Open + 60 * Second));
        cells[2].Text.ShouldBe(Key);
        cells[3..7].Select(c => c.Price).ShouldBe([11m, 16m, 9m, 15m]);
        cells[7].Number.ShouldBe(12);
        cells[8].Number.ShouldBe(3);
        cells[9].Text.ShouldBe(Series);
    }

    [Fact]
    public void A_bar_without_trades_puts_the_series_straight_after_the_volume()
    {
        var bar = new Bar(Series, Key, Series, Day(10, 28), Open, Open, 10m, 10m, 10m, 10m, 1, 0);
        var cells = new Cell[9];

        BarBuilder.Fill(bar, trades: false, p => p, cells);

        cells[8].Text.ShouldBe(Series);
    }

    private static List<Bar> Built(string size, SessionFilter sessions, params (DateOnly Date, ulong Time, decimal Price)[] prints)
    {
        var bars = new List<Bar>();
        var builder = new BarBuilder(BarSpec.Parse(size).Value, sessions, bars.Add);

        foreach (var (date, time, price) in prints)
            builder.Add(Key, Key, null, date, time, price, price, price, price, 1, 0);

        builder.Flush();

        return bars;
    }

    private static SessionFilter Eth(DateOnly from, DateOnly to) =>
        SessionFilter.Create(new DateSpan(from, to), SessionKind.Eth, "ES").Value;

    [Fact]
    public void A_time_bar_ends_where_the_next_begins_and_empty_ones_are_not_written()
    {
        var bars = Built("1m", Rth, (Day(10, 28), Open, 10m), (Day(10, 28), Open + 180 * Second, 11m));

        bars.Select(b => b.Start).ShouldBe([Open, Open + 180 * Second]);
        bars.Select(b => b.End).ShouldBe([Open + 60 * Second, Open + 240 * Second]);
    }

    [Fact]
    public void The_last_time_bar_of_a_session_ends_when_the_session_does()
    {
        var rthClose = DbnBytes.Utc(2026, 10, 28, 20);

        var bar = Built("1h", Rth, (Day(10, 28), rthClose - 60 * Second, 10m)).ShouldHaveSingleItem();

        bar.End.ShouldBe(rthClose);
    }

    [Fact]
    public void A_daily_bar_runs_from_the_sessions_open_to_its_close()
    {
        var sessionOpen = DbnBytes.Utc(2026, 10, 27, 22);
        var sessionClose = DbnBytes.Utc(2026, 10, 28, 21);

        var bars = Built("1d", Eth(Day(10, 28), Day(10, 28)),
            (Day(10, 28), Open, 10m),
            (Day(10, 28), Open + 7200 * Second, 12m));

        var bar = bars.ShouldHaveSingleItem();
        bar.Start.ShouldBe(sessionOpen);
        bar.End.ShouldBe(sessionClose);
        bar.Close.ShouldBe(12m);
    }

    [Fact]
    public void A_new_trade_date_starts_a_new_daily_bar()
    {
        var bars = Built("1d", Eth(Day(10, 28), Day(10, 29)), (Day(10, 28), Open, 10m), (Day(10, 29), Open, 11m));

        bars.Select(b => b.TradeDate).ShouldBe([Day(10, 28), Day(10, 29)]);
    }

    [Fact]
    public void A_weekly_bar_runs_monday_to_friday_from_the_first_session_to_the_last()
    {
        var mondayOpen = DbnBytes.Utc(2026, 10, 25, 22);
        var fridayClose = DbnBytes.Utc(2026, 10, 30, 21);

        var bars = Built("1w", Eth(Day(10, 26), Day(11, 2)),
            (Day(10, 26), Open, 10m),
            (Day(10, 30), Open, 13m),
            (Day(11, 2), Open, 20m));

        bars.Count.ShouldBe(2);
        bars[0].Start.ShouldBe(mondayOpen);
        bars[0].End.ShouldBe(fridayClose);
        bars[0].Close.ShouldBe(13m);
        bars[1].TradeDate.ShouldBe(Day(11, 2));
    }

    [Fact]
    public void A_sunday_trade_date_belongs_to_the_week_it_ends()
    {
        var bars = Built("1w", Eth(Day(10, 26), Day(11, 2)), (Day(10, 26), Open, 10m), (Day(11, 1), Open, 11m));

        bars.ShouldHaveSingleItem();
    }

    [Fact]
    public void A_monthly_bar_follows_the_trade_dates_calendar_month()
    {
        var bars = Built("1mo", Eth(Day(10, 1), Day(11, 2)),
            (Day(10, 1), Open, 10m),
            (Day(10, 30), Open, 12m),
            (Day(11, 2), Open, 20m));

        bars.Count.ShouldBe(2);
        bars[0].Close.ShouldBe(12m);
        bars[1].Open.ShouldBe(20m);
    }

    [Fact]
    public void An_activity_bar_never_spans_two_trade_dates()
    {
        var bars = Built("5t", Eth(Day(10, 28), Day(10, 29)), (Day(10, 28), Open, 10m), (Day(10, 29), Open, 11m));

        bars.Select(b => b.Trades).ShouldBe([0, 0]);
        bars.Count.ShouldBe(2);
    }

    [Fact]
    public void An_activity_bar_runs_from_its_first_trade_to_its_last()
    {
        var bars = new List<Bar>();
        var builder = new BarBuilder(BarSpec.Parse("2t").Value, Rth, bars.Add);

        builder.AddTrade(Key, Key, null, Day(10, 28), Open, 10m, 1);
        builder.AddTrade(Key, Key, null, Day(10, 28), Open + 7 * Second, 10m, 1);

        bars.ShouldHaveSingleItem().Start.ShouldBe(Open);
        bars[0].End.ShouldBe(Open + 7 * Second);
    }

    [Fact]
    public void A_volume_bar_closes_on_its_size_without_splitting_the_last_trade()
    {
        var bars = new List<Bar>();
        var builder = new BarBuilder(BarSpec.Parse("5v").Value, Rth, bars.Add);

        builder.AddTrade(Key, Key, null, Day(10, 28), Open, 10m, 3);
        builder.AddTrade(Key, Key, null, Day(10, 28), Open + Second, 11m, 4);
        builder.AddTrade(Key, Key, null, Day(10, 28), Open + 2 * Second, 12m, 1);
        builder.Flush();

        bars.Select(b => b.Volume).ShouldBe([7, 1]);
    }

    [Fact]
    public void A_bar_belongs_to_one_contract_even_when_a_series_rolls_inside_a_week()
    {
        var bars = new List<Bar>();
        var builder = new BarBuilder(BarSpec.Parse("1w").Value, Eth(Day(10, 26), Day(10, 30)), bars.Add);

        builder.Add(Series, Key, Series, Day(10, 28), Open, 10m, 10m, 10m, 10m, 1, 0);
        builder.Add(Series, Next, Series, Day(10, 29), Open, 20m, 20m, 20m, 20m, 1, 0);
        builder.Flush();

        bars.Select(b => b.Symbol).ShouldBe([Key, Next]);
        bars.Select(b => b.TradeDate).ShouldBe([Day(10, 28), Day(10, 29)]);
    }

    [Fact]
    public void Flushing_before_a_date_closes_only_the_bars_of_earlier_dates()
    {
        var bars = new List<Bar>();
        var builder = new BarBuilder(BarSpec.Parse("1m").Value, Rth, bars.Add);
        builder.Add(Key, Key, null, Day(10, 28), Open, 10m, 10m, 10m, 10m, 1, 0);

        builder.Flush(Day(10, 28));
        bars.ShouldBeEmpty();

        builder.Flush(Day(10, 29));
        bars.ShouldHaveSingleItem();
    }

    [Fact]
    public void Flushing_before_a_date_closes_bars_oldest_first()
    {
        var bars = new List<Bar>();
        var builder = new BarBuilder(BarSpec.Parse("1m").Value, Rth, bars.Add);
        builder.Add(Next, Next, null, Day(10, 28), Open + 60 * Second, 10m, 10m, 10m, 10m, 1, 0);
        builder.Add(Key, Key, null, Day(10, 28), Open, 10m, 10m, 10m, 10m, 1, 0);

        builder.Flush(Day(10, 29));

        bars.Select(b => b.Symbol).ShouldBe([Key, Next]);
    }

    [Theory]
    [InlineData("1w")]
    [InlineData("1mo")]
    public void Flushing_leaves_a_longer_bar_open_while_its_period_runs(string size)
    {
        var bars = new List<Bar>();
        var builder = new BarBuilder(BarSpec.Parse(size).Value, Eth(Day(10, 26), Day(10, 27)), bars.Add);
        builder.Add(Key, Key, null, Day(10, 26), Open, 10m, 10m, 10m, 10m, 1, 0);

        builder.Flush(Day(10, 27));
        bars.ShouldBeEmpty();

        builder.Flush(Day(11, 2));
        bars.ShouldHaveSingleItem();
    }

    [Fact]
    public void Each_series_has_its_own_bars()
    {
        var bars = new List<Bar>();
        var builder = new BarBuilder(BarSpec.Parse("1m").Value, Rth, bars.Add);

        builder.Add(Key, Key, null, Day(10, 28), Open, 10m, 10m, 10m, 10m, 1, 0);
        builder.Add("ESH27", "ESH27", null, Day(10, 28), Open, 20m, 20m, 20m, 20m, 1, 0);
        builder.Flush();

        bars.Select(b => b.Symbol).ShouldBe([Key, "ESH27"], ignoreOrder: true);
    }

    [Theory]
    [InlineData("1d", true)]
    [InlineData("1w", true)]
    [InlineData("1mo", true)]
    [InlineData("24h", true)]
    [InlineData("1440m", true)]
    [InlineData("23h", false)]
    [InlineData("12h", false)]
    [InlineData("1m", false)]
    [InlineData("500t", false)]
    [InlineData("1000v", false)]
    public void A_day_or_longer_goes_by_the_bars_size(string text, bool dayOrLonger)
    {
        BarSpec.Parse(text).Value.DayOrLonger.ShouldBe(dayOrLonger);
    }
}
