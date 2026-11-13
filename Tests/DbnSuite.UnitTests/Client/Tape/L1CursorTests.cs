using DbnSuite.Client.Tape;
using Shouldly;

namespace DbnSuite.UnitTests.TapeFormatTests;

public sealed class L1CursorTests
{
    private const int RandomEvents = 10_000;
    private const int Bid = 30_842;
    private const int Ask = 30_843;
    private const uint BidLots = 40;
    private const uint AskLots = 25;
    private const byte Trading = 7;
    private const byte Halt = 8;
    private const int QuoteMask = 0x3C;
    private const long Later = 800;

    public readonly record struct Step(
        L1Kind Kind,
        long Time,
        int BidPrice,
        uint BidSize,
        int AskPrice,
        uint AskSize,
        int TradePrice,
        uint TradeSize,
        TradeSide Side,
        byte State);

    [Fact]
    public async Task EveryQuoteControlByteRoundTrips()
    {
        for (var bits = 0; bits < 64; bits += 2)
        {
            if ((bits & QuoteMask) == 0)
                continue;

            var time = TapeFixture.OpenMicros + ((bits & 2) != 0 ? Later : 0);
            var bid = Bid - ((bits & 4) != 0 ? 1 : 0);
            var ask = Ask + ((bits & 8) != 0 ? 2 : 0);
            var bidLots = BidLots + ((bits & 16) != 0 ? 3u : 0);
            var askLots = AskLots - ((bits & 32) != 0 ? 4u : 0);
            var writer = Opened();
            var before = await BodyLengthAsync(Opened());

            writer.Quote(time, bid, bidLots, ask, askLots).ShouldBeTrue();

            var tape = await TapeFixture.RoundTripAsync(writer);
            var last = Read(tape)[^1];

            tape.Body[before].ShouldBe((byte)bits);
            last.Kind.ShouldBe(L1Kind.Quote);
            last.Time.ShouldBe(time);
            last.BidPrice.ShouldBe(bid);
            last.BidSize.ShouldBe(bidLots);
            last.AskPrice.ShouldBe(ask);
            last.AskSize.ShouldBe(askLots);
        }
    }

    [Fact]
    public async Task EveryTradeControlByteRoundTrips()
    {
        for (var control = 1; control <= byte.MaxValue; control += 2)
        {
            var side = (control >> 2) & 3;
            var implied = (control & 16) != 0;

            if (side == 3 || (implied && side == 0))
                continue;

            var time = TapeFixture.OpenMicros + ((control & 2) != 0 ? Later : 0);
            var price = implied ? (side == 1 ? Ask : Bid) : Bid - 7;
            var inline = control >> 5;
            var size = inline == 0 ? 100u : (uint)inline;
            var writer = Opened();
            var before = await BodyLengthAsync(Opened());

            writer.Trade(time, price, size, (TradeSide)side).ShouldBeTrue();

            var tape = await TapeFixture.RoundTripAsync(writer);
            var last = Read(tape)[^1];

            tape.Body[before].ShouldBe((byte)control);
            last.Kind.ShouldBe(L1Kind.Trade);
            last.Time.ShouldBe(time);
            last.TradePrice.ShouldBe(price);
            last.TradeSize.ShouldBe(size);
            last.Side.ShouldBe((TradeSide)side);
        }
    }

