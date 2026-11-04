using DbnClient.Dbn;
using DbnClient.Export;
using Shouldly;

namespace DbnSync.UnitTests.ClientTests;

/// <summary>Books rebuilt from hand-made events, checked against the rules in the layout's "Rebuilt books and trades".</summary>
public sealed class MboTransformTests
{
    private const string Trades = "trades";
    private const string Tbbo = "tbbo";
    private const string Mbp1 = "mbp-1";
    private const string Mbp10 = "mbp-10";
    private const string Bbo1s = "bbo-1s";
    private const string Bbo1m = "bbo-1m";
    private const string Symbol = "ESZ26";
    private const string Series = "ES.n.0";
    private const string TsRecv = "ts_recv";
    private const string TsEvent = "ts_event";
    private const string SymbolColumn = "symbol";
    private const string InstrumentId = "instrument_id";
    private const string Side = "side";
    private const string Price = "price";
    private const string Size = "size";
    private const string Sequence = "sequence";
    private const string Action = "action";
    private const string SeriesColumn = "series";
    private const string BidPrice = "bid_px_00";
    private const string Bid = "bid";
    private const string Ask = "ask";
    private const uint Instrument = 42;
    private const uint OtherInstrument = 43;
    private const ulong Second = 1_000_000_000;
    private const ulong Minute = 60 * Second;

    /// <summary>2026-10-28 13:30:00 UTC, on a whole minute.</summary>
    private const ulong T0 = 1_793_194_200_000_000_000;

    private const byte Last = MboRecord.LastFlag;
    private const byte NoFlags = 0;
    private const byte Snapshot = MboRecord.SnapshotFlag;

    private static readonly DateOnly Date = new(2026, 10, 28);
    private static readonly DateOnly NextDate = Date.AddDays(1);

    private static MboRecord Event(char action, char side, decimal? price, uint size, ulong order, ulong time = T0,
        byte flags = Last, uint instrument = Instrument, uint sequence = 0) =>
        new(1, instrument, time + 5, order, price, size, flags, action, side, time, Sequence: sequence);

    /// <summary>A transformation being fed, and the rows it has written.</summary>
    private sealed class Rebuild(string target, bool series = false)
    {
        private readonly MboTransform transform = MboTransform.Create(target, series).Value;

        public List<(DateOnly Date, Cell[] Cells)> Rows { get; } = [];

        public IEnumerable<string> Names => transform.Columns.Select(c => c.Name);

        public Rebuild Feed(MboRecord m, DateOnly? tradeDate) => Feed(m, tradeDate, p => p);

        /// <summary>An event outside the dates and hours asked for.</summary>
        public Rebuild FeedOutside(MboRecord m) => Feed(m, (DateOnly?)null);

        public Rebuild Feed(MboRecord m, DateOnly? tradeDate, Func<decimal?, decimal?> price)
        {
            transform.Process(m, tradeDate, Symbol, series ? Series : null, price, (date, row) => Rows.Add((date, row)));

            return this;
        }

        public Rebuild Feed(params MboRecord[] events)
        {
            foreach (var m in events)
                Feed(m, Date);

            return this;
        }

        public void Flush(DateOnly? before = null) => transform.Flush(before, (date, row) => Rows.Add((date, row)));

        public Cell Cell(int row, string column) => Rows[row].Cells[Names.ToList().IndexOf(column)];
    }

    private static void ShouldHaveLevel(Rebuild rebuild, int row, string side, int level, decimal price, long size, long count)
    {
        rebuild.Cell(row, $"{side}_px_{level:D2}").Price.ShouldBe(price);
        rebuild.Cell(row, $"{side}_sz_{level:D2}").Number.ShouldBe(size);
        rebuild.Cell(row, $"{side}_ct_{level:D2}").Number.ShouldBe(count);
    }

