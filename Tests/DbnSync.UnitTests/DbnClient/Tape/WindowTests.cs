using DbnClient.Tape;
using Shouldly;

namespace DbnSync.UnitTests.TapeFormatTests;

public sealed class WindowTests
{
    private const long Minute = 60 * TapeFixture.MicrosPerSecond;
    private const long Hour = 60 * Minute;
    private const int Price = 30_000;
    private const int StepMinutes = 15;

    /// <summary>08:30 and 15:00 Chicago on the fixture's trade date.</summary>
    private const long RthOpen = TapeFixture.OpenMicros + 15 * Hour + 30 * Minute;
    private const long RthClose = TapeFixture.OpenMicros + 22 * Hour;

    private static readonly TimeOnly NineThirty = new(9, 30);
    private static readonly TimeOnly Eleven = new(11, 0);

    [Fact]
    public async Task TheDefaultWindowIsTheWholeSession()
    {
        var tape = await TradesAsync();

        var times = Times(tape, Window.Eth);

        times.Count.ShouldBe((int)tape.Header.EventCount);
    }

    [Fact]
    public async Task RthStartsAtTheMarkAndStopsAtTheEndMark()
    {
        var tape = await TradesAsync();

        var times = Times(tape, Window.Rth);

        times[0].ShouldBe(RthOpen);
        times[^1].ShouldBeLessThan(RthClose);
        times.ShouldBe(Times(tape, Window.Eth).Where(t => t >= RthOpen && t < RthClose));
    }

    [Fact]
    public async Task HoursInsideRthKeepTheStateFromTheMark()
    {
        var tape = await TradesAsync();

        var cursor = tape.TradeCursor(Window.Hours(NineThirty, Eleven));
        var whole = tape.TradeCursor();

        cursor.MoveNext().ShouldBeTrue();

        while (whole.MoveNext() && whole.Time < cursor.Time)
        {
        }

        cursor.Time.ShouldBe(RthOpen + Hour);
        cursor.Price.ShouldBe(whole.Price);
    }

    [Fact]
    public async Task HoursStopAtTheirEnd()
    {
        var tape = await TradesAsync();

        var times = Times(tape, Window.Hours(NineThirty, Eleven));

        times[^1].ShouldBeLessThan(RthOpen + Hour + 90 * Minute);
        times.ShouldBe(Times(tape, Window.Eth).Where(t => t >= RthOpen + Hour && t < RthOpen + Hour + 90 * Minute));
    }

    [Fact]
    public async Task HoursBeforeRthDecodeFromTheOpen()
    {
        var tape = await TradesAsync();

        var times = Times(tape, Window.Hours(new TimeOnly(1, 0), new TimeOnly(2, 0)));

        times.ShouldNotBeEmpty();
        times.ShouldBe(Times(tape, Window.Eth).Where(t => t >= RthOpen - 7 * Hour - 30 * Minute && t < RthOpen - 6 * Hour - 30 * Minute));
    }

    [Fact]
    public async Task HoursCanRunPastMidnight()
    {
        var tape = await TradesAsync();

        var times = Times(tape, Window.Hours(new TimeOnly(22, 0), new TimeOnly(2, 0)));

        times.ShouldBe(Times(tape, Window.Eth).Where(t => t >= TapeFixture.OpenMicros + 5 * Hour && t < TapeFixture.OpenMicros + 9 * Hour));
    }

    [Fact]
    public async Task HoursInTheEveningAreTheEveningTheSessionOpens()
    {
        var tape = await TradesAsync();

        var times = Times(tape, Window.Hours(new TimeOnly(18, 0), new TimeOnly(23, 0)));

        times.ShouldNotBeEmpty();
        times.ShouldBe(Times(tape, Window.Eth).Where(t => t >= TapeFixture.OpenMicros + Hour && t < TapeFixture.OpenMicros + 6 * Hour));
    }

    [Fact]
    public async Task ATapeWithNoRegularSessionReplaysNothingForRth()
    {
        var writer = new TradeWriter(TapeFixture.Info());

        writer.Add(TapeFixture.OpenMicros, Price, 1, TradeSide.Buyer);

        var tape = await TapeFixture.RoundTripAsync(writer);

        Times(tape, Window.Rth).ShouldBeEmpty();
    }

    [Fact]
    public async Task InRthCostsNoSeparateWalk()
    {
        var tape = await TradesAsync();
        var cursor = tape.TradeCursor();
        var inside = new List<long>();

        while (cursor.MoveNext())
        {
            if (cursor.InRth)
                inside.Add(cursor.Time);
        }

        inside.ShouldBe(Times(tape, Window.Rth));
    }

