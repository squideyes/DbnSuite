using ClientBar = DbnSuite.Client.Export.Bar;
using DbnSuite.Client.Export;
using DbnSuite.Client.Tape;
using DbnSuite.Client.Time;
using Shouldly;

namespace DbnSuite.UnitTests.TapeFormatTests;

public sealed class BarStreamTests
{
    private const long Second = TapeFixture.MicrosPerSecond;
    private const long Minute = 60 * Second;
    private const int Price = 30_000;
    private const decimal TickSize = 0.25m;
    private const long RthOpen = TapeFixture.OpenMicros + 15 * 3_600 * Second + 30 * Minute;
    private const long RthClose = TapeFixture.OpenMicros + 22 * 3_600 * Second;

    private static BarSpec Spec(string text) => BarSpec.Parse(text).Value;

    [Theory]
    [InlineData("5s")]
    [InlineData("7s")]
    [InlineData("1m")]
    [InlineData("13m")]
    [InlineData("1h")]
    [InlineData("4h")]
    [InlineData("1d")]
    public async Task TimeBarsMatchBarBuilder(string size)
    {
        var (tape, bars) = await RandomBarsAsync();

        var streamed = Stream(tape, Spec(size), Window.Eth);
        var built = Built(Spec(size), SessionKind.Eth, bars);

        Same(streamed, built);
    }

    [Theory]
    [InlineData("1m")]
    [InlineData("7s")]
    [InlineData("1d")]
    public async Task RthBarsMatchBarBuilder(string size)
    {
        var (tape, bars) = await RandomBarsAsync();

        var streamed = Stream(tape, Spec(size), Window.Rth);
        var built = Built(Spec(size), SessionKind.Rth, bars);

        streamed.ShouldNotBeEmpty();
        Same(streamed, built);
    }

    [Theory]
    [InlineData("50t")]
    [InlineData("1000v")]
    public async Task ActivityBarsMatchBarBuilder(string size)
    {
        var prints = TradeCursorTests.RandomStream(3);
        var writer = new TradeWriter(TapeFixture.Info());

        foreach (var print in prints)
            writer.Add(print.Time, print.Price, print.Size, print.Side);

        var tape = await TapeFixture.RoundTripAsync(writer);
        var bars = tape.TradeBars(Spec(size));
        var streamed = new List<TapeBar>();

        while (bars.MoveNext())
            streamed.Add(bars.Bar);

        Same(streamed, BuiltFromTrades(Spec(size), prints));
    }

    [Fact]
    public async Task TheFirstRthBarOpensAtTheWindow()
    {
        var (tape, _) = await RandomBarsAsync();

        var bars = tape.Bars(Spec("5m"), Window.Rth);

        bars.MoveNext().ShouldBeTrue();
        bars.Start.ShouldBe(RthOpen);
        bars.End.ShouldBe(RthOpen + 5 * Minute);
    }

    [Fact]
    public async Task ADelayedOpenKeepsTheDaysUsualGrid()
    {
        var delayed = TapeFixture.Info(basePrice: Price) with { OpenTime = TapeFixture.OpenMicros * TapeFixture.NanosPerMicro + 2 * Minute * TapeFixture.NanosPerMicro };
        var writer = new S1Writer(delayed);

        writer.Add(TapeFixture.OpenMicros + 125 * Second, Price, Price, Price, Price, 1);

        var tape = await TapeFixture.RoundTripAsync(writer);
        var bars = tape.Bars(Spec("7s"));

        bars.MoveNext().ShouldBeTrue();
        bars.Start.ShouldBe(TapeFixture.OpenMicros + 119 * Second);
    }

    [Fact]
    public async Task AWindowThatDoesNotDivideEvenlyEndsShort()
    {
        var (tape, _) = await RandomBarsAsync();

        var streamed = Stream(tape, Spec("7m"), Window.Rth);

        streamed[^1].IsShort.ShouldBeTrue();
        streamed[^1].End.ShouldBe(RthClose);
        streamed[..^1].ShouldAllBe(b => !b.IsShort);
    }

    [Fact]
    public async Task EmptyPeriodsGiveNoBarByDefault()
    {
        var tape = await SparseAsync();

        var streamed = Stream(tape, Spec("1m"), Window.Eth);

        streamed.Select(b => b.Start).ShouldBe([TapeFixture.OpenMicros, TapeFixture.OpenMicros + 4 * Minute]);
    }

