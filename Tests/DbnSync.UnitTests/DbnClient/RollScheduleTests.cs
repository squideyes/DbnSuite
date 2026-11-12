using DbnClient;
using DbnClient.Archive;
using DbnClient.Contracts;
using DbnClient.Dbn;
using DbnClient.Time;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using static DbnSync.UnitTests.TestData;

namespace DbnSync.UnitTests.ClientTests;

/// <summary>
/// Rolls worked out by hand from the calendar of December 2026: ESZ26 expires on
/// Friday the 18th, so its calendar roll is Thursday the 10th, and the session of
/// a trade date in December opens at 23:00 UTC the day before (17:00 Chicago).
/// </summary>
public sealed class RollScheduleTests : IDisposable
{
    private const string Es = "ES";
    private const string Ohlcv = "ohlcv-1s";
    private const string Statistics = "statistics";
    private const string Calendar = "ES.c.0";
    private const string SecondCalendar = "ES.c.1";
    private const string ByVolume = "ES.v.0";
    private const string SecondByVolume = "ES.v.1";
    private const string ByInterest = "ES.n.0";
    private const string SecondByInterest = "ES.n.1";
    private const string TenthByInterest = "ES.n.9";
    private const string Front = "ESZ26";
    private const string Next = "ESH27";
    private const string Third = "ESM27";
    private const string RawFront = "ESZ6";
    private const string RawNext = "ESH7";
    private const string RawThird = "ESM7";
    private const string RawSpread = "ESZ6-ESH7";
    private const uint FrontId = 1;
    private const uint NextId = 2;
    private const uint ThirdId = 3;
    private const uint UndatedId = 8;
    private const uint SpreadId = 9;
    private const uint StrangerId = 77;
    private const ushort SettlementPrice = 3;
    private const ushort ClearedVolume = 6;
    private const ushort OpenInterest = 9;
    private const long Nano = 1_000_000_000;
    private const long Tick = 250_000_000;

    private static readonly ulong Listed = DbnBytes.Utc(2025, 1, 1, 0);
    private static readonly ulong FrontExpires = DbnBytes.Utc(2026, 12, 18, 14, 30);
    private static readonly ulong NextExpires = DbnBytes.Utc(2027, 3, 19, 13, 30);
    private static readonly ulong ThirdExpires = DbnBytes.Utc(2027, 6, 18, 13, 30);
    private static readonly DateOnly Listing = Dec(1);

    private readonly ArchiveFolder folder = new();

    public RollScheduleTests() => List(Futures());

    public void Dispose() => folder.Dispose();

    private static DateOnly Dec(int day) => new(2026, 12, day);

    private static byte[][] Futures() =>
    [
        Future(FrontId, RawFront, FrontExpires, 2026, 12),
        Future(NextId, RawNext, NextExpires, 2027, 3),
        Future(ThirdId, RawThird, ThirdExpires, 2027, 6),
        Future(UndatedId, "ESU7", DbnTime.Undefined, 2027, 9),
        DbnBytes.Definition(SpreadId, DbnBytes.Start, RawSpread, Es, DefinitionRecord.Spread, Tick, Listed, FrontExpires, 2026, 12)
    ];

    private static byte[] Future(uint id, string raw, ulong expires, ushort year, byte month, ulong? listed = null) =>
        DbnBytes.Definition(id, DbnBytes.Start, raw, Es, DefinitionRecord.Future, Tick, listed ?? Listed, expires, year, month);

    private void List(params byte[][] definitions) => folder.Add(Definition, Es, Listing, definitions);

    /// <summary>Noon in Chicago, well inside the trade date of that day.</summary>
    private static ulong Noon(DateOnly day) => DbnBytes.Utc(day.Year, day.Month, day.Day, 18);

    /// <summary>16:30 in Chicago: after the session closed, before the next opens at 17:00.</summary>
    private static ulong AfterClose(DateOnly day) => DbnBytes.Utc(day.Year, day.Month, day.Day, 22, 30);

    private static byte[] Bar(uint id, ulong time, ulong volume) =>
        DbnBytes.Ohlcv(id, time, 100 * Nano, 100 * Nano, 100 * Nano, 100 * Nano, volume);

    private void Volumes(DateOnly day, params (uint Id, ulong Volume)[] volumes) =>
        folder.Add(Ohlcv, Es, day, [.. volumes.Select(v => Bar(v.Id, Noon(day), v.Volume))]);

    private void Interest(DateOnly day, params (uint Id, long Quantity)[] interest) =>
        folder.Add(Statistics, Es, day, [.. interest.Select(i => Stat(i.Id, AfterClose(day), OpenInterest, quantity: i.Quantity))]);

    private void Settlements(DateOnly day, params (uint Id, long Price)[] settlements) =>
        folder.Add(Statistics, Es, day, [.. settlements.Select(s => Stat(s.Id, AfterClose(day), SettlementPrice, price: s.Price))]);

