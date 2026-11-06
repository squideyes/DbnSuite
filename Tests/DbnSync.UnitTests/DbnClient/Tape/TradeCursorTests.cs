using DbnClient.Tape;
using Shouldly;

namespace DbnSync.UnitTests.TapeFormatTests;

public sealed class TradeCursorTests
{
    private const int RandomTrades = 5_000;
    private const int SideMask = 3;

    public readonly record struct Print(long Time, int Price, uint Size, TradeSide Side);

    [Fact]
    public async Task EveryControlByteRoundTrips()
    {
        for (var control = 0; control <= byte.MaxValue; control++)
        {
            if ((control & SideMask) == SideMask)
                continue;

            var print = PrintFor((byte)control);
            var writer = new TradeWriter(TapeFixture.Info());

            writer.Add(print.Time, print.Price, print.Size, print.Side);

            var tape = await TapeFixture.RoundTripAsync(writer);

            tape.Body[0].ShouldBe((byte)control);
            Read(tape).ShouldBe([print]);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task SeededStreamsRoundTrip(int seed)
    {
        var prints = RandomStream(seed);
        var writer = new TradeWriter(TapeFixture.Info());

        foreach (var print in prints)
            writer.Add(print.Time, print.Price, print.Size, print.Side).ShouldBeTrue();

        var tape = await TapeFixture.RoundTripAsync(writer);

        Read(tape).ShouldBe(prints);
    }

    [Fact]
    public async Task TheLargestJumpRoundTrips()
    {
        Print[] prints =
        [
            new(TapeFixture.OpenMicros, int.MinValue, uint.MaxValue, TradeSide.Buyer),
            new(TapeFixture.OpenMicros, int.MaxValue, 0, TradeSide.Seller)
        ];

        var writer = new TradeWriter(TapeFixture.Info(basePrice: int.MaxValue));

        foreach (var print in prints)
            writer.Add(print.Time, print.Price, print.Size, print.Side);

        var tape = await TapeFixture.RoundTripAsync(writer);

        Read(tape).ShouldBe(prints);
    }

    [Theory]
    [InlineData(0x13)]
    [InlineData(0xF3)]
    public void TheReservedSideThrows(byte control)
    {
        byte[] body = [control];
        var opening = RthMark.Opening(TapeKind.Trade, 0, 0);

        Should.Throw<InvalidDataException>(() =>
        {
            var cursor = new TradeCursor(body, opening, 1, body.Length);

            cursor.MoveNext();
        });
    }

    internal static List<Print> Read(Tape tape)
    {
        var prints = new List<Print>();
        var cursor = tape.TradeCursor();

        while (cursor.MoveNext())
            prints.Add(new Print(cursor.Time, cursor.Price, cursor.Size, cursor.Side));

        return prints;
    }

    internal static List<Print> RandomStream(int seed, int count = RandomTrades)
    {
        var random = new Random(seed);
        var prints = new List<Print>(count);
        var time = TapeFixture.OpenMicros;
        var price = TapeFixture.BasePrice;

        for (var i = 0; i < count; i++)
        {
            time += random.Next(2) == 0 ? 0 : random.Next(1, 2_000_000);
            price += random.Next(2) == 0 ? 0 : random.Next(-4, 5);

            var size = random.Next(5) == 0 ? (uint)random.Next(16, 5_000) : (uint)random.Next(1, 16);

            prints.Add(new Print(time, price, size, (TradeSide)random.Next(0, 3)));
        }

        return prints;
    }

    private static Print PrintFor(byte control)
    {
        var time = TapeFixture.OpenMicros + ((control & 4) != 0 ? 1_250 : 0);
        var price = TapeFixture.BasePrice + ((control & 8) != 0 ? -3 : 0);
        var inline = control >> 4;

        return new Print(time, price, inline == 0 ? 100u : (uint)inline, (TradeSide)(control & SideMask));
    }
}
