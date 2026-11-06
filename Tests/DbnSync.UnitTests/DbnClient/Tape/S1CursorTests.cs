using DbnClient.Tape;
using Shouldly;

namespace DbnSync.UnitTests.TapeFormatTests;

public sealed class S1CursorTests
{
    private const int RandomBars = 5_000;

    public readonly record struct Bar(long Time, int Open, int High, int Low, int Close, uint Volume);

    [Fact]
    public async Task EveryControlByteRoundTrips()
    {
        for (var control = 0; control <= byte.MaxValue; control++)
        {
            var bar = BarFor((byte)control);
            var writer = new S1Writer(TapeFixture.Info());

            writer.Add(TapeFixture.OpenMicros, TapeFixture.BasePrice, TapeFixture.BasePrice, TapeFixture.BasePrice, TapeFixture.BasePrice, 1);
            writer.Add(bar.Time, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume);

            var tape = await TapeFixture.RoundTripAsync(writer);

            tape.Body[1].ShouldBe((byte)control);
            Read(tape)[1].ShouldBe(bar);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task SeededStreamsRoundTrip(int seed)
    {
        var bars = RandomStream(seed);
        var writer = new S1Writer(TapeFixture.Info());

        foreach (var bar in bars)
            writer.Add(bar.Time, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume).ShouldBeTrue();

        var tape = await TapeFixture.RoundTripAsync(writer);

        Read(tape).ShouldBe(bars);
    }

    [Fact]
    public async Task TheWidestBarRoundTrips()
    {
        var bar = new Bar(TapeFixture.OpenMicros, 0, int.MaxValue, int.MinValue, 0, uint.MaxValue);
        var writer = new S1Writer(TapeFixture.Info(basePrice: int.MinValue));

        writer.Add(bar.Time, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume);

        var tape = await TapeFixture.RoundTripAsync(writer);

        Read(tape).ShouldBe([bar]);
    }

    [Fact]
    public async Task ABarCanTradeBelowZero()
    {
        var bar = new Bar(TapeFixture.OpenMicros, -150, -100, -200, -120, 9);
        var writer = new S1Writer(TapeFixture.Info());

        writer.Add(bar.Time, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume);

        var tape = await TapeFixture.RoundTripAsync(writer);

        Read(tape).ShouldBe([bar]);
    }

    [Fact]
    public async Task ACursorStopsAndStaysStopped()
    {
        var writer = new S1Writer(TapeFixture.Info());

        writer.Add(TapeFixture.OpenMicros, 1, 1, 1, 1, 1);

        var tape = await TapeFixture.RoundTripAsync(writer);
        var cursor = tape.S1Cursor();

        cursor.MoveNext().ShouldBeTrue();
        cursor.MoveNext().ShouldBeFalse();
        cursor.MoveNext().ShouldBeFalse();
    }

    [Theory]
    [InlineData("", 1)]
    [InlineData("01", 1)]
    [InlineData("2020", 1)]
    [InlineData("0280808080808080808080808001", 1)]
    public void ABodyThatDisagreesWithItsCountThrows(string hex, long events)
    {
        var body = TapeFixture.Bytes(hex);
        var opening = RthMark.Opening(TapeKind.S1, 0, 0);

        Should.Throw<InvalidDataException>(() =>
        {
            var cursor = new S1Cursor(body, opening, events, body.Length);

            while (cursor.MoveNext())
            {
            }
        });
    }

    internal static List<Bar> Read(Tape tape)
    {
        var bars = new List<Bar>();
        var cursor = tape.S1Cursor();

        while (cursor.MoveNext())
            bars.Add(new Bar(cursor.Time, cursor.Open, cursor.High, cursor.Low, cursor.Close, cursor.Volume));

        return bars;
    }

    internal static List<Bar> RandomStream(int seed, int count = RandomBars)
    {
        var random = new Random(seed);
        var bars = new List<Bar>(count);
        var time = TapeFixture.OpenMicros;
        var close = TapeFixture.BasePrice;

        for (var i = 0; i < count; i++)
        {
            var open = close + (random.Next(3) == 0 ? random.Next(-5, 6) : 0);

            close = open + (random.Next(2) == 0 ? random.Next(-8, 9) : 0);

            var high = Math.Max(open, close) + (random.Next(3) == 0 ? random.Next(0, 6) : 0);
            var low = Math.Min(open, close) - (random.Next(3) == 0 ? random.Next(0, 6) : 0);
            var volume = random.Next(4) == 0 ? (uint)random.Next(8, 100_000) : (uint)random.Next(0, 8);

            bars.Add(new Bar(time, open, high, low, close, volume));

            time += TapeFixture.MicrosPerSecond * (random.Next(3) == 0 ? random.Next(2, 30) : 1);
        }

        return bars;
    }

    private static Bar BarFor(byte control)
    {
        var last = TapeFixture.BasePrice;
        var time = TapeFixture.OpenMicros + TapeFixture.MicrosPerSecond * ((control & 1) != 0 ? 5 : 1);
        var open = last + ((control & 2) != 0 ? 3 : 0);
        var close = open + ((control & 4) != 0 ? -2 : 0);
        var high = Math.Max(open, close) + ((control & 8) != 0 ? 4 : 0);
        var low = Math.Min(open, close) - ((control & 16) != 0 ? 6 : 0);
        var inline = control >> 5;

        return new Bar(time, open, high, low, close, inline == 0 ? 100u : (uint)inline);
    }
}