    private static byte[] Stat(uint id, ulong time, ushort type, long price = DbnPrice.Undefined, long quantity = StatRecord.UndefinedQuantity) =>
        DbnBytes.Statistic(id, time, DbnTime.Undefined, price, quantity, type);

    private async Task<RollSchedule> Build(string series, DateOnly from, DateOnly to, int? rollDays = null)
    {
        using var archive = folder.Open();

        var built = await RollSchedule.BuildAsync(archive, SymbolSpec.Parse(series).Value, new DateSpan(from, to), rollDays, null, CancellationToken.None);

        return built.Value;
    }

    [Fact]
    public async Task Calendar_rolls_eight_days_before_expiry_on_the_thursday()
    {
        var schedule = await Build(Calendar, Dec(9), Dec(11));

        schedule.On(Dec(9))!.Symbol.ShouldBe(Front);
        schedule.On(Dec(10))!.Symbol.ShouldBe(Next);
        schedule.On(Dec(11))!.Symbol.ShouldBe(Next);

        var roll = schedule.Rolls.ShouldHaveSingleItem();
        roll.TradeDate.ShouldBe(Dec(10));
        roll.From.Symbol.ShouldBe(Front);
        roll.To.Symbol.ShouldBe(Next);
    }

    [Fact]
    public async Task Gold_rolls_by_the_calendar_before_its_delivery_month_and_skips_thin_months()
    {
        const string gold = "GC";
        var goldExpires = DbnBytes.Utc(2027, 2, 24, 18, 30);
        folder.Add(Definition, gold, Listing,
        [
            DbnBytes.Definition(41, DbnBytes.Start, "GCG7", gold, DefinitionRecord.Future, Tick, Listed, goldExpires, 2027, 2),
            DbnBytes.Definition(42, DbnBytes.Start, "GCH7", gold, DefinitionRecord.Future, Tick, Listed, DbnBytes.Utc(2027, 3, 29, 17, 30), 2027, 3),
            DbnBytes.Definition(43, DbnBytes.Start, "GCJ7", gold, DefinitionRecord.Future, Tick, Listed, DbnBytes.Utc(2027, 4, 28, 17, 30), 2027, 4)
        ]);
        using var archive = folder.Open();

        var schedule = (await RollSchedule.BuildAsync(archive, SymbolSpec.Parse("GC.c.0").Value,
            new DateSpan(new DateOnly(2027, 1, 22), new DateOnly(2027, 1, 25)), null, null, CancellationToken.None)).Value;

        schedule.On(new DateOnly(2027, 1, 22))!.Symbol.ShouldBe("GCG27");
        schedule.On(new DateOnly(2027, 1, 25))!.Symbol.ShouldBe("GCJ27");
    }

    [Fact]
    public async Task Roll_days_move_the_calendar_roll()
    {
        var schedule = await Build(Calendar, Dec(14), Dec(16), rollDays: 3);

        schedule.On(Dec(14))!.Symbol.ShouldBe(Front);
        schedule.On(Dec(15))!.Symbol.ShouldBe(Next);
    }

    [Fact]
    public async Task With_no_roll_days_the_calendar_rolls_on_the_expiry_date()
    {
        var schedule = await Build(Calendar, Dec(17), Dec(18), rollDays: 0);

        schedule.On(Dec(17))!.Symbol.ShouldBe(Front);
        schedule.On(Dec(18))!.Symbol.ShouldBe(Next);
    }

    [Fact]
    public async Task The_second_rank_holds_the_contract_behind_the_front()
    {
        var schedule = await Build(SecondCalendar, Dec(9), Dec(10));

        schedule.On(Dec(9))!.Symbol.ShouldBe(Next);
        schedule.On(Dec(10))!.Symbol.ShouldBe(Third);
    }

    [Fact]
    public async Task The_schedule_remembers_its_series()
    {
        var schedule = await Build(Calendar, Dec(9), Dec(9));

        schedule.Series.Text.ShouldBe(Calendar);
    }

    [Fact]
    public async Task A_contract_not_yet_listed_at_the_open_is_not_held()
    {
        var listedAfterOpen = DbnBytes.Utc(2026, 12, 8, 23, 30);
        List(Future(FrontId, RawFront, FrontExpires, 2026, 12, listedAfterOpen), Future(NextId, RawNext, NextExpires, 2027, 3));

        var schedule = await Build(Calendar, Dec(9), Dec(9));

        schedule.On(Dec(9))!.Symbol.ShouldBe(Next);
    }

    [Fact]
    public async Task A_contract_listed_exactly_at_the_open_is_held()
    {
        var listedAtOpen = DbnBytes.Utc(2026, 12, 8, 23);
        List(Future(FrontId, RawFront, FrontExpires, 2026, 12, listedAtOpen), Future(NextId, RawNext, NextExpires, 2027, 3, listedAtOpen));

        var schedule = await Build(Calendar, Dec(9), Dec(9));

        schedule.On(Dec(9))!.Symbol.ShouldBe(Front);
    }