    [Fact]
    public async Task L1AndS1FollowWindowsToo()
    {
        var bars = new S1Writer(TapeFixture.Info());
        var quotes = new L1Writer(TapeFixture.Info());
        var size = 1u;

        for (var time = TapeFixture.OpenMicros; time < TapeFixture.CloseMicros; time += StepMinutes * Minute)
        {
            if (time == RthOpen)
            {
                bars.MarkRthStart();
                quotes.MarkRthStart();
            }

            if (time == RthClose)
            {
                bars.MarkRthEnd();
                quotes.MarkRthEnd();
            }

            bars.Add(time, Price, Price, Price, Price, 1);
            quotes.Quote(time, Price, size++, Price + 1, 1);
        }

        var s1 = await TapeFixture.RoundTripAsync(bars);
        var l1 = await TapeFixture.RoundTripAsync(quotes);
        var s1Cursor = s1.S1Cursor(Window.Rth);
        var l1Cursor = l1.L1Cursor(Window.Hours(NineThirty, Eleven));

        s1Cursor.MoveNext().ShouldBeTrue();
        s1Cursor.Time.ShouldBe(RthOpen);
        s1Cursor.InRth.ShouldBeTrue();
        l1Cursor.MoveNext().ShouldBeTrue();
        l1Cursor.Time.ShouldBe(RthOpen + Hour);
        l1Cursor.BidSize.ShouldBeGreaterThan(1u);
        l1Cursor.AskPrice.ShouldBe(Price + 1);
    }

    [Fact]
    public async Task LoadingRthReadsOnlyItsBytes()
    {
        var bytes = await TapeFixture.ToBytesAsync(Trades());
        using var stream = new MemoryStream(bytes);

        var loaded = await Tape.LoadAsync(stream, Window.Rth, CancellationToken.None);
        var whole = await TapeFixture.LoadAsync(bytes);

        loaded.Value.IsRthOnly.ShouldBeTrue();
        loaded.Value.Body.Length.ShouldBeLessThan(whole.Value.Body.Length);
        Times(loaded.Value, Window.Rth).ShouldBe(Times(whole.Value, Window.Rth));
    }

    [Fact]
    public async Task AnRthLoadCannotReplayTheWholeSession()
    {
        var bytes = await TapeFixture.ToBytesAsync(Trades());
        using var stream = new MemoryStream(bytes);

        var loaded = await Tape.LoadAsync(stream, Window.Rth, CancellationToken.None);

        Should.Throw<InvalidOperationException>(() => loaded.Value.TradeCursor());
    }

    [Fact]
    public async Task AZstdTapeLoadsWholeForRth()
    {
        var bytes = await TapeFixture.ToBytesAsync(Trades(compress: true));
        using var stream = new MemoryStream(bytes);

        var loaded = await Tape.LoadAsync(stream, Window.Rth, CancellationToken.None);

        loaded.Value.IsRthOnly.ShouldBeFalse();
        Times(loaded.Value, Window.Rth)[0].ShouldBe(RthOpen);
    }

    [Fact]
    public async Task AWrongLengthFailsAnRthLoad()
    {
        var bytes = await TapeFixture.ToBytesAsync(Trades());
        using var stream = new MemoryStream(bytes[..^1]);

        var loaded = await Tape.LoadAsync(stream, Window.Rth, CancellationToken.None);

        loaded.Error!.Code.ShouldBe(TapeErrors.Corrupt);
    }

    [Fact]
    public async Task ReplayAllocatesNothing()
    {
        var writer = new L1Writer(TapeFixture.Info());

        L1CursorTests.FillRandom(writer, 1, 5_000);

        var tape = await TapeFixture.RoundTripAsync(writer);

        WalkL1(tape);

        var before = GC.GetAllocatedBytesForCurrentThread();

        WalkL1(tape);

        (GC.GetAllocatedBytesForCurrentThread() - before).ShouldBe(0);
    }

    private static long WalkL1(Tape tape)
    {
        var cursor = tape.L1Cursor();
        var sum = 0L;

        while (cursor.MoveNext())
            sum += cursor.BidSize;

        return sum;
    }

    private static List<long> Times(Tape tape, Window window)
    {
        var times = new List<long>();
        var cursor = tape.TradeCursor(window);

        while (cursor.MoveNext())
            times.Add(cursor.Time);

        return times;
    }

    private static Task<Tape> TradesAsync() => TapeFixture.RoundTripAsync(Trades());

    /// <summary>A trade every 15 minutes through the session, each a tick from the last.</summary>
    private static TradeWriter Trades(bool compress = false)
    {
        var writer = new TradeWriter(TapeFixture.Info(compress));
        var price = Price;

        for (var time = TapeFixture.OpenMicros; time < TapeFixture.CloseMicros; time += StepMinutes * Minute)
        {
            if (time == RthOpen)
                writer.MarkRthStart();

            if (time == RthClose)
                writer.MarkRthEnd();

            writer.Add(time, price++, 1, TradeSide.Buyer);
        }

        return writer;
    }
}