    private static void ShouldHaveNoLevel(Rebuild rebuild, int row, string side, int level)
    {
        rebuild.Cell(row, $"{side}_px_{level:D2}").IsEmpty.ShouldBeTrue();
        rebuild.Cell(row, $"{side}_sz_{level:D2}").IsEmpty.ShouldBeTrue();
        rebuild.Cell(row, $"{side}_ct_{level:D2}").IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void The_names_are_the_ones_mbo_can_become()
    {
        MboTransform.Names.ShouldBe([Trades, Tbbo, Mbp1, Mbp10, Bbo1s, Bbo1m]);
    }

    [Fact]
    public void Anything_else_is_refused_by_code_and_name()
    {
        var created = MboTransform.Create("ohlcv-1s", series: false);

        created.Error!.Code.ShouldBe("input.bad_as");
        created.Error.Message.ShouldContain("ohlcv-1s");
    }

    [Fact]
    public void Trades_have_the_documented_columns()
    {
        new Rebuild(Trades).Names.ShouldBe(["ts_recv", "ts_event", "symbol", "instrument_id", "side", "price", "size", "sequence"]);
    }

    [Fact]
    public void Tbbo_adds_level_00()
    {
        new Rebuild(Tbbo).Names.ShouldBe(["ts_recv", "ts_event", "symbol", "instrument_id", "side", "price", "size", "sequence",
            "bid_px_00", "bid_sz_00", "bid_ct_00", "ask_px_00", "ask_sz_00", "ask_ct_00"]);
    }

    [Fact]
    public void Mbp_1_adds_the_action_then_level_00()
    {
        new Rebuild(Mbp1).Names.ShouldBe(["ts_recv", "ts_event", "symbol", "instrument_id", "side", "price", "size", "sequence", "action",
            "bid_px_00", "bid_sz_00", "bid_ct_00", "ask_px_00", "ask_sz_00", "ask_ct_00"]);
    }

    [Fact]
    public void Mbp_10_has_ten_levels_in_order()
    {
        var names = new Rebuild(Mbp10).Names.ToList();

        names.Count.ShouldBe(9 + 10 * 6);
        names[9..15].ShouldBe(["bid_px_00", "bid_sz_00", "bid_ct_00", "ask_px_00", "ask_sz_00", "ask_ct_00"]);
        names[^6..].ShouldBe(["bid_px_09", "bid_sz_09", "bid_ct_09", "ask_px_09", "ask_sz_09", "ask_ct_09"]);
    }

    [Theory]
    [InlineData(Bbo1s)]
    [InlineData(Bbo1m)]
    public void Bbo_has_the_last_trade_then_level_00(string target)
    {
        new Rebuild(target).Names.ShouldBe(["ts_recv", "symbol", "instrument_id", "price", "size",
            "bid_px_00", "bid_sz_00", "bid_ct_00", "ask_px_00", "ask_sz_00", "ask_ct_00"]);
    }

    [Theory]
    [InlineData(Trades)]
    [InlineData(Tbbo)]
    [InlineData(Mbp1)]
    [InlineData(Mbp10)]
    [InlineData(Bbo1s)]
    [InlineData(Bbo1m)]
    public void A_series_is_the_last_column(string target)
    {
        new Rebuild(target, series: true).Names.Last().ShouldBe(SeriesColumn);
    }

    [Fact]
    public void A_trade_row_carries_the_trade()
    {
        var rebuild = new Rebuild(Trades).Feed(Event('T', 'A', 100.25m, 3, 0, sequence: 77));

        rebuild.Rows.ShouldHaveSingleItem().Date.ShouldBe(Date);
        rebuild.Cell(0, TsRecv).Number.ShouldBe((long)T0);
        rebuild.Cell(0, TsEvent).Number.ShouldBe((long)T0 + 5);
        rebuild.Cell(0, SymbolColumn).Text.ShouldBe(Symbol);
        rebuild.Cell(0, InstrumentId).Number.ShouldBe(Instrument);
        rebuild.Cell(0, Side).Text.ShouldBe("A");
        rebuild.Cell(0, Price).Price.ShouldBe(100.25m);
        rebuild.Cell(0, Size).Number.ShouldBe(3);
        rebuild.Cell(0, Sequence).Number.ShouldBe(77);
    }

    [Fact]
    public void Each_trade_is_a_row_and_nothing_else_is()
    {
        var rebuild = new Rebuild(Trades).Feed(
            Event('A', 'B', 100m, 5, 1),
            Event('T', 'A', 100m, 2, 0),
            Event('F', 'B', 100m, 2, 1),
            Event('C', 'B', 100m, 3, 1),
            Event('T', 'B', 101m, 1, 0));

        rebuild.Rows.Count.ShouldBe(2);
        rebuild.Cell(0, Price).Price.ShouldBe(100m);
        rebuild.Cell(1, Price).Price.ShouldBe(101m);
    }

    [Fact]
    public void A_trade_outside_what_was_asked_for_writes_nothing()
    {
        var rebuild = new Rebuild(Trades).FeedOutside(Event('T', 'A', 100m, 2, 0));

        rebuild.Rows.ShouldBeEmpty();
    }

    [Fact]
    public void A_trade_inside_a_snapshot_writes_nothing()
    {
        var rebuild = new Rebuild(Trades).Feed(Event('T', 'A', 100m, 2, 0, flags: Snapshot));

        rebuild.Rows.ShouldBeEmpty();
    }

    [Fact]
    public void Prices_go_through_the_function_given_and_the_series_is_named()
    {
        var rebuild = new Rebuild(Trades, series: true).Feed(Event('T', 'A', 100m, 2, 0), Date, p => p + 1);

        rebuild.Cell(0, Price).Price.ShouldBe(101m);
        rebuild.Cell(0, SeriesColumn).Text.ShouldBe(Series);
    }

    [Fact]
    public void Tbbo_shows_the_book_just_before_the_trade()
    {
        var rebuild = new Rebuild(Tbbo).Feed(
            Event('A', 'B', 100m, 5, 1),
            Event('A', 'B', 100m, 2, 2),
            Event('A', 'A', 101m, 4, 3),
            Event('T', 'A', 100m, 5, 0),
            Event('F', 'B', 100m, 5, 1));

        rebuild.Rows.Count.ShouldBe(1);
        ShouldHaveLevel(rebuild, 0, Bid, 0, 100m, 7, 2);
        ShouldHaveLevel(rebuild, 0, Ask, 0, 101m, 4, 1);
    }

    [Fact]
    public void Tbbo_of_an_empty_side_has_no_values()
    {
        var rebuild = new Rebuild(Tbbo).Feed(Event('A', 'B', 100m, 5, 1), Event('T', 'A', 100m, 1, 0));

        ShouldHaveLevel(rebuild, 0, Bid, 0, 100m, 5, 1);
        ShouldHaveNoLevel(rebuild, 0, Ask, 0);
    }

    [Fact]
    public void Tbbo_of_a_series_names_it_after_the_levels()
    {
        var rebuild = new Rebuild(Tbbo, series: true).Feed(Event('T', 'A', 100m, 1, 0));

        rebuild.Cell(0, SeriesColumn).Text.ShouldBe(Series);
    }

    [Fact]
    public void Mbp_1_writes_when_the_top_changes_and_not_behind_it()
    {
        var rebuild = new Rebuild(Mbp1).Feed(
            Event('A', 'B', 100m, 5, 1),
            Event('A', 'B', 99m, 5, 2),
            Event('A', 'A', 101m, 4, 3));

        rebuild.Rows.Count.ShouldBe(2);
        rebuild.Cell(0, Action).Text.ShouldBe("A");
        rebuild.Cell(1, Price).Price.ShouldBe(101m);
        ShouldHaveLevel(rebuild, 1, Bid, 0, 100m, 5, 1);
        ShouldHaveLevel(rebuild, 1, Ask, 0, 101m, 4, 1);
    }

    [Fact]
    public void Mbp_1_writes_every_trade_even_one_that_moves_nothing()
    {
        var rebuild = new Rebuild(Mbp1).Feed(Event('A', 'B', 100m, 5, 1), Event('T', 'A', 100m, 2, 0));

        rebuild.Rows.Count.ShouldBe(2);
        rebuild.Cell(1, Action).Text.ShouldBe("T");
        ShouldHaveLevel(rebuild, 1, Bid, 0, 100m, 5, 1);
    }

    [Fact]
    public void Mbp_writes_a_trade_that_is_not_the_last_record_of_its_event()
    {
        var rebuild = new Rebuild(Mbp1).Feed(
            Event('A', 'B', 100m, 5, 1),
            Event('T', 'A', 100m, 2, 0, flags: NoFlags),
            Event('T', 'A', 100m, 1, 0, flags: NoFlags),
            Event('F', 'B', 100m, 3, 1));

        rebuild.Rows.Count.ShouldBe(4);
        rebuild.Cell(1, Action).Text.ShouldBe("T");
        rebuild.Cell(2, Action).Text.ShouldBe("T");
        rebuild.Cell(3, Action).Text.ShouldBe("F");
        ShouldHaveLevel(rebuild, 3, Bid, 0, 100m, 2, 1);
    }

    [Fact]
    public void Mbp_waits_for_the_last_record_of_an_event()
    {
        var rebuild = new Rebuild(Mbp1).Feed(Event('A', 'B', 100m, 5, 1, flags: NoFlags));

        rebuild.Rows.ShouldBeEmpty();

        rebuild.Feed(Event('A', 'B', 100m, 2, 2));

        rebuild.Rows.Count.ShouldBe(1);
        ShouldHaveLevel(rebuild, 0, Bid, 0, 100m, 7, 2);
    }

    [Fact]
    public void Mbp_compares_the_whole_event_not_just_its_last_record()
    {
        var rebuild = new Rebuild(Mbp1).Feed(Event('A', 'B', 100m, 5, 1), Event('A', 'B', 99m, 5, 2));
        rebuild.Rows.Clear();

        rebuild.Feed(Event('C', 'B', 100m, 5, 1, flags: NoFlags), Event('A', 'B', 98m, 1, 3));

        rebuild.Rows.Count.ShouldBe(1);
        ShouldHaveLevel(rebuild, 0, Bid, 0, 99m, 5, 1);
    }

    [Fact]
    public void Mbp_writes_nothing_for_an_event_that_ends_where_it_began()
    {
        var rebuild = new Rebuild(Mbp1).Feed(Event('A', 'B', 100m, 5, 1));
        rebuild.Rows.Clear();

        rebuild.Feed(Event('C', 'B', 100m, 5, 1, flags: NoFlags), Event('A', 'B', 100m, 5, 2));

        rebuild.Rows.ShouldBeEmpty();
    }

    [Fact]
    public void A_snapshot_seeds_the_book_without_writing_rows()
    {
        var rebuild = new Rebuild(Mbp1).Feed(
            Event('R', 'N', null, 0, 0, flags: Snapshot),
            Event('A', 'B', 100m, 5, 1, flags: Snapshot),
            Event('A', 'A', 101m, 4, 2, flags: Snapshot | Last));

        rebuild.Rows.ShouldBeEmpty();

        rebuild.Feed(Event('A', 'B', 100m, 1, 3));

        ShouldHaveLevel(rebuild, 0, Bid, 0, 100m, 6, 2);
        ShouldHaveLevel(rebuild, 0, Ask, 0, 101m, 4, 1);
    }

    [Fact]
    public void A_snapshot_without_a_last_flag_does_not_leak_into_the_next_event()
    {
        var rebuild = new Rebuild(Mbp1).Feed(Event('A', 'B', 100m, 5, 1, flags: Snapshot));

        rebuild.Feed(Event('A', 'B', 99m, 1, 2));

        rebuild.Rows.ShouldBeEmpty();
    }

    [Fact]
    public void Events_outside_what_was_asked_for_still_move_the_book()
    {
        var rebuild = new Rebuild(Mbp1);

        rebuild.FeedOutside(Event('A', 'B', 100m, 5, 1));
        rebuild.Feed(Event('A', 'A', 101m, 4, 2));

        rebuild.Rows.Count.ShouldBe(1);
        ShouldHaveLevel(rebuild, 0, Bid, 0, 100m, 5, 1);
        ShouldHaveLevel(rebuild, 0, Ask, 0, 101m, 4, 1);
    }

    [Fact]
    public void A_level_with_no_orders_has_no_value()
    {
        var rebuild = new Rebuild(Mbp1).Feed(Event('A', 'B', 100m, 5, 1));

        ShouldHaveNoLevel(rebuild, 0, Ask, 0);
    }

    [Fact]
    public void A_cleared_book_writes_a_row_of_empty_levels()
    {
        var rebuild = new Rebuild(Mbp1).Feed(Event('A', 'B', 100m, 5, 1), Event('R', 'N', null, 0, 0));

        rebuild.Rows.Count.ShouldBe(2);
        ShouldHaveNoLevel(rebuild, 1, Bid, 0);
    }

    [Fact]
    public void Mbp_10_holds_ten_levels_and_ignores_the_eleventh()
    {
        var rebuild = new Rebuild(Mbp10);

        for (var level = 0u; level < 11; level++)
            rebuild.Feed(Event('A', 'B', 100m - level, 1 + level, 1 + level));

        rebuild.Rows.Count.ShouldBe(10);
        ShouldHaveLevel(rebuild, 9, Bid, 0, 100m, 1, 1);
        ShouldHaveLevel(rebuild, 9, Bid, 9, 91m, 10, 1);
        ShouldHaveNoLevel(rebuild, 9, Ask, 0);
    }

    [Fact]
    public void Mbp_10_of_a_series_names_it()
    {
        var rebuild = new Rebuild(Mbp10, series: true).Feed(Event('A', 'B', 100m, 1, 1));

        rebuild.Cell(0, SeriesColumn).Text.ShouldBe(Series);
    }

    [Fact]
    public void Each_instrument_has_its_own_book()
    {
        var rebuild = new Rebuild(Mbp1).Feed(
            Event('A', 'B', 100m, 5, 1),
            Event('A', 'B', 200m, 3, 1, instrument: OtherInstrument));

        ShouldHaveLevel(rebuild, 1, Bid, 0, 200m, 3, 1);
        rebuild.Cell(1, InstrumentId).Number.ShouldBe(OtherInstrument);
    }

    [Fact]
    public void Bbo_writes_the_book_at_the_end_of_the_interval_when_the_next_one_starts()
    {
        var rebuild = new Rebuild(Bbo1s).Feed(
            Event('A', 'B', 100m, 5, 1, T0 + Second / 5),
            Event('A', 'A', 101m, 4, 2, T0 + Second / 2));

        rebuild.Rows.ShouldBeEmpty();

        rebuild.Feed(Event('A', 'B', 100.5m, 1, 3, T0 + Second + 1));

        rebuild.Rows.ShouldHaveSingleItem().Date.ShouldBe(Date);
        rebuild.Cell(0, TsRecv).Number.ShouldBe((long)(T0 + Second));
        rebuild.Cell(0, SymbolColumn).Text.ShouldBe(Symbol);
        rebuild.Cell(0, InstrumentId).Number.ShouldBe(Instrument);
        ShouldHaveLevel(rebuild, 0, Bid, 0, 100m, 5, 1);
        ShouldHaveLevel(rebuild, 0, Ask, 0, 101m, 4, 1);
    }

    [Fact]
    public void Bbo_1m_closes_on_the_minute()
    {
        var rebuild = new Rebuild(Bbo1m).Feed(
            Event('A', 'B', 100m, 5, 1, T0 + Second),
            Event('A', 'B', 101m, 5, 2, T0 + Minute - 1));

        rebuild.Rows.ShouldBeEmpty();

        rebuild.Feed(Event('A', 'B', 102m, 5, 3, T0 + Minute));

        rebuild.Cell(0, TsRecv).Number.ShouldBe((long)(T0 + Minute));
        ShouldHaveLevel(rebuild, 0, Bid, 0, 101m, 5, 1);
    }

    [Fact]
    public void Bbo_carries_the_last_trade()
    {
        var rebuild = new Rebuild(Bbo1s).Feed(
            Event('T', 'A', 100m, 2, 0, T0),
            Event('T', 'B', 101m, 3, 0, T0 + 1),
            Event('A', 'B', 100m, 5, 1, T0 + Second));

        rebuild.Cell(0, Price).Price.ShouldBe(101m);
        rebuild.Cell(0, Size).Number.ShouldBe(3);
    }

    [Fact]
    public void Bbo_before_any_trade_has_no_price_or_size()
    {
        var rebuild = new Rebuild(Bbo1s).Feed(
            Event('A', 'B', 100m, 5, 1, T0),
            Event('A', 'B', 100m, 5, 2, T0 + Second));

        rebuild.Cell(0, Price).Price.ShouldBeNull();
        rebuild.Cell(0, Size).IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void Bbo_prices_go_through_the_function_given_and_the_series_is_named()
    {
        var rebuild = new Rebuild(Bbo1s, series: true);

        rebuild.Feed(Event('A', 'B', 100m, 5, 1, T0), Date, p => p * 2);
        rebuild.Feed(Event('A', 'B', 100m, 5, 2, T0 + Second), Date, p => p * 2);

        rebuild.Cell(0, BidPrice).Price.ShouldBe(200m);
        rebuild.Cell(0, SeriesColumn).Text.ShouldBe(Series);
    }

    [Fact]
    public void Bbo_skips_an_interval_in_which_nothing_happened_inside_the_hours()
    {
        var rebuild = new Rebuild(Bbo1s);

        rebuild.FeedOutside(Event('T', 'A', 100m, 2, 0, T0));
        rebuild.FeedOutside(Event('T', 'A', 100m, 2, 0, T0 + Second));

        rebuild.Rows.ShouldBeEmpty();
    }

    [Fact]
    public void Bbo_writes_nothing_for_an_interval_that_closed_outside_the_hours()
    {
        var rebuild = new Rebuild(Bbo1s);

        rebuild.FeedOutside(Event('A', 'B', 100m, 5, 1, T0));
        rebuild.Feed(Event('A', 'B', 100m, 5, 2, T0 + Second), Date);

        rebuild.Rows.ShouldBeEmpty();
    }

    [Fact]
    public void Bbo_writes_no_row_for_a_snapshot_being_replayed()
    {
        var rebuild = new Rebuild(Bbo1s).Feed(
            Event('A', 'B', 100m, 5, 1, T0, Snapshot),
            Event('A', 'B', 100m, 5, 2, T0 + Second, Snapshot),
            Event('A', 'B', 100m, 5, 3, T0 + 2 * Second, Snapshot | Last));

        rebuild.Rows.ShouldBeEmpty();
    }

    [Fact]
    public void Bbo_still_closes_the_interval_a_snapshot_interrupts()
    {
        var rebuild = new Rebuild(Bbo1s).Feed(
            Event('A', 'B', 100m, 5, 1, T0),
            Event('R', 'N', null, 0, 0, T0 + Second, Snapshot),
            Event('A', 'B', 99m, 5, 2, T0 + 2 * Second, Snapshot),
            Event('A', 'B', 98m, 5, 3, T0 + 3 * Second, Snapshot | Last));

        rebuild.Rows.Count.ShouldBe(1);
        rebuild.Cell(0, TsRecv).Number.ShouldBe((long)(T0 + Second));
        ShouldHaveLevel(rebuild, 0, Bid, 0, 100m, 5, 1);
    }

    [Fact]
    public void Bbo_writes_the_book_a_snapshot_left_once_the_snapshot_is_over()
    {
        var rebuild = new Rebuild(Bbo1s).Feed(
            Event('A', 'B', 99m, 5, 2, T0, Snapshot | Last),
            Event('A', 'B', 98m, 5, 3, T0 + Second));

        rebuild.Rows.Count.ShouldBe(1);
        ShouldHaveLevel(rebuild, 0, Bid, 0, 99m, 5, 1);
    }

    [Fact]
    public void Bbo_has_no_row_for_an_interval_with_no_events()
    {
        var rebuild = new Rebuild(Bbo1s).Feed(
            Event('A', 'B', 100m, 5, 1, T0),
            Event('A', 'B', 101m, 5, 2, T0 + 5 * Second),
            Event('A', 'B', 102m, 5, 3, T0 + 6 * Second));

        rebuild.Rows.Count.ShouldBe(2);
        rebuild.Cell(0, TsRecv).Number.ShouldBe((long)(T0 + Second));
        rebuild.Cell(1, TsRecv).Number.ShouldBe((long)(T0 + 6 * Second));
    }

    [Fact]
    public void Bbo_files_a_row_under_the_trade_date_of_its_interval()
    {
        var rebuild = new Rebuild(Bbo1s);

        rebuild.Feed(Event('A', 'B', 100m, 5, 1, T0), Date);
        rebuild.Feed(Event('A', 'B', 100m, 5, 2, T0 + Second), NextDate);

        rebuild.Rows.ShouldHaveSingleItem().Date.ShouldBe(Date);
    }

    [Fact]
    public void Flushing_writes_the_last_interval_once()
    {
        var rebuild = new Rebuild(Bbo1s).Feed(Event('A', 'B', 100m, 5, 1, T0));

        rebuild.Flush();
        rebuild.Flush();

        rebuild.Rows.Count.ShouldBe(1);
        rebuild.Cell(0, TsRecv).Number.ShouldBe((long)(T0 + Second));
    }

    [Fact]
    public void Flushing_keeps_the_instrument_series_and_units_the_row_was_built_with()
    {
        var rebuild = new Rebuild(Bbo1s, series: true);

        rebuild.Feed(Event('A', 'B', 100m, 5, 1, T0, instrument: OtherInstrument), Date, p => p * 2);
        rebuild.Flush();

        rebuild.Cell(0, InstrumentId).Number.ShouldBe(OtherInstrument);
        rebuild.Cell(0, SymbolColumn).Text.ShouldBe(Symbol);
        rebuild.Cell(0, SeriesColumn).Text.ShouldBe(Series);
        rebuild.Cell(0, BidPrice).Price.ShouldBe(200m);
    }

    [Fact]
    public void Flushing_before_a_date_leaves_that_dates_interval_open()
    {
        var rebuild = new Rebuild(Bbo1s).Feed(Event('A', 'B', 100m, 5, 1, T0));

        rebuild.Flush(Date);
        rebuild.Rows.ShouldBeEmpty();

        rebuild.Flush(NextDate);
        rebuild.Rows.Count.ShouldBe(1);
    }

    [Fact]
    public void A_flushed_interval_is_not_written_again_when_the_next_one_starts()
    {
        var rebuild = new Rebuild(Bbo1s).Feed(Event('A', 'B', 100m, 5, 1, T0));

        rebuild.Flush();
        rebuild.Feed(Event('A', 'B', 100m, 1, 2, T0 + Second));

        rebuild.Rows.Count.ShouldBe(1);
    }

    [Fact]
    public void Flushing_outside_the_hours_has_nothing_to_write()
    {
        var rebuild = new Rebuild(Bbo1s);

        rebuild.FeedOutside(Event('A', 'B', 100m, 5, 1, T0));
        rebuild.Flush();

        rebuild.Rows.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(Trades)]
    [InlineData(Tbbo)]
    [InlineData(Mbp1)]
    [InlineData(Mbp10)]
    public void Only_intervals_are_flushed(string target)
    {
        var rebuild = new Rebuild(target).Feed(Event('T', 'A', 100m, 2, 0));
        rebuild.Rows.Clear();

        rebuild.Flush();

        rebuild.Rows.ShouldBeEmpty();
    }
}