    [Fact]
    public async Task A_contract_with_no_listing_time_counts_as_listed()
    {
        List(Future(FrontId, RawFront, FrontExpires, 2026, 12, DbnTime.Undefined));

        var schedule = await Build(Calendar, Dec(9), Dec(9));

        schedule.On(Dec(9))!.Symbol.ShouldBe(Front);
    }

    [Fact]
    public async Task A_date_with_nothing_left_to_hold_has_no_contract_and_no_roll()
    {
        List(Future(FrontId, RawFront, FrontExpires, 2026, 12));

        var schedule = await Build(Calendar, Dec(9), Dec(11));

        schedule.On(Dec(9))!.Symbol.ShouldBe(Front);
        schedule.On(Dec(10)).ShouldBeNull();
        schedule.Rolls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Spreads_and_undated_instruments_are_never_held()
    {
        var schedule = await Build("ES.c.3", Dec(9), Dec(9));

        schedule.On(Dec(9)).ShouldBeNull();
    }

    [Fact]
    public async Task A_date_outside_the_schedule_has_no_contract()
    {
        var schedule = await Build(Calendar, Dec(9), Dec(9));

        schedule.On(Dec(8)).ShouldBeNull();
    }

    [Fact]
    public async Task After_a_roll_the_second_rank_moves_on_instead_of_copying_the_front()
    {
        Volumes(Dec(8), (FrontId, 100), (NextId, 50), (ThirdId, 1));
        Volumes(Dec(9), (FrontId, 40), (NextId, 60), (ThirdId, 1));

        var front = await Build(ByVolume, Dec(9), Dec(10));
        var second = await Build(SecondByVolume, Dec(9), Dec(10));

        second.On(Dec(9))!.Symbol.ShouldBe(Next);
        front.On(Dec(10))!.Symbol.ShouldBe(Next);
        second.On(Dec(10))!.Symbol.ShouldBe(Third);
    }

    [Fact]
    public async Task Volume_rolls_at_the_session_after_the_next_contract_overtakes()
    {
        Volumes(Dec(8), (FrontId, 100), (NextId, 50));
        Volumes(Dec(9), (FrontId, 40), (NextId, 60));

        var schedule = await Build(ByVolume, Dec(9), Dec(10));

        schedule.On(Dec(9))!.Symbol.ShouldBe(Front);
        schedule.On(Dec(10))!.Symbol.ShouldBe(Next);
        schedule.Rolls.ShouldHaveSingleItem().TradeDate.ShouldBe(Dec(10));
    }

    [Fact]
    public async Task Volume_of_a_session_cannot_decide_that_same_session()
    {
        Volumes(Dec(8), (FrontId, 100), (NextId, 50));
        Volumes(Dec(9), (FrontId, 1), (NextId, 1000));

        var schedule = await Build(ByVolume, Dec(9), Dec(9));

        schedule.On(Dec(9))!.Symbol.ShouldBe(Front);
    }

    [Fact]
    public async Task A_sessions_volume_adds_up_across_its_bars_and_its_utc_days()
    {
        folder.Add(Ohlcv, Es, Dec(7), Bar(NextId, DbnBytes.Utc(2026, 12, 7, 23, 30), 60));
        folder.Add(Ohlcv, Es, Dec(8), Bar(NextId, Noon(Dec(8)), 60), Bar(FrontId, Noon(Dec(8)), 100));

        var schedule = await Build(ByVolume, Dec(9), Dec(9));

        schedule.On(Dec(9))!.Symbol.ShouldBe(Next);
    }

    [Fact]
    public async Task Volume_after_the_evening_open_belongs_to_the_next_session()
    {
        folder.Add(Ohlcv, Es, Dec(8),
            Bar(FrontId, Noon(Dec(8)), 100),
            Bar(NextId, Noon(Dec(8)), 50),
            Bar(NextId, DbnBytes.Utc(2026, 12, 8, 23, 30), 1000));

        var schedule = await Build(ByVolume, Dec(9), Dec(10));

        schedule.On(Dec(9))!.Symbol.ShouldBe(Front);
        schedule.On(Dec(10))!.Symbol.ShouldBe(Next);
    }

    [Fact]
    public async Task Monday_decides_from_fridays_volume_across_the_weekend()
    {
        Volumes(Dec(4), (FrontId, 10), (NextId, 20));

        var schedule = await Build(ByVolume, Dec(7), Dec(7));

        schedule.On(Dec(7))!.Symbol.ShouldBe(Next);
    }

    [Fact]
    public async Task A_roll_decided_by_fridays_volume_is_held_from_monday()
    {
        Volumes(Dec(3), (FrontId, 100), (NextId, 50));
        Volumes(Dec(4), (FrontId, 10), (NextId, 20));

        var schedule = await Build(ByVolume, Dec(4), Dec(7));

        schedule.On(Dec(4))!.Symbol.ShouldBe(Front);
        schedule.On(Dec(7))!.Symbol.ShouldBe(Next);
        schedule.Rolls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_session_that_traded_nothing_is_passed_over()
    {
        Volumes(Dec(7), (FrontId, 5), (NextId, 1));
        Volumes(Dec(8), (FrontId, 0), (NextId, 0));

        var schedule = await Build(ByVolume, Dec(9), Dec(9));

        schedule.On(Dec(9))!.Symbol.ShouldBe(Front);
    }

    [Fact]
    public async Task With_no_volume_at_all_the_nearest_expiry_is_held()
    {
        var schedule = await Build(ByVolume, Dec(9), Dec(9));

        schedule.On(Dec(9))!.Symbol.ShouldBe(Front);
    }

    [Fact]
    public async Task Equal_volumes_go_to_the_nearer_expiry()
    {
        Volumes(Dec(8), (NextId, 50), (FrontId, 50));

        var schedule = await Build(ByVolume, Dec(9), Dec(9));

        schedule.On(Dec(9))!.Symbol.ShouldBe(Front);
    }

    [Fact]
    public async Task A_contract_with_no_volume_comes_last_so_it_can_still_hold_a_lower_rank()
    {
        Volumes(Dec(8), (FrontId, 100));

        var schedule = await Build(SecondByVolume, Dec(9), Dec(9));

        schedule.On(Dec(9))!.Symbol.ShouldBe(Next);
    }

    [Fact]
    public async Task When_only_an_expired_contract_traded_the_series_moves_to_the_next_expiry()
    {
        var expiredBeforeOpen = DbnBytes.Utc(2026, 12, 8, 21);
        List(Future(FrontId, RawFront, expiredBeforeOpen, 2026, 12), Future(NextId, RawNext, NextExpires, 2027, 3));
        Volumes(Dec(8), (FrontId, 100));

        var schedule = await Build(ByVolume, Dec(9), Dec(9));

        schedule.On(Dec(9))!.Symbol.ShouldBe(Next);
    }

    [Fact]
    public async Task Volume_never_rolls_back_to_an_earlier_contract()
    {
        Volumes(Dec(8), (FrontId, 100), (NextId, 50));
        Volumes(Dec(9), (FrontId, 40), (NextId, 60));
        Volumes(Dec(10), (FrontId, 500), (NextId, 60));

        var schedule = await Build(ByVolume, Dec(9), Dec(11));

        schedule.On(Dec(11))!.Symbol.ShouldBe(Next);
        schedule.Rolls.ShouldHaveSingleItem().To.Symbol.ShouldBe(Next);
    }

    [Fact]
    public async Task Volume_can_skip_a_contract_on_the_way_forward()
    {
        Volumes(Dec(8), (FrontId, 100), (NextId, 50), (ThirdId, 1));
        Volumes(Dec(9), (FrontId, 100), (NextId, 50), (ThirdId, 500));

        var schedule = await Build(ByVolume, Dec(9), Dec(10));

        var roll = schedule.Rolls.ShouldHaveSingleItem();
        roll.From.Symbol.ShouldBe(Front);
        roll.To.Symbol.ShouldBe(Third);
    }

    [Fact]
    public async Task Spreads_and_strangers_do_not_count_as_volume()
    {
        Volumes(Dec(8), (SpreadId, 1000), (StrangerId, 1000), (FrontId, 10), (NextId, 5));

        var schedule = await Build(ByVolume, Dec(9), Dec(9));

        schedule.On(Dec(9))!.Symbol.ShouldBe(Front);
    }

    [Fact]
    public async Task Other_records_among_the_bars_are_ignored()
    {
        folder.Add(Ohlcv, Es, Dec(8),
            DbnBytes.Status(NextId, Noon(Dec(8)), 7, 0, 0, 'Y'),
            Bar(FrontId, Noon(Dec(8)), 10));

        var schedule = await Build(ByVolume, Dec(9), Dec(9));

        schedule.On(Dec(9))!.Symbol.ShouldBe(Front);
    }

    [Fact]
    public async Task Open_interest_rolls_at_the_open_after_it_is_published()
    {
        Interest(Dec(8), (FrontId, 500), (NextId, 400));
        Interest(Dec(9), (FrontId, 300), (NextId, 600));

        var schedule = await Build(ByInterest, Dec(9), Dec(10));

        schedule.On(Dec(9))!.Symbol.ShouldBe(Front);
        schedule.On(Dec(10))!.Symbol.ShouldBe(Next);
        schedule.Rolls.ShouldHaveSingleItem().TradeDate.ShouldBe(Dec(10));
    }

    [Fact]
    public async Task Open_interest_published_at_the_open_counts_only_from_the_next_session()
    {
        var atTheOpen = DbnBytes.Utc(2026, 12, 8, 23);
        Interest(Dec(7), (FrontId, 500), (NextId, 400));
        folder.Add(Statistics, Es, Dec(8), Stat(NextId, atTheOpen, OpenInterest, quantity: 900));

        var schedule = await Build(ByInterest, Dec(9), Dec(10));

        schedule.On(Dec(9))!.Symbol.ShouldBe(Front);
        schedule.On(Dec(10))!.Symbol.ShouldBe(Next);
    }

    [Fact]
    public async Task The_latest_open_interest_is_the_one_that_counts()
    {
        Interest(Dec(7), (FrontId, 900), (NextId, 100));
        Interest(Dec(8), (FrontId, 100), (NextId, 900));

        var schedule = await Build(ByInterest, Dec(9), Dec(9));

        schedule.On(Dec(9))!.Symbol.ShouldBe(Next);
    }

    [Fact]
    public async Task A_date_holds_the_same_contract_whatever_span_it_is_asked_in()
    {
        Interest(Dec(8), (FrontId, 100), (NextId, 900));
        Interest(Dec(10), (FrontId, 900), (NextId, 100));

        var wide = await Build(ByInterest, Dec(9), Dec(11));
        var alone = await Build(ByInterest, Dec(11), Dec(11));

        wide.On(Dec(11))!.Symbol.ShouldBe(Next);
        alone.On(Dec(11))!.Symbol.ShouldBe(Next);
    }

    [Fact]
    public async Task The_week_before_the_span_is_not_part_of_it()
    {
        var schedule = await Build(Calendar, Dec(10), Dec(11));

        schedule.On(Dec(9)).ShouldBeNull();
    }

    [Fact]
    public async Task An_older_value_later_in_the_file_does_not_replace_a_newer_one()
    {
        folder.Add(Statistics, Es, Dec(8),
            Stat(FrontId, AfterClose(Dec(8)), OpenInterest, quantity: 100),
            Stat(NextId, AfterClose(Dec(8)), OpenInterest, quantity: 50),
            Stat(NextId, Noon(Dec(8)), OpenInterest, quantity: 900));

        var schedule = await Build(ByInterest, Dec(9), Dec(9));

        schedule.On(Dec(9))!.Symbol.ShouldBe(Front);
    }

    [Fact]
    public async Task An_undefined_quantity_leaves_the_last_open_interest_standing()
    {
        Interest(Dec(7), (FrontId, 100), (NextId, 900));
        folder.Add(Statistics, Es, Dec(8), Stat(NextId, AfterClose(Dec(8)), OpenInterest));

        var schedule = await Build(ByInterest, Dec(9), Dec(9));

        schedule.On(Dec(9))!.Symbol.ShouldBe(Next);
    }

    [Fact]
    public async Task Statistics_of_other_kinds_and_other_instruments_are_not_open_interest()
    {
        folder.Add(Statistics, Es, Dec(8),
            Stat(FrontId, AfterClose(Dec(8)), OpenInterest, quantity: 100),
            Stat(NextId, AfterClose(Dec(8)), ClearedVolume, quantity: 900),
            Stat(StrangerId, AfterClose(Dec(8)), OpenInterest, quantity: 900));

        var schedule = await Build(ByInterest, Dec(9), Dec(9));

        schedule.On(Dec(9))!.Symbol.ShouldBe(Front);
    }

    [Fact]
    public async Task The_second_rank_holds_the_second_largest_open_interest()
    {
        Interest(Dec(8), (FrontId, 300), (NextId, 500), (ThirdId, 400));

        var schedule = await Build(SecondByInterest, Dec(9), Dec(9));

        schedule.On(Dec(9))!.Symbol.ShouldBe(Third);
    }

    [Fact]
    public async Task With_no_open_interest_the_nearest_expiry_is_held()
    {
        var schedule = await Build(ByInterest, Dec(9), Dec(9));

        schedule.On(Dec(9))!.Symbol.ShouldBe(Front);
    }

    [Fact]
    public async Task Open_interest_never_rolls_back_to_an_earlier_contract()
    {
        Interest(Dec(8), (FrontId, 500), (NextId, 400));
        Interest(Dec(9), (FrontId, 300), (NextId, 600));
        Interest(Dec(10), (FrontId, 700), (NextId, 600));

        var schedule = await Build(ByInterest, Dec(9), Dec(11));

        schedule.On(Dec(11))!.Symbol.ShouldBe(Next);
        schedule.Rolls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_rank_beyond_the_contracts_holds_nothing()
    {
        Interest(Dec(8), (FrontId, 500), (NextId, 400));

        var schedule = await Build(TenthByInterest, Dec(9), Dec(9));

        schedule.On(Dec(9)).ShouldBeNull();
        schedule.Rolls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_series_that_starts_mid_contract_has_no_roll_on_its_first_date()
    {
        var schedule = await Build(Calendar, Dec(10), Dec(11));

        schedule.On(Dec(10))!.Symbol.ShouldBe(Next);
        schedule.Rolls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_roll_carries_both_settlements_of_the_session_before_it()
    {
        Settlements(Dec(9), (FrontId, 100 * Nano), (NextId, 110 * Nano));

        var schedule = await Build(Calendar, Dec(9), Dec(10));

        var roll = schedule.Rolls.ShouldHaveSingleItem();
        roll.FromSettle.ShouldBe(100m);
        roll.ToSettle.ShouldBe(110m);
        roll.Gap.ShouldBe(10m);
        roll.Ratio.ShouldBe(1.1m);
    }

    [Fact]
    public async Task A_settlement_after_the_rolls_open_is_not_used()
    {
        Settlements(Dec(8), (FrontId, 100 * Nano), (NextId, 110 * Nano));
        folder.Add(Statistics, Es, Dec(9), Stat(FrontId, DbnBytes.Utc(2026, 12, 9, 23), SettlementPrice, price: 999 * Nano));

        var schedule = await Build(Calendar, Dec(9), Dec(10));

        schedule.Rolls.ShouldHaveSingleItem().FromSettle.ShouldBe(100m);
    }

    [Fact]
    public async Task A_settlement_with_no_price_leaves_the_last_one_standing()
    {
        Settlements(Dec(8), (FrontId, 100 * Nano), (NextId, 110 * Nano));
        folder.Add(Statistics, Es, Dec(9), Stat(FrontId, AfterClose(Dec(9)), SettlementPrice));

        var schedule = await Build(Calendar, Dec(9), Dec(10));

        schedule.Rolls.ShouldHaveSingleItem().FromSettle.ShouldBe(100m);
    }

    [Fact]
    public async Task Without_a_settlement_a_roll_has_no_gap_and_cannot_be_adjusted_across()
    {
        Settlements(Dec(9), (FrontId, 100 * Nano));

        var schedule = await Build(Calendar, Dec(9), Dec(10));

        var roll = schedule.Rolls.ShouldHaveSingleItem();
        roll.ToSettle.ShouldBeNull();
        roll.Gap.ShouldBeNull();
        roll.Ratio.ShouldBeNull();
        schedule.Adjuster(Dec(9), Adjustment.Difference).Error!.Code.ShouldBe(Errors.NoSettlement);
        schedule.Adjuster(Dec(9), Adjustment.Ratio).Error!.Code.ShouldBe(Errors.NoSettlement);
    }

    [Fact]
    public async Task A_rolls_settlements_come_from_one_session_never_two()
    {
        Settlements(Dec(8), (FrontId, 100 * Nano), (NextId, 110 * Nano));
        Settlements(Dec(9), (FrontId, 105 * Nano));

        var schedule = await Build(Calendar, Dec(9), Dec(10));

        var roll = schedule.Rolls.ShouldHaveSingleItem();
        roll.FromSettle.ShouldBe(100m);
        roll.ToSettle.ShouldBe(110m);
    }

    [Fact]
    public async Task With_no_session_both_settled_a_roll_has_no_gap()
    {
        Settlements(Dec(8), (FrontId, 100 * Nano));
        Settlements(Dec(9), (NextId, 110 * Nano));

        var schedule = await Build(Calendar, Dec(9), Dec(10));

        var roll = schedule.Rolls.ShouldHaveSingleItem();
        roll.Gap.ShouldBeNull();
        schedule.Adjuster(Dec(9), Adjustment.Difference).Error!.Code.ShouldBe(Errors.NoSettlement);
    }

    [Fact]
    public async Task A_settlement_counts_for_the_session_it_refers_to_not_the_day_it_was_published()
    {
        var sessionOfDec8 = DbnBytes.Utc(2026, 12, 8, 0);
        folder.Add(Statistics, Es, Dec(8), DbnBytes.Statistic(FrontId, AfterClose(Dec(8)), sessionOfDec8, 100 * Nano, StatRecord.UndefinedQuantity, SettlementPrice));
        folder.Add(Statistics, Es, Dec(9), DbnBytes.Statistic(NextId, Noon(Dec(9)), sessionOfDec8, 110 * Nano, StatRecord.UndefinedQuantity, SettlementPrice));

        var schedule = await Build(Calendar, Dec(9), Dec(10));

        schedule.Rolls.ShouldHaveSingleItem().Gap.ShouldBe(10m);
    }

    [Fact]
    public async Task A_settlement_of_zero_has_a_gap_but_no_ratio()
    {
        Settlements(Dec(9), (FrontId, 0), (NextId, 110 * Nano));

        var schedule = await Build(Calendar, Dec(9), Dec(10));

        var roll = schedule.Rolls.ShouldHaveSingleItem();
        roll.Gap.ShouldBe(110m);
        roll.Ratio.ShouldBeNull();
        schedule.Adjuster(Dec(9), Adjustment.Ratio).Error!.Code.ShouldBe(Errors.NoSettlement);
        schedule.Adjuster(Dec(9), Adjustment.Difference).Value(100m).ShouldBe(210m);
    }

    /// <summary>ESZ26 to ESH27 on 10 December, gap 10, ratio 1.1; ESH27 to ESM27 on 11 March, gap 30, ratio 1.25.</summary>
    private async Task<RollSchedule> TwoRolls()
    {
        Settlements(Dec(9), (FrontId, 100 * Nano), (NextId, 110 * Nano));
        Settlements(new DateOnly(2027, 3, 10), (NextId, 120 * Nano), (ThirdId, 150 * Nano));

        return await Build(Calendar, Dec(9), new DateOnly(2027, 3, 12));
    }

    [Theory]
    [InlineData(2026, 12, 9, 140)]
    [InlineData(2026, 12, 10, 130)]
    [InlineData(2027, 3, 10, 130)]
    [InlineData(2027, 3, 11, 100)]
    [InlineData(2027, 3, 12, 100)]
    public async Task Difference_adds_the_gaps_of_later_rolls_only(int year, int month, int day, int adjusted)
    {
        var schedule = await TwoRolls();

        schedule.Adjuster(new DateOnly(year, month, day), Adjustment.Difference).Value(100m).ShouldBe(adjusted);
    }

    [Theory]
    [InlineData(2026, 12, 9, "137.5")]
    [InlineData(2026, 12, 10, "125")]
    [InlineData(2027, 3, 10, "125")]
    [InlineData(2027, 3, 11, "100")]
    public async Task Ratio_multiplies_by_the_ratios_of_later_rolls_only(int year, int month, int day, string adjusted)
    {
        var schedule = await TwoRolls();

        schedule.Adjuster(new DateOnly(year, month, day), Adjustment.Ratio).Value(100m).ShouldBe(decimal.Parse(adjusted, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task No_adjustment_leaves_prices_as_traded()
    {
        var schedule = await TwoRolls();

        schedule.Adjuster(Dec(9), Adjustment.None).Value(100m).ShouldBe(100m);
    }

    [Theory]
    [InlineData(Adjustment.None)]
    [InlineData(Adjustment.Difference)]
    [InlineData(Adjustment.Ratio)]
    public async Task No_price_stays_no_price(Adjustment adjustment)
    {
        var schedule = await TwoRolls();

        schedule.Adjuster(Dec(9), adjustment).Value(null).ShouldBeNull();
    }

    [Fact]
    public async Task A_day_with_no_data_is_stepped_over()
    {
        folder.AddEmpty(Statistics, Es, Dec(8));
        Settlements(Dec(9), (FrontId, 100 * Nano), (NextId, 110 * Nano));

        var schedule = await Build(Calendar, Dec(9), Dec(10));

        schedule.Rolls.ShouldHaveSingleItem().Gap.ShouldBe(10m);
    }

    [Fact]
    public async Task Each_file_read_is_reported()
    {
        Volumes(Dec(7), (FrontId, 1));
        Volumes(Dec(8), (FrontId, 1));
        Settlements(Dec(8), (FrontId, 100 * Nano));
        var progress = Substitute.For<IProgress<long>>();
        using var archive = folder.Open();

        await RollSchedule.BuildAsync(archive, SymbolSpec.Parse(ByVolume).Value, new DateSpan(Dec(9), Dec(9)),
            RollSchedule.DefaultRollDays, progress, CancellationToken.None);

        progress.Received(4).Report(1);
    }

    [Theory]
    [InlineData(Es)]
    [InlineData(Front)]
    public async Task Only_a_continuous_name_has_a_schedule(string symbol)
    {
        using var archive = folder.Open();

        await Should.ThrowAsync<ArgumentException>(() => RollSchedule.BuildAsync(archive, SymbolSpec.Parse(symbol).Value,
            new DateSpan(Dec(9), Dec(9)), RollSchedule.DefaultRollDays, null, CancellationToken.None));
    }

    [Fact]
    public async Task A_refused_archive_is_an_error_not_an_empty_schedule()
    {
        var store = Substitute.For<IObjectStore>();
        store.ObjectsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Throws(new ArchiveException(new Error(Errors.Denied, Errors.Denied)));
        using var archive = new DbnArchive(store, null);

        var result = await RollSchedule.BuildAsync(archive, SymbolSpec.Parse(Calendar).Value, new DateSpan(Dec(9), Dec(9)),
            RollSchedule.DefaultRollDays, null, CancellationToken.None);

        result.Error!.Code.ShouldBe(Errors.Denied);
    }

    [Theory]
    [InlineData(Statistics, Calendar)]
    [InlineData(Ohlcv, ByVolume)]
    public async Task A_day_that_cannot_be_read_is_an_error_not_a_roll_without_a_gap(string schema, string series)
    {
        using var archive = folder.OpenFailing(key => key.Contains($"/{schema}/", StringComparison.Ordinal),
            new Error(Errors.Unreachable, Errors.Unreachable));

        var result = await RollSchedule.BuildAsync(archive, SymbolSpec.Parse(series).Value, new DateSpan(Dec(9), Dec(10)),
            RollSchedule.DefaultRollDays, null, CancellationToken.None);

        result.Error!.Code.ShouldBe(Errors.Unreachable);
    }

    [Fact]
    public async Task A_roll_decided_by_fridays_volume_is_first_used_on_monday()
    {
        Volumes(Dec(10), (FrontId, 100), (NextId, 10));
        Volumes(Dec(11), (FrontId, 10), (NextId, 100));
        Settlements(Dec(11), (FrontId, 100 * Nano), (NextId, 110 * Nano));

        var schedule = await Build(ByVolume, Dec(10), Dec(15));

        schedule.Rolls.ShouldHaveSingleItem().TradeDate.ShouldBe(Dec(14));
        schedule.On(Dec(11))!.Symbol.ShouldBe(Front);
        schedule.On(Dec(14))!.Symbol.ShouldBe(Next);
    }

    [Fact]
    public async Task What_the_schedule_holds_over_the_weekend_does_not_move_with_the_date_shown()
    {
        Volumes(Dec(10), (FrontId, 100), (NextId, 10));
        Volumes(Dec(11), (FrontId, 10), (NextId, 100));
        Settlements(Dec(11), (FrontId, 100 * Nano), (NextId, 110 * Nano));

        var schedule = await Build(ByVolume, Dec(10), Dec(15));

        schedule.On(Dec(12))!.Symbol.ShouldBe(Next);
        schedule.On(Dec(13))!.Symbol.ShouldBe(Next);
        schedule.Adjuster(Dec(11), Adjustment.Difference).Value(100m).ShouldBe(110m);
        schedule.Adjuster(Dec(12), Adjustment.Difference).Value(100m).ShouldBe(100m);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(5)]
    public async Task A_calendar_roll_that_falls_on_a_weekend_is_first_used_on_monday(int rollDays)
    {
        var schedule = await Build(Calendar, Dec(9), Dec(15), rollDays);

        schedule.Rolls.ShouldHaveSingleItem().TradeDate.ShouldBe(Dec(14));
    }

    [Fact]
    public async Task A_roll_on_a_weekday_keeps_its_day()
    {
        var schedule = await Build(Calendar, Dec(9), Dec(11));

        schedule.Rolls.ShouldHaveSingleItem().TradeDate.ShouldBe(Dec(10));
    }

    [Fact]
    public async Task The_error_for_a_missing_settlement_names_the_roll_and_the_contracts()
    {
        var schedule = await Build(Calendar, Dec(9), Dec(10));

        var error = schedule.Adjuster(Dec(9), Adjustment.Difference).Error!;

        error.Message.ShouldContain("2026-12-10");
        error.Message.ShouldContain(Front);
        error.Message.ShouldContain(Next);
    }

    [Fact]
    public async Task A_price_after_a_roll_with_no_settlement_does_not_need_it()
    {
        var schedule = await Build(Calendar, Dec(9), Dec(11));

        schedule.Adjuster(Dec(10), Adjustment.Difference).Value(100m).ShouldBe(100m);
    }

    [Fact]
    public async Task A_damaged_statistics_file_is_a_result_naming_it()
    {
        folder.Write(DbnSyncLayout.Data(Dataset, Statistics, Es, Dec(9)), [1, 2, 3, 4, 5, 6, 7, 8]);
        using var archive = folder.Open();

        var built = await RollSchedule.BuildAsync(archive, SymbolSpec.Parse(Calendar).Value, new DateSpan(Dec(9), Dec(10)),
            RollSchedule.DefaultRollDays, null, CancellationToken.None);

        built.Error!.Code.ShouldBe(Errors.Corrupt);
        built.Error.Message.ShouldContain($"{Es}.{Statistics}.20261209");
    }

    [Fact]
    public async Task A_statistics_file_damaged_part_way_through_is_a_result_too()
    {
        var record = Stat(FrontId, AfterClose(Dec(9)), SettlementPrice, price: 100 * Nano);
        folder.Add(Statistics, Es, Dec(9), record, record[..30]);
        using var archive = folder.Open();

        var built = await RollSchedule.BuildAsync(archive, SymbolSpec.Parse(Calendar).Value, new DateSpan(Dec(9), Dec(10)),
            RollSchedule.DefaultRollDays, null, CancellationToken.None);

        built.Error!.Code.ShouldBe(Errors.Corrupt);
    }
}
