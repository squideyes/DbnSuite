using DbnSuite.Client.Tape;
using DbnSuite.UnitTests.ClientTests;
using Shouldly;

namespace DbnSuite.UnitTests.TapeFormatTests;

public sealed class TapeBuilderTests : IDisposable
{
    private const string Ohlcv = "ohlcv-1s";
    private const string Front = "ESZ6";
    private const string Next = "ESH7";
    private const uint FrontId = 11;
    private const uint NextId = 12;
    private const long Price = TapeFixture.BasePrice * TapeFixture.TickRaw;

    private static readonly DateOnly TradeDate = TapeFixture.TradeDate;

    private readonly ArchiveFolder archive = new();
    private readonly string tapes = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public TapeBuilderTests()
    {
        var evening = (ulong)(TapeFixture.OpenSeconds + 60) * TapeFixture.NanosPerSecond;
        var midday = (ulong)(TapeFixture.OpenSeconds + 18 * 3_600) * TapeFixture.NanosPerSecond;

        archive.Add(Ohlcv, TapeFixture.Root, TradeDate.AddDays(-1), Bar(FrontId, evening), Bar(NextId, evening));
        archive.Add(Ohlcv, TapeFixture.Root, TradeDate, Bar(FrontId, midday), Bar(NextId, midday));
    }

    [Fact]
    public async Task ADateHasOneTapeOfTheContractItHolds()
    {
        var builder = new TapeBuilder(archive.Open(), CatalogHolding(Next), tapes);

        var built = await builder.BuildAsync(TradeDate, compress: false, CancellationToken.None);

        var tape = built.Value.Tapes.ShouldHaveSingleItem();
        tape.Error.ShouldBeNull();
        tape.Symbol.ShouldBe(Next);
        tape.Path.ShouldBe(TapePaths.For(tapes, TapeFixture.Dataset, TapeKind.S1, TapeFixture.Root, TradeDate));
        Directory.EnumerateFiles(tapes, "*.tape", SearchOption.AllDirectories).ShouldHaveSingleItem();
        (await Tape.LoadAsync(tape.Path!, CancellationToken.None)).Value.Header.InstrumentId.ShouldBe(NextId);
    }

    [Fact]
    public async Task ARebuildDeletesATapeOfAKindItDidNotMake()
    {
        var stale = TapePaths.For(tapes, TapeFixture.Dataset, TapeKind.L1, TapeFixture.Root, TradeDate);
        Directory.CreateDirectory(Path.GetDirectoryName(stale)!);
        File.WriteAllBytes(stale, [1, 2, 3]);
        var builder = new TapeBuilder(archive.Open(), CatalogHolding(Next), tapes);

        var built = await builder.BuildAsync(TradeDate, compress: false, CancellationToken.None);

        File.Exists(stale).ShouldBeFalse();
        built.Value.Removed.ShouldBe([stale]);
    }

    [Fact]
    public async Task ADateThatHoldsNoContractHasNoTapes()
    {
        var builder = new TapeBuilder(archive.Open(), CatalogHolding(null), tapes);

        var built = await builder.BuildAsync(TradeDate, compress: false, CancellationToken.None);

        built.Value.Tapes.ShouldBeEmpty();
        Directory.Exists(tapes).ShouldBeFalse();
    }

    [Fact]
    public void ATapeIsNamedByRootKindAndDate()
    {
        var path = TapePaths.For(tapes, TapeFixture.Dataset, TapeKind.L1, TapeFixture.Root, TradeDate);

        var date = TapePaths.TradeDateOf(path);

        Path.GetFileName(path).ShouldBe("ES.l1.20261029.tape");
        date.ShouldBe(TradeDate);
    }

    public void Dispose()
    {
        archive.Dispose();

        if (Directory.Exists(tapes))
            Directory.Delete(tapes, recursive: true);
    }

    private static byte[] Bar(uint id, ulong time) => DbnBytes.Ohlcv(id, time, Price, Price, Price, Price, 5);

    private static Catalog CatalogHolding(string? held) => new(TapeFixture.Dataset, TapeFixture.Root,
        [Contract(FrontId, Front, 2026), Contract(NextId, Next, 2027)],
        [TapeFixture.FullDay(TradeDate) with { Held = held }]);

    private static CatalogContract Contract(uint id, string symbol, int year) => new()
    {
        InstrumentId = id,
        Symbol = symbol,
        TickRaw = TapeFixture.TickRaw,
        TickValue = 12.5m,
        Year = year,
        Month = 12,
        FirstTrade = 0,
        LastTrade = long.MaxValue
    };
}