    [Fact]
    public async Task FillGapsRepeatsTheLastCloseAtZeroVolume()
    {
        var tape = await SparseAsync();
        var bars = tape.Bars(Spec("1m"), fillGaps: true);
        var streamed = new List<TapeBar>();

        while (bars.MoveNext())
            streamed.Add(bars.Bar);

        streamed.Select(b => b.Start).ShouldBe(Enumerable.Range(0, 5).Select(i => TapeFixture.OpenMicros + i * Minute));
        streamed[1].Volume.ShouldBe(0);
        streamed[1].Open.ShouldBe(Price + 2);
        streamed[3].Close.ShouldBe(Price + 2);
    }

    [Fact]
    public async Task FormingShowsTheBarSoFar()
    {
        var writer = new S1Writer(TapeFixture.Info());

        writer.Add(TapeFixture.OpenMicros, Price, Price, Price, Price, 2);
        writer.Add(TapeFixture.OpenMicros + Second, Price, Price + 3, Price, Price + 1, 5);

        var tape = await TapeFixture.RoundTripAsync(writer);
        var bars = tape.Bars(Spec("1m"));

        bars.Step().ShouldBeTrue();
        bars.HasBar.ShouldBeFalse();
        bars.Forming.Volume.ShouldBe(2);

        bars.Step().ShouldBeTrue();
        bars.HasBar.ShouldBeFalse();
        bars.Forming.Volume.ShouldBe(7);
        bars.Forming.High.ShouldBe(Price + 3);

        bars.Step().ShouldBeTrue();
        bars.HasBar.ShouldBeTrue();
        bars.Volume.ShouldBe(7);

        bars.Step().ShouldBeFalse();
    }

    [Fact]
    public async Task BarsAreDeliveredOnlyOnceTheirTimeHasPassed()
    {
        var (tape, _) = await RandomBarsAsync();
        var bars = tape.Bars(Spec("1m"));

        while (bars.Step())
        {
            if (bars.HasBar && bars.Forming.Volume > 0)
                bars.Forming.Start.ShouldBeGreaterThanOrEqualTo(bars.End);
        }
    }

    [Fact]
    public async Task BarFanMatchesEachSizeAlone()
    {
        var (tape, _) = await RandomBarsAsync();
        BarSpec[] specs = [Spec("1m"), Spec("5m"), Spec("1h")];
        var fan = tape.BarFan(specs);
        var bySize = specs.Select(_ => new List<TapeBar>()).ToList();

        while (fan.MoveNext())
            bySize[fan.Index].Add(fan.Bar);

        for (var i = 0; i < specs.Length; i++)
            bySize[i].ShouldBe(Stream(tape, specs[i], Window.Eth));
    }

    [Fact]
    public async Task ARuleOfYourOwnCanCutBars()
    {
        var (tape, _) = await RandomBarsAsync();
        var bars = tape.Bars(new RangeRule(10));
        var ranges = new List<int>();

        while (bars.MoveNext())
            ranges.Add(bars.High - bars.Low);

        ranges.Count.ShouldBeGreaterThan(1);
        ranges[..^1].ShouldAllBe(r => r >= 10);
    }

    [Fact]
    public async Task ActivityBarsNeedATradeTape()
    {
        var (tape, _) = await RandomBarsAsync();

        Should.Throw<ArgumentException>(() => tape.Bars(Spec("500t")));
    }

    [Fact]
    public async Task StreamingAllocatesNothing()
    {
        var (tape, _) = await RandomBarsAsync();
        var spec = Spec("1m");

        Walk(tape, spec);

        var before = GC.GetAllocatedBytesForCurrentThread();

        Walk(tape, spec);

        // The roller is made once per stream; the bars themselves cost nothing.
        (GC.GetAllocatedBytesForCurrentThread() - before).ShouldBeLessThan(512);
    }

    private static long Walk(Tape tape, BarSpec spec)
    {
        var bars = tape.Bars(spec);
        var sum = 0L;

        while (bars.MoveNext())
            sum += bars.Volume;

        return sum;
    }

    private static List<TapeBar> Stream(Tape tape, BarSpec spec, Window window)
    {
        var bars = tape.Bars(spec, window);
        var list = new List<TapeBar>();

        while (bars.MoveNext())
            list.Add(bars.Bar);

        return list;
    }

