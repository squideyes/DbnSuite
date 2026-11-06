using DbnClient.Dbn;
using DbnClient.Export;
using DbnClient.Tape;
using Shouldly;

namespace DbnSync.UnitTests.TapeFormatTests;

public sealed class TickBookTests
{
    private const int FlowEvents = 20_000;
    private const decimal TickSize = 0.25m;
    private const int Mid = 30_000;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void GeneratedFlowsAgreeWithOrderBook(int seed)
    {
        var random = new Random(seed);
        var mine = new TickBook();
        var theirs = new OrderBook();
        var live = new List<ulong>();
        var nextId = 1UL;

        for (var i = 0; i < FlowEvents; i++)
        {
            var (action, id, price, size, side) = NextEvent(random, live, ref nextId);

            mine.Apply(action, id, price, size, side);
            theirs.Apply(Record(action, id, price, size, side));

            AgreeOnTop(mine, theirs);
        }
    }

    [Fact]
    public void AnEmptyBookHasNoSizes()
    {
        var book = new TickBook();

        var top = book.Top();

        top.BidSize.ShouldBe(0u);
        top.AskSize.ShouldBe(0u);
    }

    [Fact]
    public void AClearEmptiesTheBook()
    {
        var book = new TickBook();

        book.Apply('A', 1, Mid, 5, 'B');
        book.Apply('A', 2, Mid + 1, 5, 'A');
        book.Apply('R', 0, null, 0, 'N');

        book.Top().ShouldBe(default);
    }

    [Fact]
    public void AClearedBookTakesOrdersAgain()
    {
        var book = new TickBook();

        book.Apply('A', 1, Mid, 5, 'A');
        book.Apply('R', 0, null, 0, 'N');
        book.Apply('A', 2, Mid + 3, 7, 'A');

        book.Top().ShouldBe(new BookTop(0, 0, Mid + 3, 7));
    }

    [Fact]
    public void BidsCanStandAtAndBelowZero()
    {
        var book = new TickBook();

        book.Apply('A', 1, -8_770, 2, 'B');
        book.Apply('A', 2, 0, 1, 'B');

        book.Top().BidPrice.ShouldBe(0);

        book.Apply('C', 2, 0, 1, 'B');

        book.Top().ShouldBe(new BookTop(-8_770, 2, 0, 0));
    }

    [Fact]
    public void AnOrderFarFromTheBookIsKept()
    {
        var book = new TickBook();

        book.Apply('A', 1, Mid, 3, 'B');
        book.Apply('A', 2, Mid + 1, 4, 'A');
        book.Apply('A', 3, 3_103_900, 1, 'A');

        book.Top().ShouldBe(new BookTop(Mid, 3, Mid + 1, 4));

        book.Apply('C', 2, 0, 4, 'A');

        book.Top().ShouldBe(new BookTop(Mid, 3, 3_103_900, 1));
    }

    [Fact]
    public void AnOfferAtAThousandTimesTheMarketIsKept()
    {
        var book = new TickBook();

        book.Apply('A', 1, Mid, 3, 'B');
        book.Apply('A', 2, Mid + 1, 4, 'A');
        book.Apply('A', 3, 31_397_600, 1, 'A');

        book.Top().ShouldBe(new BookTop(Mid, 3, Mid + 1, 4));

        book.Apply('C', 2, 0, 4, 'A');

        book.Top().ShouldBe(new BookTop(Mid, 3, 31_397_600, 1));

        book.Apply('C', 3, 0, 1, 'A');

        book.Top().ShouldBe(new BookTop(Mid, 3, 0, 0));
    }

    [Fact]
    public void AFarBidAboveTheWindowIsTheBest()
    {
        var book = new TickBook();

        book.Apply('A', 1, Mid, 3, 'B');
        book.Apply('A', 2, int.MaxValue, 1, 'B');

        book.Top().BidPrice.ShouldBe(int.MaxValue);
    }

    [Fact]
    public void AFarOfferBelowTheWindowIsTheBest()
    {
        var book = new TickBook();

        book.Apply('A', 1, Mid, 3, 'A');
        book.Apply('A', 2, int.MinValue, 1, 'A');

        book.Top().AskPrice.ShouldBe(int.MinValue);
    }

    [Fact]
    public void AClearEmptiesFarLevelsToo()
    {
        var book = new TickBook();

        book.Apply('A', 1, Mid, 3, 'B');
        book.Apply('A', 2, 31_397_600, 1, 'A');
        book.Apply('R', 0, null, 0, 'N');

        book.Top().ShouldBe(default);
    }

    [Fact]
    public void AModifyWithNoPriceChangesNothing()
    {
        var book = new TickBook();

        book.Apply('A', 1, Mid, 3, 'B');
        book.Apply('M', 1, null, 9, 'B');

        book.Top().BidSize.ShouldBe(3u);
    }

    [Fact]
    public void TradesAndNoOpsLeaveTheBookAlone()
    {
        var book = new TickBook();

        book.Apply('A', 1, Mid, 3, 'B');
        book.Apply('T', 2, Mid, 1, 'A');
        book.Apply('N', 0, null, 0, 'N');

        book.Top().BidSize.ShouldBe(3u);
    }

    private static (char Action, ulong Id, int? Price, uint Size, char Side) NextEvent(Random random, List<ulong> live, ref ulong nextId)
    {
        var side = random.Next(2) == 0 ? 'B' : 'A';
        var price = Mid + (side == 'B' ? -random.Next(1, 30) : random.Next(1, 30));

        // A rare stray far from the book, and a rare price below zero.
        if (random.Next(200) == 0)
            price = random.Next(2) == 0 ? -random.Next(1, 2_000) : Mid + random.Next(100_000, 900_000);

        var pick = random.Next(100);

        if (live.Count == 0 || pick < 45)
        {
            live.Add(nextId);

            return ('A', nextId++, price, (uint)random.Next(1, 40), side);
        }

        var index = random.Next(live.Count);
        var id = live[index];

        if (pick < 60)
            return ('M', id, price, (uint)random.Next(1, 40), side);

        if (pick < 70)
            return ('T', id, price, (uint)random.Next(1, 5), side);

        if (pick < 72)
        {
            live.Clear();

            return ('R', 0, null, 0, 'N');
        }

        if (pick < 75)
            return ('A', nextId++, null, 3, side);

        live.RemoveAt(index);

        return (pick < 90 ? 'C' : 'F', id, null, (uint)random.Next(1, 60), side);
    }

    private static MboRecord Record(char action, ulong id, int? price, uint size, char side) =>
        new(0, 0, 0, id, price * TickSize, size, 0, action, side, 0);

    private static void AgreeOnTop(TickBook mine, OrderBook theirs)
    {
        var top = mine.Top();
        var bid = theirs.Best('B');
        var ask = theirs.Best('A');

        top.BidSize.ShouldBe((uint)(bid?.Size ?? 0));
        top.AskSize.ShouldBe((uint)(ask?.Size ?? 0));

        if (bid is { } b)
            (top.BidPrice * TickSize).ShouldBe(b.Price);

        if (ask is { } a)
            (top.AskPrice * TickSize).ShouldBe(a.Price);
    }
}
