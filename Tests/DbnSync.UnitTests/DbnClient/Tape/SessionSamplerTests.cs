using DbnClient.Tape;
using Shouldly;

namespace DbnSync.UnitTests.TapeFormatTests;

public sealed class SessionSamplerTests
{
    private const ulong Seed = 42;
    private const int DayCount = 20;
    private const int Warmup = 3;
    private const int Runs = 50;

    private static readonly DateOnly First = new(2026, 1, 5);

    [Fact]
    public void SplitMixMatchesItsReference()
    {
        var state = 0UL;

        var first = SeededRandom.SplitMix(ref state);
        var second = SeededRandom.SplitMix(ref state);

        first.ShouldBe(0xE220A8397B1DCDAFUL);
        second.ShouldBe(0x6E789E6AA1B965F4UL);
    }

    [Fact]
    public void ARunDealsTheSameSessionsEveryTime()
    {
        var one = new SessionSampler(Days(DayCount), Seed, warmup: Warmup);
        var two = new SessionSampler(Days(DayCount), Seed, warmup: Warmup);

        var a = one.Run(7);
        var b = two.Run(7);

        b.ShouldBe(a);
    }

    [Fact]
    public void ASeedsFirstRunIsPinned()
    {
        var sampler = new SessionSampler(Days(6), Seed, warmup: 2);

        var run = sampler.Run(0);

        run.Select(d => d.TradeDate.Day).ShouldBe(GoldenDays);
        run.Select(d => d.IsWarmup).ShouldBe([true, true, false, false, false, false, false, false]);
    }

    [Fact]
    public void RunsOfOneSeedDealDifferently()
    {
        var sampler = new SessionSampler(Days(DayCount), Seed);

        var orders = Enumerable.Range(0, Runs).Select(r => string.Join(',', sampler.Run(r))).ToHashSet();

        orders.Count.ShouldBe(Runs);
    }

    [Fact]
    public void SeedsDealDifferently()
    {
        var one = new SessionSampler(Days(DayCount), Seed);
        var two = new SessionSampler(Days(DayCount), Seed + 1);

        var a = one.Run(0);
        var b = two.Run(0);

        b.ShouldNotBe(a);
    }

    [Fact]
    public void APermutationTradesEveryDayOnce()
    {
        var days = Days(DayCount);
        var sampler = new SessionSampler(days, Seed, warmup: Warmup);

        var trading = Enumerable.Range(0, Runs).Select(r => sampler.Run(r).Where(d => !d.IsWarmup).Select(d => d.TradeDate).ToList());

        trading.ShouldAllBe(t => t.Order().SequenceEqual(days));
    }

    [Fact]
    public void WarmupComesFirst()
    {
        var sampler = new SessionSampler(Days(DayCount), Seed, Sampling.Bootstrap, Warmup, sessions: 5);

        var run = sampler.Run(0);

        run.Count.ShouldBe(Warmup + 5);
        run.Take(Warmup).ShouldAllBe(d => d.IsWarmup);
        run.Skip(Warmup).ShouldAllBe(d => !d.IsWarmup);
    }

    [Fact]
    public void ABootstrapDrawsFromTheDaysWithReplacement()
    {
        var days = Days(DayCount);
        var sampler = new SessionSampler(days, Seed, Sampling.Bootstrap);

        var runs = Enumerable.Range(0, Runs).Select(r => sampler.Run(r).Select(d => d.TradeDate).ToList()).ToList();

        runs.ShouldAllBe(r => r.Count == DayCount && r.All(days.Contains));
        runs.ShouldContain(r => r.Distinct().Count() < DayCount);
    }

    [Fact]
    public void TheOrderAndRepeatsOfTheDaysGivenDontMatter()
    {
        var days = Days(DayCount);
        var sorted = new SessionSampler(days, Seed);
        var messy = new SessionSampler([.. days.AsEnumerable().Reverse(), days[3], days[9]], Seed);

        var a = sorted.Run(0);
        var b = messy.Run(0);

        b.ShouldBe(a);
        messy.Fingerprint.ShouldBe(sorted.Fingerprint);
    }

    [Fact]
    public void TheFingerprintChangesWithTheDays()
    {
        var before = new SessionSampler(Days(DayCount), Seed);
        var after = new SessionSampler(Days(DayCount + 1), Seed);

        var changed = after.Fingerprint;

        changed.ShouldNotBe(before.Fingerprint);
    }

    [Fact]
    public void APermutationTakesNoCountOfSessions()
    {
        var days = Days(DayCount);

        var build = () => new SessionSampler(days, Seed, Sampling.Permutation, sessions: 5);

        build.ShouldThrow<ArgumentException>();
    }

    [Fact]
    public void NoDaysIsRefused()
    {
        var build = () => new SessionSampler([], Seed);

        build.ShouldThrow<ArgumentException>();
    }

    [Fact]
    public void NextBelowReachesEveryValueAndNoOther()
    {
        const int count = 7;
        var random = new SeededRandom(Seed);

        var seen = Enumerable.Range(0, 1_000).Select(_ => random.NextBelow(count)).ToHashSet();

        seen.Order().ShouldBe(Enumerable.Range(0, count));
    }

    /// <summary>
    /// Days of the month dealt by seed 42's first run over six days from 5 January, with two
    /// warm-ups. If this changes, every saved seed deals differently: don't change it lightly.
    /// </summary>
    private static readonly int[] GoldenDays = [5, 5, 6, 7, 9, 5, 8, 10];

    private static DateOnly[] Days(int count) => [.. Enumerable.Range(0, count).Select(First.AddDays)];
}
