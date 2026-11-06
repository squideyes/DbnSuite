using DbnClient.Export;
using DbnClient.Tape;
using DbnClient.Time;
using Shouldly;

namespace DbnSync.UnitTests.TapeFormatTests;

public sealed class TapeSeriesTests : IDisposable
{
    private const uint OldId = 1;
    private const uint NewId = 2;
    private const string OldSymbol = "ESZ6";
    private const string NewSymbol = "ESH7";
    private const long OldSettle = 7_754_250_000_000;
    private const long NewSettle = 7_820_000_000_000;
    private const long Second = TapeFixture.MicrosPerSecond;
    private const long Day = 86_400;
    private const int Price = 31_000;

    /// <summary>The first trade date of a week of five.</summary>
    private static readonly DateOnly Monday = new(2026, 10, 26);

    private readonly string folder = Path.Combine(Path.GetTempPath(), $"tape-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public async Task EachDayOpensTheContractHeld()
    {
        var catalog = await WeekAsync(rollOn: 3);

        var days = await DaysAsync(catalog);

        days.Select(d => d.Symbol).ShouldBe([OldSymbol, OldSymbol, OldSymbol, NewSymbol, NewSymbol]);
    }

    [Fact]
    public async Task OffsetsAddTheGapsOfLaterRolls()
    {
        var catalog = await WeekAsync(rollOn: 3);

        var days = await DaysAsync(catalog);

        days[0].Offset.ShouldBe(NewSettle - OldSettle);
        days[2].Offset.ShouldBe(NewSettle - OldSettle);
        days[3].Offset.ShouldBe(0);
        days[4].Offset.ShouldBe(0);
    }

    [Fact]
    public async Task ARollWithNoSettlementHasNoOffset()
    {
        var catalog = await WeekAsync(rollOn: 3, settled: false);

        var days = await DaysAsync(catalog);

        days[2].Offset.ShouldBeNull();
        days[3].Offset.ShouldBe(0);
    }

    [Fact]
    public async Task AMissingTapeEndsTheSeriesWithAFailure()
    {
        var catalog = await WeekAsync(rollOn: 3);

        File.Delete(TapePaths.For(folder, TapeFixture.Dataset, TapeKind.S1, TapeFixture.Root, OldSymbol, Monday.AddDays(1)));

        var series = new TapeSeries(catalog, folder, TapeKind.S1, new DateSpan(Monday, Monday.AddDays(4)));
        var results = new List<bool>();

        await foreach (var day in series.DaysAsync(CancellationToken.None))
            results.Add(day.IsSuccess);

        results.ShouldBe([true, false]);
    }

    [Fact]
    public async Task DailyBarsNeverSpanTwoDates()
    {
        var catalog = await WeekAsync(rollOn: 9);

        var bars = await BarsAsync(catalog, "1d");

        bars.Count.ShouldBe(5);
    }

    [Fact]
    public async Task AWeeklyBarRunsAcrossItsDays()
    {
        var catalog = await WeekAsync(rollOn: 9);

        var bars = await BarsAsync(catalog, "1w");

        bars.Count.ShouldBe(1);
        bars[0].Volume.ShouldBe(5);
        bars[0].Close.ShouldBe(Price + 4);
    }

    [Fact]
    public async Task AWeeklyBarNeverSpansTwoContracts()
    {
        var catalog = await WeekAsync(rollOn: 3);

        var bars = await BarsAsync(catalog, "1w");

        bars.Select(b => b.Volume).ShouldBe([3, 2]);
    }

    private async Task<List<SeriesDay>> DaysAsync(Catalog catalog)
    {
        var days = new List<SeriesDay>();
        var series = new TapeSeries(catalog, folder, TapeKind.S1, new DateSpan(Monday, Monday.AddDays(4)));

        await foreach (var day in series.DaysAsync(CancellationToken.None))
            days.Add(day.Value);

        return days;
    }

    private async Task<List<TapeBar>> BarsAsync(Catalog catalog, string size)
    {
        var list = new List<TapeBar>();
        var series = new TapeSeries(catalog, folder, TapeKind.S1, new DateSpan(Monday, Monday.AddDays(4)));
        var rolling = series.Bars(BarSpec.Parse(size).Value);

        await foreach (var day in series.DaysAsync(CancellationToken.None))
            Collect(rolling, day.Value, list);

        if (rolling.Finish(out var last))
            list.Add(last);

        return list;
    }

    private static void Collect(SeriesBars rolling, SeriesDay day, List<TapeBar> list)
    {
        var bars = rolling.Over(day);

        while (bars.MoveNext())
            list.Add(bars.Bar);
    }

    /// <summary>Five sessions with one bar each. The contract held changes on the given day.</summary>
    private async Task<Catalog> WeekAsync(int rollOn, bool settled = true)
    {
        var days = new List<CatalogDay>();

        for (var i = 0; i < 5; i++)
        {
            var date = Monday.AddDays(i);
            var open = (Sessions.SessionStart(date) - DateTimeOffset.UnixEpoch).Ticks / 10;
            var close = (Sessions.SessionEnd(date) - DateTimeOffset.UnixEpoch).Ticks / 10;
            var symbol = i < rollOn ? OldSymbol : NewSymbol;

            var info = TapeFixture.Info(basePrice: Price) with
            {
                Symbol = symbol,
                TradeDate = date,
                OpenTime = open * 1_000,
                CloseTime = close * 1_000
            };

            var writer = new S1Writer(info);

            writer.Add(open / Second * Second, Price + i, Price + i, Price + i, Price + i, 1);

            await writer.SaveAsync(TapePaths.For(folder, TapeFixture.Dataset, TapeKind.S1, TapeFixture.Root, symbol, date), CancellationToken.None);

            days.Add(new CatalogDay
            {
                TradeDate = date,
                Held = symbol,
                Sessions = [new CatalogSession(open * 1_000, close * 1_000, [])],
                Contracts = settled
                    ? [new CatalogEntry { InstrumentId = OldId, Settlement = OldSettle }, new CatalogEntry { InstrumentId = NewId, Settlement = NewSettle }]
                    : []
            });
        }

        return new Catalog(TapeFixture.Dataset, TapeFixture.Root, [Contract(OldId, OldSymbol), Contract(NewId, NewSymbol)], days);
    }

    private static CatalogContract Contract(uint id, string symbol) => new()
    {
        InstrumentId = id,
        Symbol = symbol,
        TickRaw = TapeFixture.TickRaw,
        TickValue = 12.5m,
        Year = 2026,
        Month = 12,
        FirstTrade = 0,
        LastTrade = long.MaxValue - Day
    };
}