    [Theory]
    [InlineData(0, 0x00)]
    [InlineData(Later, 0x02)]
    public async Task BothStateControlBytesRoundTrip(long gap, byte control)
    {
        var writer = Opened();
        var before = await BodyLengthAsync(Opened());

        writer.State(TapeFixture.OpenMicros + gap, Halt);

        var tape = await TapeFixture.RoundTripAsync(writer);
        var last = Read(tape)[^1];

        tape.Body[before].ShouldBe(control);
        last.Kind.ShouldBe(L1Kind.State);
        last.State.ShouldBe(Halt);
        last.Time.ShouldBe(TapeFixture.OpenMicros + gap);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task SeededStreamsRoundTrip(int seed)
    {
        var writer = new L1Writer(TapeFixture.Info());

        var steps = FillRandom(writer, seed, RandomEvents);
        var tape = await TapeFixture.RoundTripAsync(writer);

        Read(tape).ShouldBe(steps);
    }

    [Fact]
    public async Task AnEmptySideKeepsItsPrice()
    {
        var writer = Opened();

        writer.Quote(TapeFixture.OpenMicros, 0, 0, Ask, AskLots);

        var tape = await TapeFixture.RoundTripAsync(writer);
        var last = Read(tape)[^1];

        last.BidSize.ShouldBe(0u);
        last.BidPrice.ShouldBe(Bid);
    }

    [Fact]
    public async Task AnUnchangedQuoteWritesNothing()
    {
        var writer = Opened();
        var before = writer.EventCount;

        writer.Quote(TapeFixture.OpenMicros + Later, Bid, BidLots, Ask, AskLots).ShouldBeTrue();
        await TapeFixture.ToBytesAsync(writer);

        writer.EventCount.ShouldBe(before);
    }

    [Fact]
    public async Task AnUnchangedStateWritesNothing()
    {
        var writer = Opened();
        var before = writer.EventCount;

        writer.State(TapeFixture.OpenMicros + Later, Trading).ShouldBeTrue();
        await TapeFixture.ToBytesAsync(writer);

        writer.EventCount.ShouldBe(before);
    }

    [Fact]
    public async Task ABookCanStandBelowZero()
    {
        var writer = new L1Writer(TapeFixture.Info(basePrice: 0));

        writer.Quote(TapeFixture.OpenMicros, -300, 5, -299, 6);
        writer.Trade(TapeFixture.OpenMicros, -299, 2, TradeSide.Buyer);

        var tape = await TapeFixture.RoundTripAsync(writer);
        var last = Read(tape)[^1];

        last.BidPrice.ShouldBe(-300);
        last.TradePrice.ShouldBe(-299);
    }

    [Fact]
    public async Task APriceOfZeroTicksIsLegal()
    {
        var writer = new L1Writer(TapeFixture.Info(basePrice: 5));

        writer.Quote(TapeFixture.OpenMicros, 0, 5, 1, 6);

        var tape = await TapeFixture.RoundTripAsync(writer);
        var last = Read(tape)[^1];

        last.BidPrice.ShouldBe(0);
        last.BidSize.ShouldBe(5u);
    }

    [Theory]
    [InlineData("44")]
    [InlineData("84")]
    [InlineData("C4")]
    [InlineData("40")]
    [InlineData("80")]
    public void AReservedQuoteBitThrows(string hex) => ShouldThrowOn(hex);

    [Theory]
    [InlineData("0D")]
    [InlineData("11")]
    [InlineData("1D")]
    public void ATradeWithABadSideThrows(string hex) => ShouldThrowOn(hex);

    [Fact]
    public void AStateWithNoActionThrows() => ShouldThrowOn("0000");

    [Fact]
    public void ASizeBelowZeroThrows() => ShouldThrowOn("1001");

    [Fact]
    public void RandomBytesNeverEscapeTheBody()
    {
        var random = new Random(1);
        var opening = RthMark.Opening(TapeKind.L1, 0, 0);

        for (var round = 0; round < 2_000; round++)
        {
            var body = new byte[random.Next(1, 48)];

            random.NextBytes(body);

            var ended = Walk(body, opening);

            (ended is null or InvalidDataException).ShouldBeTrue(ended?.ToString());
        }
    }

    internal static List<Step> Read(Tape tape)
    {
        var steps = new List<Step>();
        var cursor = tape.L1Cursor();

        while (cursor.MoveNext())
            steps.Add(Snapshot(cursor));

        return steps;
    }

    internal static Step Snapshot(in L1Cursor cursor) => new(
        cursor.Kind,
        cursor.Time,
        cursor.BidPrice,
        cursor.BidSize,
        cursor.AskPrice,
        cursor.AskSize,
        cursor.TradePrice,
        cursor.TradeSize,
        cursor.Side,
        cursor.State);

    /// <summary>Writes a random stream, and works out on its own what a reader must see.</summary>
    internal static List<Step> FillRandom(L1Writer writer, int seed, int count)
    {
        var random = new Random(seed);
        var steps = new List<Step>(count);
        var now = new Step(L1Kind.Quote, TapeFixture.OpenMicros, TapeFixture.BasePrice, 0, TapeFixture.BasePrice, 0, TapeFixture.BasePrice, 0, TradeSide.None, 0);
        byte[] actions = [1, 6, 7, 8, 12, 15];

        for (var i = 0; i < count; i++)
        {
            var time = now.Time + (random.Next(2) == 0 ? 0 : random.Next(1, 500_000));
            var pick = random.Next(100);

            if (pick < 75)
            {
                var bidLots = NextSize(random, now.BidSize);
                var askLots = NextSize(random, now.AskSize);
                var bid = bidLots == 0 ? now.BidPrice : now.BidPrice + NextMove(random);
                var ask = askLots == 0 ? now.AskPrice : now.AskPrice + NextMove(random);
                var changed = bid != now.BidPrice || ask != now.AskPrice || bidLots != now.BidSize || askLots != now.AskSize;

                writer.Quote(time, bid, bidLots, ask, askLots).ShouldBeTrue();

                if (changed)
                {
                    now = now with { Kind = L1Kind.Quote, Time = time, BidPrice = bid, BidSize = bidLots, AskPrice = ask, AskSize = askLots };
                    steps.Add(now);
                }
            }
            else if (pick < 97)
            {
                var side = (TradeSide)random.Next(0, 3);

                var price = random.Next(2) == 0
                    ? side == TradeSide.Seller ? now.BidPrice : now.AskPrice
                    : now.TradePrice + NextMove(random);

                var size = random.Next(4) == 0 ? (uint)random.Next(8, 3_000) : (uint)random.Next(1, 8);

                writer.Trade(time, price, size, side).ShouldBeTrue();

                now = now with { Kind = L1Kind.Trade, Time = time, TradePrice = price, TradeSize = size, Side = side };
                steps.Add(now);
            }
            else
            {
                var action = actions[random.Next(actions.Length)];

                writer.State(time, action).ShouldBeTrue();

                if (action != now.State)
                {
                    now = now with { Kind = L1Kind.State, Time = time, State = action };
                    steps.Add(now);
                }
            }
        }

        return steps;
    }

    private static int NextMove(Random random) => random.Next(3) == 0 ? random.Next(-3, 4) : 0;

    private static uint NextSize(Random random, uint size) => random.Next(10) switch
    {
        0 => 0,
        1 or 2 => (uint)random.Next(1, 2_000),
        3 or 4 or 5 => (uint)Math.Max(0, size + random.Next(-4, 5)),
        _ => size
    };

    private static L1Writer Opened()
    {
        var writer = new L1Writer(TapeFixture.Info());

        writer.State(TapeFixture.OpenMicros, Trading);
        writer.Quote(TapeFixture.OpenMicros, Bid, BidLots, Ask, AskLots);

        return writer;
    }

    private static async Task<int> BodyLengthAsync(L1Writer writer) =>
        (await TapeFixture.ToBytesAsync(writer)).Length - TapeHeader.Size;

    private static void ShouldThrowOn(string hex)
    {
        var body = TapeFixture.Bytes(hex);
        var opening = RthMark.Opening(TapeKind.L1, 0, 0);

        Walk(body, opening).ShouldBeOfType<InvalidDataException>();
    }

    private static Exception? Walk(byte[] body, RthMark opening)
    {
        try
        {
            var cursor = new L1Cursor(body, opening, body.Length, body.Length);

            while (cursor.MoveNext())
            {
            }

            return null;
        }
        catch (Exception problem)
        {
            return problem;
        }
    }
}
