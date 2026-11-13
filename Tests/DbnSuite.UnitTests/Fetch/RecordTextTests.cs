using DbnSuite.Client.Dbn;
using DbnSuite.Client.Time;
using DbnSuite.Fetch;
using Shouldly;
using static DbnSuite.UnitTests.TestData;

namespace DbnSuite.UnitTests.FetchTests;

public sealed class RecordTextTests
{
    private const string Symbol = SampleArchive.Front;
    private const ushort Publisher = 1;
    private const uint Instrument = 42;
    private const ulong OrderId = 6883391723797;
    private const string Stamp = "2026-10-30T12:00:00.000000000Z";

    private static readonly ulong Time = SampleArchive.At(Day(10, 30), 12);

    private static string Format(DbnRecord record) => RecordText.Format(record, Symbol, TimeZoneInfo.Utc);

    private static MboRecord Order(char action, char side, decimal? price = 7820.25m) =>
        new(Publisher, Instrument, Time, OrderId, price, 3, 0, action, side, Time);

    private static StatRecord Stat(ushort type, decimal? price, long quantity) =>
        new(Publisher, Instrument, Time, Time, price, quantity, type, 1);

    private static DefinitionRecord Definition(ulong expiration) =>
        new(Publisher, Instrument, Time, 0.25m, expiration, Time, SampleArchive.FrontRaw, Root, DefinitionRecord.Future, 2026, 12);

    [Fact]
    public void A_line_opens_with_the_time_and_the_symbol()
    {
        Format(Order('A', 'B')).ShouldStartWith($"{Stamp}  {Symbol}");
    }

    [Fact]
    public void Times_are_shown_in_the_zone_asked_for()
    {
        var chicago = Zones.Find(Zones.Default).Value;

        RecordText.Format(Order('A', 'B'), Symbol, chicago).ShouldStartWith("2026-10-30T07:00:00.000000000-05:00");
    }

    [Theory]
    [InlineData('A', "add")]
    [InlineData('C', "cancel")]
    [InlineData('M', "modify")]
    [InlineData('R', "clear")]
    [InlineData('T', "trade")]
    [InlineData('F', "fill")]
    [InlineData('N', "none")]
    public void An_order_event_names_its_action(char action, string expected)
    {
        Format(Order(action, 'B')).ShouldContain($" {expected}");
    }

    [Theory]
    [InlineData('B', "bid")]
    [InlineData('A', "ask")]
    [InlineData('N', "-")]
    public void An_order_event_names_its_side(char side, string expected)
    {
        Format(Order('A', side)).ShouldContain($"add    {expected}");
    }

    [Fact]
    public void An_order_shows_its_size_price_and_id()
    {
        Format(Order('A', 'B')).ShouldEndWith("3 @ 7820.25      order 6883391723797");
    }

    [Fact]
    public void An_event_with_no_price_shows_a_dash()
    {
        Format(Order('R', 'N', price: null)).ShouldContain("@ - ");
    }

    [Fact]
    public void A_bar_shows_its_prices_and_volume()
    {
        var bar = new OhlcvRecord(OhlcvRecord.FirstType, Publisher, Instrument, Time, 1.5m, 2.25m, 1m, 2m, 40);

        Format(bar).ShouldEndWith("O 1.5  H 2.25  L 1  C 2  V 40");
    }

    [Fact]
    public void A_definition_shows_its_symbol_tick_and_expiry()
    {
        Format(Definition(SampleArchive.At(SampleArchive.FrontExpiry))).ShouldEndWith("tick 0.25  expires 2026-12-18");
    }

    [Fact]
    public void A_definition_with_no_expiry_shows_a_dash()
    {
        Format(Definition(DbnTime.Undefined)).ShouldEndWith("expires -");
    }

    [Fact]
    public void A_statistic_with_a_price_shows_the_price()
    {
        Format(Stat(SampleArchive.Settlement, 7820.5m, StatRecord.UndefinedQuantity)).ShouldEndWith("settlement price         7820.5");
    }

    [Fact]
    public void A_statistic_with_a_quantity_shows_the_quantity()
    {
        Format(Stat(SampleArchive.OpenInterest, null, 1_234_567)).ShouldEndWith("open interest            1,234,567");
    }

    [Fact]
    public void A_statistic_with_neither_shows_a_dash()
    {
        Format(Stat(SampleArchive.Settlement, null, StatRecord.UndefinedQuantity)).ShouldEndWith("settlement price         -");
    }

    [Fact]
    public void A_statistic_of_an_unknown_type_shows_its_number()
    {
        Format(Stat(99, 1m, 0)).ShouldContain(" 99 ");
    }

    [Fact]
    public void A_status_shows_its_action_and_whether_it_trades()
    {
        var status = new StatusRecord(Publisher, Instrument, Time, 7, 0, 0, 'Y');

        Format(status).ShouldEndWith("trading              trading Y");
    }

    [Fact]
    public void A_status_of_an_unknown_action_shows_its_number()
    {
        var status = new StatusRecord(Publisher, Instrument, Time, 99, 0, 0, 'N');

        Format(status).ShouldContain(" 99 ");
    }

    [Fact]
    public void A_record_dbnf_does_not_decode_shows_its_type_and_size()
    {
        var other = new OtherRecord(0x99, Publisher, Instrument, Time, 64);

        Format(other).ShouldEndWith("rtype 0x99, 64 bytes");
    }
}