    private static List<ClientBar> Built(BarSpec spec, SessionKind kind, List<S1CursorTests.Bar> bars)
    {
        var list = new List<ClientBar>();
        var day = TapeFixture.TradeDate;
        var filter = SessionFilter.Create(new DateSpan(day, day), kind, TapeFixture.Root).Value;
        var builder = new BarBuilder(spec, filter, list.Add);

        foreach (var bar in bars)
        {
            var nanos = (ulong)(bar.Time * TapeFixture.NanosPerMicro);

            if (filter.Accept(nanos) is { } date)
                builder.Add(TapeFixture.Symbol, TapeFixture.Symbol, null, date, nanos, bar.Open * TickSize, bar.High * TickSize, bar.Low * TickSize, bar.Close * TickSize, bar.Volume, 0);
        }

        builder.Flush();

        return list;
    }

    private static List<ClientBar> BuiltFromTrades(BarSpec spec, List<TradeCursorTests.Print> prints)
    {
        var list = new List<ClientBar>();
        var day = TapeFixture.TradeDate;
        var filter = SessionFilter.Create(new DateSpan(day, day), SessionKind.Eth, TapeFixture.Root).Value;
        var builder = new BarBuilder(spec, filter, list.Add);

        foreach (var print in prints)
            builder.AddTrade(TapeFixture.Symbol, TapeFixture.Symbol, null, day, (ulong)(print.Time * TapeFixture.NanosPerMicro), print.Price * TickSize, print.Size);

        builder.Flush();

        return list;
    }

    private static void Same(List<TapeBar> streamed, List<ClientBar> built)
    {
        streamed.Count.ShouldBe(built.Count);

        for (var i = 0; i < built.Count; i++)
        {
            var mine = streamed[i];
            var theirs = built[i];

            (mine.Start * TapeFixture.NanosPerMicro).ShouldBe((long)theirs.Start);
            (mine.End * TapeFixture.NanosPerMicro).ShouldBe((long)theirs.End);
            (mine.Open * TickSize).ShouldBe(theirs.Open);
            (mine.High * TickSize).ShouldBe(theirs.High);
            (mine.Low * TickSize).ShouldBe(theirs.Low);
            (mine.Close * TickSize).ShouldBe(theirs.Close);
            mine.Volume.ShouldBe(theirs.Volume);
        }
    }

    /// <summary>A session's worth of random bars, with gaps, marked for RTH.</summary>
    private static async Task<(Tape Tape, List<S1CursorTests.Bar> Bars)> RandomBarsAsync()
    {
        var bars = new List<S1CursorTests.Bar>();
        var random = new Random(7);
        var close = Price;

        for (var time = TapeFixture.OpenMicros; time < TapeFixture.CloseMicros; time += Second * random.Next(1, 40))
        {
            var open = close + random.Next(-2, 3);

            close = open + random.Next(-3, 4);

            bars.Add(new S1CursorTests.Bar(time, open, Math.Max(open, close) + random.Next(0, 3), Math.Min(open, close) - random.Next(0, 3), close, (uint)random.Next(1, 50)));
        }

        var writer = new S1Writer(TapeFixture.Info(basePrice: Price));
        var started = false;
        var ended = false;

        foreach (var bar in bars)
        {
            if (!started && bar.Time >= RthOpen)
            {
                started = true;
                writer.MarkRthStart();
            }

            if (!ended && bar.Time >= RthClose)
            {
                ended = true;
                writer.MarkRthEnd();
            }

            writer.Add(bar.Time, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume);
        }

        return (await TapeFixture.RoundTripAsync(writer), bars);
    }

    private static Task<Tape> SparseAsync()
    {
        var writer = new S1Writer(TapeFixture.Info(basePrice: Price));

        writer.Add(TapeFixture.OpenMicros, Price, Price + 2, Price, Price + 2, 3);
        writer.Add(TapeFixture.OpenMicros + 4 * Minute + 5 * Second, Price + 2, Price + 2, Price + 2, Price + 2, 1);

        return TapeFixture.RoundTripAsync(writer);
    }

    /// <summary>A range bar: it finishes once its high and low are this far apart.</summary>
    private readonly record struct RangeRule(int Range) : IBarRule
    {
        public bool SpansDates => false;

        public bool Splits(in TapeBar forming, long time) => false;

        public bool IsFull(in TapeBar forming) => forming.High - forming.Low >= Range;

        public void Open(ref TapeBar bar, long time) => bar.Start = bar.End = time;

        public long EndAfter(in TapeBar forming, long time) => time;

        public bool FillsGap(in TapeBar last, long time, ref TapeBar bar) => false;
    }
}
