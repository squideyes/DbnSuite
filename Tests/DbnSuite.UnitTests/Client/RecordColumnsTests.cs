using DbnSuite.Client.Dbn;
using DbnSuite.Client.Export;
using Shouldly;

namespace DbnSuite.UnitTests.ClientTests;

public sealed class RecordColumnsTests
{
    private const string Symbol = "ESZ26";
    private const string Series = "ES.v.0";
    private const uint Instrument = 42;
    private const ulong Time = 1_793_194_200_000_000_000;
    private const ulong Received = Time + 7;
    private const ulong Reference = Time - 100;
    private const ushort Publisher = 1;

    private static readonly Func<decimal?, decimal?> AsTraded = p => p;

    private static IEnumerable<string> Names(string schema, bool series = false) =>
        RecordColumns.For(schema, series).Select(c => c.Name);

    private static Dictionary<string, Cell> Filled(string schema, DbnRecord record, string? series = null, Func<decimal?, decimal?>? price = null)
    {
        var columns = RecordColumns.For(schema, series is not null);
        var cells = new Cell[columns.Count];

        RecordColumns.Fill(record, Symbol, series, price ?? AsTraded, cells);

        return columns.Select((c, i) => (c.Name, Cell: cells[i])).ToDictionary(x => x.Name, x => x.Cell);
    }

    private static MboRecord Mbo() =>
        new(Publisher, Instrument, Time, 8_068_565, 100.25m, 3, MboRecord.LastFlag, 'A', 'B', Received, 4, 0, 99);

    private static OhlcvRecord Bar() =>
        new(OhlcvRecord.FirstType, Publisher, Instrument, Time, 100m, 102m, 99m, 101m, 12);

    private static StatRecord Stat(ushort type, ulong tsRef = Reference, long quantity = 500) =>
        new(Publisher, Instrument, Time, tsRef, 100.25m, quantity, type, 2);

    private static DefinitionRecord Definition(ulong activation, ulong expiration) =>
        new(Publisher, Instrument, Time, 0.25m, expiration, activation, "ESZ6", "ES", DefinitionRecord.Future, 2026, 12);

    [Fact]
    public void Mbo_has_the_documented_columns()
    {
        Names("mbo").ShouldBe(["ts_recv", "ts_event", "symbol", "instrument_id", "action", "side", "price", "size", "order_id", "flags", "channel_id", "sequence"]);
    }

    [Theory]
    [InlineData("ohlcv-1s")]
    [InlineData("ohlcv-1m")]
    [InlineData("ohlcv-1h")]
    [InlineData("ohlcv-1d")]
    [InlineData("ohlcv-eod")]
    public void Every_ohlcv_schema_has_the_documented_columns(string schema)
    {
        Names(schema).ShouldBe(["ts_event", "symbol", "instrument_id", "open", "high", "low", "close", "volume"]);
    }

    [Fact]
    public void Statistics_has_the_documented_columns()
    {
        Names("statistics").ShouldBe(["ts_event", "ts_ref", "symbol", "instrument_id", "stat_type", "price", "quantity", "update_action"]);
    }

    [Fact]
    public void Status_has_the_documented_columns()
    {
        Names("status").ShouldBe(["ts_event", "symbol", "instrument_id", "action", "reason", "trading_event", "is_trading"]);
    }

    [Fact]
    public void Definition_has_the_documented_columns()
    {
        Names("definition").ShouldBe(["ts_event", "symbol", "raw_symbol", "instrument_id", "asset", "instrument_class", "min_price_increment", "activation", "expiration"]);
    }

    [Fact]
    public void Any_other_schema_has_the_header_columns()
    {
        Names("mbp-1").ShouldBe(["ts_event", "symbol", "instrument_id", "rtype"]);
    }

    [Theory]
    [InlineData("mbo")]
    [InlineData("ohlcv-1s")]
    [InlineData("statistics")]
    [InlineData("status")]
    [InlineData("definition")]
    [InlineData("mbp-1")]
    public void A_series_comes_last_and_only_when_there_is_one(string schema)
    {
        Names(schema, series: true).Last().ShouldBe("series");
        Names(schema).ShouldNotContain("series");
    }

    [Fact]
    public void Times_prices_integers_and_text_are_typed_as_their_columns_hold_them()
    {
        var columns = RecordColumns.For("mbo", series: false).ToDictionary(c => c.Name, c => c.Kind);

        columns["ts_recv"].ShouldBe(CellKind.Time);
        columns["price"].ShouldBe(CellKind.Price);
        columns["size"].ShouldBe(CellKind.Integer);
        columns["symbol"].ShouldBe(CellKind.Text);
    }

    [Fact]
    public void An_mbo_record_fills_every_column()
    {
        var cells = Filled("mbo", Mbo());

        cells["ts_recv"].Number.ShouldBe((long)Received);
        cells["ts_event"].Number.ShouldBe((long)Time);
        cells["symbol"].Text.ShouldBe(Symbol);
        cells["instrument_id"].Number.ShouldBe(Instrument);
        cells["action"].Text.ShouldBe("A");
        cells["side"].Text.ShouldBe("B");
        cells["price"].Price.ShouldBe(100.25m);
        cells["size"].Number.ShouldBe(3);
        cells["order_id"].Number.ShouldBe(8_068_565);
        cells["flags"].Number.ShouldBe(MboRecord.LastFlag);
        cells["channel_id"].Number.ShouldBe(4);
        cells["sequence"].Number.ShouldBe(99);
    }

