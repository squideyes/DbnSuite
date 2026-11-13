using DbnClient;
using DbnClient.Tape;
using DbnClient.Time;
using Shouldly;

namespace DbnSync.UnitTests.TapeFormatTests;

public sealed class SessionStreamTests : IDisposable
{
    private const ulong Seed = 7;
    private const int DayCount = 6;
    private const int Warmup = 2;
    private const int Step = 10;
    private const int Move = 5;
    private const int FirstPrice = TapeFixture.BasePrice;
    private const long MicrosPerSecond = TapeFixture.MicrosPerSecond;

    private static readonly DateOnly Monday = new(2026, 10, 26);

    private readonly string folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public void Dispose()
    {
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public async Task TheDaysAreTheDatesTheFolderHasTapesFor()
    {
        var days = await WriteDaysAsync();
        await WriteS1Async(Monday.AddDays(30), FirstPrice, root: "NQ");

        var listed = Stream().Days();

        listed.ShouldBe(days);
    }

    [Fact]
    public async Task SessionsComeInTheSamplersOrder()
    {
        var sampler = new SessionSampler(await WriteDaysAsync(), Seed, warmup: Warmup);

        var sessions = await RunAsync(Stream(), sampler, 0);

        sessions.Select(s => (s.TradeDate, s.IsWarmup)).ShouldBe(sampler.Run(0).Select(d => (d.TradeDate, d.IsWarmup)));
        sessions.Select(s => s.Index).ShouldBe(Enumerable.Range(0, Warmup + DayCount));
        sessions.ShouldAllBe(s => s.Tape.Header.TradeDate == s.TradeDate);
    }

    [Theory]
    [InlineData(Sampling.Permutation)]
    [InlineData(Sampling.Bootstrap)]
    public async Task EachSessionStartsWhereThePreviousOneEnded(Sampling sampling)
    {
        var sampler = new SessionSampler(await WriteDaysAsync(), Seed, sampling, Warmup);

        var sessions = await RunAsync(Stream(), sampler, 3);

        sessions[0].Offset.ShouldBe(0);

        for (var i = 1; i < sessions.Count; i++)
            (First(sessions[i].TradeDate) + sessions[i].Offset).ShouldBe(Last(sessions[i - 1].TradeDate) + sessions[i - 1].Offset);
    }

    [Fact]
    public async Task ARunGivesTheSameSessionsAndOffsetsEveryTime()
    {
        var days = await WriteDaysAsync();

        var one = await RunAsync(Stream(), new SessionSampler(days, Seed, warmup: Warmup), 5);
        var two = await RunAsync(Stream(), new SessionSampler(days, Seed, warmup: Warmup), 5);

        two.Select(s => (s.TradeDate, s.Offset)).ShouldBe(one.Select(s => (s.TradeDate, s.Offset)));
    }

    [Fact]
    public async Task AMissingTapeEndsTheRunWithAFailure()
    {
        var days = await WriteDaysAsync();
        var sampler = new SessionSampler(days, Seed);
        File.Delete(TapePaths.For(folder, TapeFixture.Dataset, TapeKind.S1, TapeFixture.Root, sampler.Run(0)[2].TradeDate));

        var results = new List<Result<StreamSession>>();

        await foreach (var session in Stream().RunAsync(sampler, 0, CancellationToken.None))
            results.Add(session);

        results.Count.ShouldBe(3);
        results.Take(2).ShouldAllBe(r => r.IsSuccess);
        results[2].Error!.Code.ShouldBe(TapeErrors.Io);
    }

    [Fact]
    public async Task CancellingARunStopsItBeforeTheNextSession()
    {
        var sampler = new SessionSampler(await WriteDaysAsync(), Seed);
        using var cancel = new CancellationTokenSource();
        var seen = 0;

        var run = async () =>
        {
            await foreach (var session in Stream().RunAsync(sampler, 0, cancel.Token))
            {
                seen++;
                await cancel.CancelAsync();
            }
        };

        await run.ShouldThrowAsync<OperationCanceledException>();
        seen.ShouldBe(1);
    }

    [Fact]
    public async Task LeavingARunEarlyLeavesNoTapeOpen()
    {
        var sampler = new SessionSampler(await WriteDaysAsync(), Seed);

        await foreach (var session in Stream().RunAsync(sampler, 0, CancellationToken.None))
            break;

        var delete = () => Directory.Delete(folder, recursive: true);

        delete.ShouldNotThrow();
    }

    [Fact]
    public async Task ARunOfTwoTickSizesIsRefused()
    {
        await WriteS1Async(Monday, FirstPrice);
        await WriteS1Async(Monday.AddDays(1), FirstPrice, tickRaw: TapeFixture.TickRaw * 2);
        var sampler = new SessionSampler([Monday, Monday.AddDays(1)], Seed);

        var results = new List<Result<StreamSession>>();

        await foreach (var session in Stream().RunAsync(sampler, 0, CancellationToken.None))
            results.Add(session);

        results[^1].Error!.Code.ShouldBe(TapeErrors.Refused);
    }

    [Fact]
    public async Task AnL1SessionIsStitchedByItsTradesNotItsQuotes()
    {
        var open = OpenMicros(Monday);
        var writer = new L1Writer(Info(Monday, FirstPrice));
        writer.Quote(open, FirstPrice - 1, 3, FirstPrice + 1, 4);
        writer.Trade(open + 1, FirstPrice + 1, 1, TradeSide.Buyer);
        writer.Quote(open + 2, FirstPrice + 7, 3, FirstPrice + 9, 4);
        writer.Trade(open + 3, FirstPrice + 8, 1, TradeSide.Seller);

        var tape = await TapeFixture.RoundTripAsync(writer);
        var ends = SessionStream.Scan(tape, TapeKind.L1);

        ends.ShouldBe(new SessionStream.Ends(FirstPrice + 1, FirstPrice + 8));
    }

    private SessionStream Stream() => new(folder, TapeFixture.Dataset, TapeFixture.Root, TapeKind.S1);

    private static async Task<List<StreamSession>> RunAsync(SessionStream stream, SessionSampler sampler, int run)
    {
        var sessions = new List<StreamSession>();

        await foreach (var session in stream.RunAsync(sampler, run, CancellationToken.None))
            sessions.Add(session.Value);

        return sessions;
    }

    /// <summary>Weekdays from <see cref="Monday"/>, each opening <see cref="Step"/> ticks above the day before.</summary>
    private async Task<List<DateOnly>> WriteDaysAsync()
    {
        var days = Enumerable.Range(0, DayCount + 2).Select(Monday.AddDays).Where(d => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)).Take(DayCount).ToList();

        foreach (var day in days)
            await WriteS1Async(day, First(day));

        return days;
    }

