using DbnClient.Dbn;
using Shouldly;

namespace DbnSync.UnitTests.ClientTests;

/// <summary>Records decoded from bytes laid out by hand as DBN version 3.</summary>
public sealed class DbnRecordsTests
{
    private const uint Instrument = 42;
    private const ulong Event = 1_793_194_200_000_000_000;
    private const ulong Received = Event + 700;
    private const ulong Reference = Event - 900;
    private const long Price = 100_250_000_000;
    private const decimal Dollars = 100.25m;
    private const long Nano = 1_000_000_000;
    private const ushort Publisher = 1;

    [Fact]
    public void An_mbo_record_decodes_every_field()
    {
        var bytes = DbnBytes.Mbo(Instrument, Event, Received, Price, 3, 'F', 'A', 8_068_565, MboRecord.LastFlag, channel: 4, tsInDelta: -12, sequence: 99);

        var mbo = DbnRecord.Decode(bytes).ShouldBeOfType<MboRecord>();

        mbo.ShouldBe(new MboRecord(Publisher, Instrument, Event, 8_068_565, Dollars, 3, MboRecord.LastFlag, 'F', 'A', Received, 4, -12, 99));
        mbo.RType.ShouldBe(MboRecord.Type);
    }

    [Theory]
    [InlineData(0x20)]
    [InlineData(0x21)]
    [InlineData(0x22)]
    [InlineData(0x23)]
    [InlineData(0x24)]
    public void Every_bar_size_decodes_as_a_bar(byte rtype)
    {
        var bytes = DbnBytes.Ohlcv(Instrument, Event, 100 * Nano, 102 * Nano, 99 * Nano, 101 * Nano, 12, rtype);

        var bar = DbnRecord.Decode(bytes).ShouldBeOfType<OhlcvRecord>();

        bar.ShouldBe(new OhlcvRecord(rtype, Publisher, Instrument, Event, 100m, 102m, 99m, 101m, 12));
    }

    [Fact]
    public void A_bar_with_no_prices_reads_as_none()
    {
        var bytes = DbnBytes.Ohlcv(Instrument, Event, DbnPrice.Undefined, DbnPrice.Undefined, DbnPrice.Undefined, DbnPrice.Undefined, 0);

        var bar = DbnRecord.Decode(bytes).ShouldBeOfType<OhlcvRecord>();

        bar.Open.ShouldBeNull();
        bar.Close.ShouldBeNull();
    }

    [Fact]
    public void A_statistic_decodes_every_field()
    {
        var bytes = DbnBytes.Statistic(Instrument, Event, Reference, Price, 500, 3, updateAction: 2);

        var stat = DbnRecord.Decode(bytes).ShouldBeOfType<StatRecord>();

        stat.ShouldBe(new StatRecord(Publisher, Instrument, Event, Reference, Dollars, 500, 3, 2));
    }

    [Fact]
    public void A_statistic_keeps_the_no_quantity_sentinel()
    {
        var bytes = DbnBytes.Statistic(Instrument, Event, DbnTime.Undefined, DbnPrice.Undefined, StatRecord.UndefinedQuantity, 3);

        var stat = DbnRecord.Decode(bytes).ShouldBeOfType<StatRecord>();

        stat.Quantity.ShouldBe(StatRecord.UndefinedQuantity);
        stat.Price.ShouldBeNull();
        stat.TsRef.ShouldBe(DbnTime.Undefined);
    }

    [Fact]
    public void A_status_decodes_every_field()
    {
        var bytes = DbnBytes.Status(Instrument, Event, 7, 2, 1, 'Y');

        var status = DbnRecord.Decode(bytes).ShouldBeOfType<StatusRecord>();

        status.ShouldBe(new StatusRecord(Publisher, Instrument, Event, 7, 2, 1, 'Y'));
    }

    [Fact]
    public void An_unknown_type_is_other_and_keeps_its_length()
    {
        var other = DbnRecord.Decode(DbnBytes.Unknown(0x99, 24, Instrument, Event)).ShouldBeOfType<OtherRecord>();

        other.ShouldBe(new OtherRecord(0x99, Publisher, Instrument, Event, 24));
    }

    [Theory]
    [InlineData(MboRecord.Type)]
    [InlineData(OhlcvRecord.FirstType)]
    [InlineData(OhlcvRecord.LastType)]
    [InlineData(DefinitionRecord.Type)]
    [InlineData(StatRecord.Type)]
    [InlineData(StatusRecord.Type)]
    public void A_known_type_of_an_unexpected_size_is_not_misread(byte rtype)
    {
        const int OtherVersionsSize = 32;

        DbnRecord.Decode(DbnBytes.Unknown(rtype, OtherVersionsSize, Instrument, Event)).ShouldBeOfType<OtherRecord>();
    }

    [Fact]
    public void The_instrument_is_read_from_the_header_alone()
    {
        DbnRecord.InstrumentOf(DbnBytes.Unknown(0x99, 16, Instrument, Event)).ShouldBe(Instrument);
    }

    [Fact]
    public void Mbo_is_filed_by_when_it_was_received()
    {
        var bytes = DbnBytes.Mbo(Instrument, Event, Received, Price, 3, 'A', 'B', 1, MboRecord.LastFlag);

        DbnRecord.IndexTimeOf(bytes).ShouldBe(Received);
    }

    [Fact]
    public void A_definition_is_filed_by_when_it_was_received()
    {
        var bytes = DbnBytes.Definition(Instrument, Event, "ESZ6", "ES", DefinitionRecord.Future, 250_000_000, 1, 2, 2026, 12, Received);

        DbnRecord.IndexTimeOf(bytes).ShouldBe(Received);
    }

    [Fact]
    public void Everything_else_is_filed_by_its_event_time()
    {
        DbnRecord.IndexTimeOf(DbnBytes.Ohlcv(Instrument, Event, 1, 1, 1, 1, 1)).ShouldBe(Event);
    }

    [Fact]
    public void An_mbo_type_too_short_to_hold_a_receive_time_is_filed_by_its_event_time()
    {
        DbnRecord.IndexTimeOf(DbnBytes.Unknown(MboRecord.Type, 32, Instrument, Event)).ShouldBe(Event);
    }

    [Theory]
    [InlineData(1, "0.000000001")]
    [InlineData(-37_630_000_000, "-37.63")]
    [InlineData(6_876_250_000_000, "6876.25")]
    public void Prices_are_units_of_one_billionth(long raw, string dollars)
    {
        DbnPrice.From(raw).ShouldBe(decimal.Parse(dollars, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void The_no_price_sentinel_is_no_price()
    {
        DbnPrice.From(DbnPrice.Undefined).ShouldBeNull();
    }

    [Fact]
    public void A_time_is_counted_from_the_unix_epoch_in_utc()
    {
        var time = DbnTime.ToTime(Event + 123);

        time.ShouldBe(new DateTimeOffset(2026, 10, 28, 13, 30, 0, TimeSpan.Zero).AddTicks(1));
    }

    [Fact]
    public void The_no_time_sentinel_prints_as_a_dash()
    {
        DbnTime.Format(DbnTime.Undefined).ShouldBe("-");
    }
}
