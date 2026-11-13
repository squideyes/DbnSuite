using DbnSuite.Client.Dbn;
using DbnSuite.Client.Export;
using Shouldly;

namespace DbnSuite.UnitTests.ClientTests;

public sealed class OrderBookTests
{
    private const uint Instrument = 42;

    private static MboRecord Event(char action, char side, decimal price, uint size, ulong order) =>
        new(1, Instrument, 0, order, price, size, MboRecord.LastFlag, action, side, 0);

    [Fact]
    public void Adds_build_levels_with_their_sizes_and_counts()
    {
        var book = new OrderBook();

        book.Apply(Event('A', 'B', 100m, 5, 1));
        book.Apply(Event('A', 'B', 100m, 3, 2));
        book.Apply(Event('A', 'B', 99.75m, 7, 3));
        book.Apply(Event('A', 'A', 100.25m, 4, 4));

        book.Side('B', 10).ShouldBe([new Level(100m, 8, 2), new Level(99.75m, 7, 1)]);
        book.Best('A').ShouldBe(new Level(100.25m, 4, 1));
    }

    [Fact]
    public void A_partial_fill_shrinks_an_order_and_a_full_one_removes_it()
    {
        var book = new OrderBook();
        book.Apply(Event('A', 'A', 100.25m, 4, 1));

        book.Apply(Event('F', 'A', 100.25m, 1, 1));
        book.Best('A').ShouldBe(new Level(100.25m, 3, 1));

        book.Apply(Event('F', 'A', 100.25m, 3, 1));
        book.Best('A').ShouldBeNull();
    }

    [Fact]
    public void A_modify_moves_the_order()
    {
        var book = new OrderBook();
        book.Apply(Event('A', 'B', 100m, 5, 1));

        book.Apply(Event('M', 'B', 99.5m, 2, 1));

        book.Side('B', 10).ShouldBe([new Level(99.5m, 2, 1)]);
    }

    [Fact]
    public void A_trade_leaves_the_book_alone()
    {
        var book = new OrderBook();
        book.Apply(Event('A', 'B', 100m, 5, 1));

        book.Apply(Event('T', 'A', 100m, 5, 0));

        book.Best('B').ShouldBe(new Level(100m, 5, 1));
    }

    [Fact]
    public void A_clear_empties_the_book()
    {
        var book = new OrderBook();
        book.Apply(Event('A', 'B', 100m, 5, 1));

        book.Apply(Event('R', 'N', 0m, 0, 0));

        book.Best('B').ShouldBeNull();
    }

    [Fact]
    public void Asks_are_best_lowest_first_and_bids_best_highest_first()
    {
        var book = new OrderBook();
        book.Apply(Event('A', 'A', 101m, 1, 1));
        book.Apply(Event('A', 'A', 100.5m, 2, 2));
        book.Apply(Event('A', 'B', 99m, 3, 3));
        book.Apply(Event('A', 'B', 99.5m, 4, 4));

        book.Side('A', 10).Select(l => l.Price).ShouldBe([100.5m, 101m]);
        book.Side('B', 10).Select(l => l.Price).ShouldBe([99.5m, 99m]);
    }

    [Fact]
    public void A_side_gives_no_more_levels_than_asked_for()
    {
        var book = new OrderBook();
        book.Apply(Event('A', 'B', 100m, 1, 1));
        book.Apply(Event('A', 'B', 99m, 1, 2));

        book.Side('B', 1).ShouldBe([new Level(100m, 1, 1)]);
    }

    [Fact]
    public void A_cancel_takes_part_of_an_order_or_all_of_it()
    {
        var book = new OrderBook();
        book.Apply(Event('A', 'B', 100m, 5, 1));

        book.Apply(Event('C', 'B', 100m, 2, 1));
        book.Best('B').ShouldBe(new Level(100m, 3, 1));

        book.Apply(Event('C', 'B', 100m, 3, 1));
        book.Best('B').ShouldBeNull();
    }

    [Fact]
    public void A_cancel_of_more_than_is_left_removes_the_order()
    {
        var book = new OrderBook();
        book.Apply(Event('A', 'B', 100m, 5, 1));

        book.Apply(Event('C', 'B', 100m, 9, 1));

        book.Best('B').ShouldBeNull();
    }

    [Fact]
    public void A_cancel_of_one_order_leaves_the_others_at_its_level()
    {
        var book = new OrderBook();
        book.Apply(Event('A', 'B', 100m, 5, 1));
        book.Apply(Event('A', 'B', 100m, 3, 2));

        book.Apply(Event('C', 'B', 100m, 5, 1));

        book.Best('B').ShouldBe(new Level(100m, 3, 1));
    }

    [Fact]
    public void An_event_for_an_order_it_never_saw_changes_nothing()
    {
        var book = new OrderBook();
        book.Apply(Event('A', 'B', 100m, 5, 1));

        book.Apply(Event('C', 'B', 100m, 5, 99));
        book.Apply(Event('F', 'B', 100m, 5, 99));

        book.Best('B').ShouldBe(new Level(100m, 5, 1));
    }

    [Fact]
    public void An_add_for_an_order_already_there_replaces_it()
    {
        var book = new OrderBook();
        book.Apply(Event('A', 'B', 100m, 5, 1));

        book.Apply(Event('A', 'B', 99m, 2, 1));

        book.Side('B', 10).ShouldBe([new Level(99m, 2, 1)]);
    }

    [Fact]
    public void A_modify_can_change_the_size_where_it_stands()
    {
        var book = new OrderBook();
        book.Apply(Event('A', 'A', 101m, 5, 1));

        book.Apply(Event('M', 'A', 101m, 7, 1));

        book.Best('A').ShouldBe(new Level(101m, 7, 1));
    }

    [Fact]
    public void A_modify_of_an_order_it_never_saw_adds_it()
    {
        var book = new OrderBook();

        book.Apply(Event('M', 'B', 100m, 2, 1));

        book.Best('B').ShouldBe(new Level(100m, 2, 1));
    }

    [Theory]
    [InlineData('A', 'N')]
    [InlineData('M', 'N')]
    public void An_order_on_no_side_rests_nowhere(char action, char side)
    {
        var book = new OrderBook();

        book.Apply(Event(action, side, 100m, 5, 1));

        book.Best('B').ShouldBeNull();
        book.Best('A').ShouldBeNull();
    }

    [Theory]
    [InlineData('A')]
    [InlineData('M')]
    public void An_order_with_no_price_rests_nowhere(char action)
    {
        var book = new OrderBook();

        book.Apply(new MboRecord(1, Instrument, 0, 1, null, 5, MboRecord.LastFlag, action, 'B', 0));

        book.Best('B').ShouldBeNull();
    }

    [Fact]
    public void Mbp_1_rows_follow_changes_at_the_top_and_trades()
    {
        var transform = MboTransform.Create("mbp-1", series: false).Value;
        var rows = new List<Cell[]>();
        var date = new DateOnly(2026, 10, 28);

        void Feed(MboRecord m) => transform.Process(m, date, "ESZ26", null, p => p, (_, row) => rows.Add(row));

        Feed(Event('A', 'B', 100m, 5, 1));
        Feed(Event('A', 'B', 99m, 5, 2));
        Feed(Event('A', 'A', 101m, 4, 3));
        Feed(Event('T', 'B', 101m, 1, 0));

        rows.Count.ShouldBe(3);
        var bidPrice = transform.Columns.Select(c => c.Name).ToList().IndexOf("bid_px_00");
        rows[^1][bidPrice].Price.ShouldBe(100m);
    }
}