    [Fact]
    public void A_bar_fills_every_column()
    {
        var cells = Filled("ohlcv-1s", Bar());

        cells["ts_event"].Number.ShouldBe((long)Time);
        cells["symbol"].Text.ShouldBe(Symbol);
        cells["instrument_id"].Number.ShouldBe(Instrument);
        cells["open"].Price.ShouldBe(100m);
        cells["high"].Price.ShouldBe(102m);
        cells["low"].Price.ShouldBe(99m);
        cells["close"].Price.ShouldBe(101m);
        cells["volume"].Number.ShouldBe(12);
    }

    [Fact]
    public void A_statistic_fills_every_column()
    {
        var cells = Filled("statistics", Stat(3));

        cells["ts_event"].Number.ShouldBe((long)Time);
        cells["ts_ref"].Number.ShouldBe((long)Reference);
        cells["symbol"].Text.ShouldBe(Symbol);
        cells["instrument_id"].Number.ShouldBe(Instrument);
        cells["stat_type"].Text.ShouldBe("settlement_price");
        cells["price"].Price.ShouldBe(100.25m);
        cells["quantity"].Number.ShouldBe(500);
        cells["update_action"].Number.ShouldBe(2);
    }

    [Theory]
    [InlineData(1, "opening_price")]
    [InlineData(3, "settlement_price")]
    [InlineData(6, "cleared_volume")]
    [InlineData(9, "open_interest")]
    [InlineData(19, "block_volume")]
    [InlineData(20, "20")]
    [InlineData(10001, "venue_specific_volume_1")]
    [InlineData(21, "21")]
    [InlineData(999, "999")]
    public void A_stat_type_is_a_name_or_the_number_when_unknown(int type, string written)
    {
        Filled("statistics", Stat((ushort)type))["stat_type"].Text.ShouldBe(written);
    }

    [Fact]
    public void An_undefined_reference_time_and_quantity_are_empty()
    {
        var cells = Filled("statistics", Stat(3, DbnTime.Undefined, StatRecord.UndefinedQuantity));

        cells["ts_ref"].IsEmpty.ShouldBeTrue();
        cells["quantity"].IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void A_status_fills_every_column()
    {
        var cells = Filled("status", new StatusRecord(Publisher, Instrument, Time, 7, 2, 1, 'Y'));

        cells["ts_event"].Number.ShouldBe((long)Time);
        cells["symbol"].Text.ShouldBe(Symbol);
        cells["instrument_id"].Number.ShouldBe(Instrument);
        cells["action"].Number.ShouldBe(7);
        cells["reason"].Number.ShouldBe(2);
        cells["trading_event"].Number.ShouldBe(1);
        cells["is_trading"].Text.ShouldBe("Y");
    }

    [Fact]
    public void A_definition_fills_every_column()
    {
        var cells = Filled("definition", Definition(Reference, Received));

        cells["ts_event"].Number.ShouldBe((long)Time);
        cells["symbol"].Text.ShouldBe(Symbol);
        cells["raw_symbol"].Text.ShouldBe("ESZ6");
        cells["instrument_id"].Number.ShouldBe(Instrument);
        cells["asset"].Text.ShouldBe("ES");
        cells["instrument_class"].Text.ShouldBe("F");
        cells["min_price_increment"].Price.ShouldBe(0.25m);
        cells["activation"].Number.ShouldBe((long)Reference);
        cells["expiration"].Number.ShouldBe((long)Received);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(DbnTime.Undefined)]
    public void A_definitions_unset_times_are_empty(ulong unset)
    {
        var cells = Filled("definition", Definition(unset, unset));

        cells["activation"].IsEmpty.ShouldBeTrue();
        cells["expiration"].IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void Any_other_record_fills_its_header()
    {
        var cells = Filled("mbp-1", new OtherRecord(0x01, Publisher, Instrument, Time, 80));

        cells["ts_event"].Number.ShouldBe((long)Time);
        cells["symbol"].Text.ShouldBe(Symbol);
        cells["instrument_id"].Number.ShouldBe(Instrument);
        cells["rtype"].Number.ShouldBe(1);
    }

    public static TheoryData<string, DbnRecord> EveryKind() => new()
    {
        { "mbo", Mbo() },
        { "ohlcv-1s", Bar() },
        { "statistics", Stat(3) },
        { "status", new StatusRecord(Publisher, Instrument, Time, 7, 2, 1, 'Y') },
        { "definition", Definition(Reference, Received) },
        { "mbp-1", new OtherRecord(0x01, Publisher, Instrument, Time, 80) }
    };

    [Theory]
    [MemberData(nameof(EveryKind))]
    public void The_series_goes_in_its_column_for_every_kind_of_record(string schema, DbnRecord record)
    {
        Filled(schema, record, Series)["series"].Text.ShouldBe(Series);
    }

    [Fact]
    public void Every_price_goes_through_the_function_given()
    {
        var cells = Filled("ohlcv-1s", Bar(), price: p => p * 2);

        cells["open"].Price.ShouldBe(200m);
        cells["high"].Price.ShouldBe(204m);
        cells["low"].Price.ShouldBe(198m);
        cells["close"].Price.ShouldBe(202m);
    }

    [Fact]
    public void Mbo_and_statistics_prices_go_through_it_too()
    {
        Filled("mbo", Mbo(), price: _ => 1m)["price"].Price.ShouldBe(1m);
        Filled("statistics", Stat(3), price: _ => 1m)["price"].Price.ShouldBe(1m);
    }

    [Fact]
    public void Mbo_is_filed_by_when_it_was_received_and_everything_else_by_its_event()
    {
        RecordColumns.IndexTime(Mbo()).ShouldBe(Received);
        RecordColumns.IndexTime(Bar()).ShouldBe(Time);
    }
}