    private static int First(DateOnly day) => FirstPrice + Step * (day.DayNumber - Monday.DayNumber);

    private static int Last(DateOnly day) => First(day) + Move;

    /// <summary>Two bars: up <see cref="Move"/> ticks over the session.</summary>
    private async Task WriteS1Async(DateOnly day, int open, string root = TapeFixture.Root, long tickRaw = TapeFixture.TickRaw)
    {
        var start = OpenMicros(day);
        var writer = new S1Writer(Info(day, open, tickRaw) with { Root = root });

        writer.Add(start, open, open + 2, open, open + 2, 1);
        writer.Add(start + MicrosPerSecond, open + 2, open + Move, open + 2, open + Move, 1);

        await writer.SaveAsync(TapePaths.For(folder, TapeFixture.Dataset, TapeKind.S1, root, day), CancellationToken.None);
    }

    private static long OpenMicros(DateOnly day) => TapeFixture.Nanos(Sessions.SessionStart(day)) / TapeFixture.NanosPerMicro;

    private static TapeInfo Info(DateOnly day, int basePrice, long tickRaw = TapeFixture.TickRaw) => TapeFixture.Info(basePrice: basePrice) with
    {
        TradeDate = day,
        TickRaw = tickRaw,
        OpenTime = TapeFixture.Nanos(Sessions.SessionStart(day)),
        CloseTime = TapeFixture.Nanos(Sessions.SessionEnd(day))
    };
}
