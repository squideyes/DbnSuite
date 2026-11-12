using DbnClient.Tape;
using Shouldly;

namespace DbnSync.UnitTests.TapeFormatTests;

public sealed class CatalogTests : IDisposable
{
    private const uint Front = 1;
    private const uint Back = 2;
    private const uint OldNamesake = 3;
    private const uint Odd = 4;
    private const string FrontSymbol = "ESZ6";
    private const string BackSymbol = "ESH7";
    private const string RepeatedSymbol = "ESZ0";
    private const long OneHour = 3_600_000_000_000;
    private const long SettlementRaw = 7_754_250_000_000;
    private const long OpenInterest = 1_936_493;

    private static readonly long Open = TapeFixture.OpenSeconds * TapeFixture.NanosPerSecond;
    private static readonly long Close = TapeFixture.CloseSeconds * TapeFixture.NanosPerSecond;

    private readonly string folder = Path.Combine(Path.GetTempPath(), $"tape-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public void ADayKeepsOneSessionForContractsThatShareIt()
    {
        var days = DaysOf(new()
        {
            [Front] = [new TradingSpan(Open, Close)],
            [Back] = [new TradingSpan(Open, Close)]
        });

        days[0].Sessions.Count.ShouldBe(1);
        days[0].Sessions[0].Open.ShouldBe(Open);
    }

    [Fact]
    public void AContractWithItsOwnSessionKeepsIt()
    {
        var days = DaysOf(new()
        {
            [Front] = [new TradingSpan(Open, Close)],
            [Back] = [new TradingSpan(Open, Close)],
            [Odd] = [new TradingSpan(Open, Close - OneHour)]
        });

        var entry = days[0].Contracts.Single(c => c.InstrumentId == Odd);

        days[0].Sessions.Count.ShouldBe(2);
        days[0].Sessions[entry.Session].Close.ShouldBe(Close - OneHour);
    }

    [Fact]
    public void StatisticsLandOnTheirDay()
    {
        var statistics = new Dictionary<(DateOnly, uint), DayStatistics>
        {
            [(TapeFixture.TradeDate, Front)] = new(SettlementRaw, OpenInterest, null)
        };

        var days = Catalog.DaysFrom(TapeFixture.Root, new Dictionary<uint, List<TradingSpan>> { [Front] = [new(Open, Close)] }, statistics, _ => FrontSymbol);

        days[0].Held.ShouldBe(FrontSymbol);
        days[0].Contracts[0].Settlement.ShouldBe(SettlementRaw);
        days[0].Contracts[0].OpenInterest.ShouldBe(OpenInterest);
    }

    [Fact]
    public void ADateChoosesBetweenContractsOfOneName()
    {
        var catalog = Sample();

        var old = catalog.Contract(RepeatedSymbol, new DateOnly(2020, 6, 1));
        var latest = catalog.Contract(RepeatedSymbol);

        old!.InstrumentId.ShouldBe(OldNamesake);
        latest!.InstrumentId.ShouldBe(Odd);
    }

    [Fact]
    public void AnUnusedSecondDefinitionOfANameLosesToTheRealOne()
    {
        var days = Catalog.DaysFrom(TapeFixture.Root,
            new Dictionary<uint, List<TradingSpan>> { [Back] = [new TradingSpan(Open, Close)] },
            new Dictionary<(DateOnly, uint), DayStatistics> { [(TapeFixture.TradeDate, Back)] = new(SettlementRaw, OpenInterest, OpenInterest) },
            _ => FrontSymbol);
        var catalog = new Catalog(TapeFixture.Dataset, TapeFixture.Root,
        [
            Contract(Front, FrontSymbol, Open - 1_000 * OneHour, Close + 1_000 * OneHour),
            Contract(Back, FrontSymbol, Open - 1_000 * OneHour, Close + 1_000 * OneHour)
        ], days);

        var traded = catalog.Contract(FrontSymbol, TapeFixture.TradeDate);
        var holiday = catalog.Contract(FrontSymbol, TapeFixture.TradeDate.AddDays(1));

        traded!.InstrumentId.ShouldBe(Back);
        holiday!.InstrumentId.ShouldBe(Back);
    }

    [Fact]
    public void AnUnknownSymbolIsNull()
    {
        var catalog = Sample();

        var contract = catalog.Contract("NOPE");

        contract.ShouldBeNull();
    }

    [Fact]
    public void TheCalendarGivesAContractItsSession()
    {
        var calendar = new SessionCalendar(Sample());

        var day = calendar.For(FrontSymbol, TapeFixture.TradeDate);

        day!.Open.ShouldBe(Open);
        day.Close.ShouldBe(Close);
        day.RthOpen.ShouldNotBeNull();
    }

    [Fact]
    public void TheCalendarKnowsNothingOfADayWithNoSession()
    {
        var calendar = new SessionCalendar(Sample());

        var day = calendar.For(FrontSymbol, TapeFixture.TradeDate.AddDays(1));

        day.ShouldBeNull();
    }

    [Fact]
    public void TheCalendarKnowsNothingOfAContractNotYetListed()
    {
        var calendar = new SessionCalendar(Sample());

        var day = calendar.For(BackSymbol, TapeFixture.TradeDate);

        day.ShouldBeNull();
    }

    [Fact]
    public void AFullSessionIsAFullDay()
    {
        var catalog = Sample();

        catalog.IsFullDay(TapeFixture.TradeDate).ShouldBeTrue();
        catalog.FullDays.Select(d => d.TradeDate).ShouldBe([TapeFixture.TradeDate]);
    }

    [Fact]
    public void AnEarlyCloseLeavesTheDateOutWhole()
    {
        var catalog = WithSession(new CatalogSession(Open, Close - 4 * OneHour, []));

        catalog.Excluded(TapeFixture.TradeDate).ShouldBe("its session closed Thu 12:00, not 16:00");
        catalog.FullDays.ShouldBeEmpty();
        new SessionCalendar(catalog).For(FrontSymbol, TapeFixture.TradeDate).ShouldBeNull();
    }

    [Fact]
    public void AHaltInsideTheSessionStillLeavesAFullDay()
    {
        var catalog = WithSession(new CatalogSession(Open, Close, [new SessionBreak(Close - OneHour, Close - OneHour / 4)]));

        catalog.IsFullDay(TapeFixture.TradeDate).ShouldBeTrue();
        new SessionCalendar(catalog).For(FrontSymbol, TapeFixture.TradeDate).ShouldNotBeNull();
    }

    [Fact]
    public void ADayIsJudgedByTheSessionItsHeldContractTraded()
    {
        var stray = new CatalogSession(Close - OneHour / 2, Close, []);
        var full = new CatalogSession(Open, Close, []);
        var catalog = Catalogue(new CatalogDay
        {
            TradeDate = TapeFixture.TradeDate,
            Held = FrontSymbol,
            Sessions = [stray, full],
            Contracts = [new CatalogEntry { InstrumentId = Front, Session = 1 }]
        });

        catalog.IsFullDay(TapeFixture.TradeDate).ShouldBeTrue();
    }

    [Fact]
    public void ADateWithNoSessionIsNotAFullDay()
    {
        Sample().Excluded(TapeFixture.TradeDate.AddDays(1)).ShouldBe("it has no session");
    }

    [Fact]
    public void OnlyTheTapesOfAShortDayInsideTheCatalogAreRemoved()
    {
        var shortDate = TapeFixture.TradeDate.AddDays(1);
        var later = TapeFixture.TradeDate.AddDays(7);
        var shortSession = new CatalogSession(Open + 24 * OneHour, Close + 20 * OneHour, []);
        var catalog = Catalogue(
            new CatalogDay { TradeDate = TapeFixture.TradeDate, Held = FrontSymbol, Sessions = [new CatalogSession(Open, Close, [])], Contracts = [] },
            new CatalogDay { TradeDate = shortDate, Held = FrontSymbol, Sessions = [shortSession], Contracts = [] });

        var kept = Tape(TapeKind.S1, TapeFixture.TradeDate);
        var removed = Tape(TapeKind.L1, shortDate);
        var unjudged = Tape(TapeKind.S1, later);

        TapePaths.RemoveExcluded(folder, catalog).ShouldBe([removed]);
        File.Exists(kept).ShouldBeTrue();
        File.Exists(removed).ShouldBeFalse();
        File.Exists(unjudged).ShouldBeTrue();
    }

    [Theory]
    [InlineData("GLBX.MDP3/s1/ES/2026/ESZ6.s1.20261029.tape", "2026-10-29")]
    [InlineData(@"C:\Tapes\GLBX.MDP3\l1\ES\2026\ESZ6.l1.20261029.tape", "2026-10-29")]
    [InlineData("GLBX.MDP3/catalog/ES.catalog.json", null)]
    public void ATapesNameCarriesItsTradeDate(string key, string? date)
    {
        TapePaths.TradeDateOf(key).ShouldBe(date is null ? null : DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task ACatalogRoundTrips()
    {
        var catalog = Sample();

        var saved = await catalog.SaveAsync(folder, CancellationToken.None);
        var loaded = await Catalog.LoadAsync(saved.Value, CancellationToken.None);

        loaded.Value.Root.ShouldBe(TapeFixture.Root);
        loaded.Value.Contracts.ShouldBe(catalog.Contracts);
        loaded.Value.Day(TapeFixture.TradeDate)!.Held.ShouldBe(FrontSymbol);
        loaded.Value.Day(TapeFixture.TradeDate)!.Sessions[0].Close.ShouldBe(Close);
    }

    [Fact]
    public async Task AMissingCatalogIsAnIoFailure()
    {
        var loaded = await Catalog.LoadAsync(Path.Combine(folder, "none.json"), CancellationToken.None);

        loaded.Error!.Code.ShouldBe(TapeErrors.Io);
    }

    [Fact]
    public async Task ADamagedCatalogIsCorrupt()
    {
        Directory.CreateDirectory(folder);

        var path = Path.Combine(folder, "bad.json");

        await File.WriteAllTextAsync(path, "{");

        var loaded = await Catalog.LoadAsync(path, CancellationToken.None);

        loaded.Error!.Code.ShouldBe(TapeErrors.Corrupt);
    }

    private string Tape(TapeKind kind, DateOnly date)
    {
        var path = TapePaths.For(folder, TapeFixture.Dataset, kind, TapeFixture.Root, FrontSymbol, date);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]);

        return path;
    }

    private static Catalog WithSession(CatalogSession session) =>
        Catalogue(new CatalogDay { TradeDate = TapeFixture.TradeDate, Held = FrontSymbol, Sessions = [session], Contracts = [] });

    private static Catalog Catalogue(params CatalogDay[] days) => new(TapeFixture.Dataset, TapeFixture.Root,
        [Contract(Front, FrontSymbol, Open - 1_000 * OneHour, Close + 1_000 * OneHour)], days);

    private static IReadOnlyList<CatalogDay> DaysOf(Dictionary<uint, List<TradingSpan>> spans) =>
        Catalog.DaysFrom(TapeFixture.Root, spans, new Dictionary<(DateOnly, uint), DayStatistics>(), _ => null);

    private static Catalog Sample()
    {
        var days = Catalog.DaysFrom(TapeFixture.Root,
            new Dictionary<uint, List<TradingSpan>> { [Front] = [new TradingSpan(Open, Close)] },
            new Dictionary<(DateOnly, uint), DayStatistics>(),
            _ => FrontSymbol);

        return new Catalog(TapeFixture.Dataset, TapeFixture.Root,
        [
            Contract(Front, FrontSymbol, Open - 1_000 * OneHour, Close + 1_000 * OneHour),
            Contract(Back, BackSymbol, Close + 2_000 * OneHour, Close + 3_000 * OneHour),
            Contract(OldNamesake, RepeatedSymbol, Nanos(2019, 9, 20), Nanos(2020, 12, 18)),
            Contract(Odd, RepeatedSymbol, Nanos(2025, 9, 19), Nanos(2030, 12, 20))
        ], days);
    }

    private static CatalogContract Contract(uint id, string symbol, long first, long last) => new()
    {
        InstrumentId = id,
        Symbol = symbol,
        TickRaw = TapeFixture.TickRaw,
        TickValue = 12.5m,
        Year = 2026,
        Month = 12,
        FirstTrade = first,
        LastTrade = last
    };

    private static long Nanos(int year, int month, int day) =>
        (new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero) - DateTimeOffset.UnixEpoch).Ticks * 100;
}
