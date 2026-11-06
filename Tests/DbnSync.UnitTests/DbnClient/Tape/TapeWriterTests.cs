using DbnClient.Tape;
using Shouldly;

namespace DbnSync.UnitTests.TapeFormatTests;

public sealed class TapeWriterTests : IDisposable
{
    private const int Price = 100;
    private const int EventsBeforeRth = 1_000;
    private const int EventsInRth = 2_000;
    private const int EventsAfterRth = 500;
    private const int Seed = 11;
    private const string TapeName = "ESZ6.l1.20261029.tape";

    private readonly string folder = Path.Combine(Path.GetTempPath(), $"tape-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public void ABarBeforeTheSessionIsRefused()
    {
        var writer = new S1Writer(TapeFixture.Info());

        var added = writer.Add(TapeFixture.OpenMicros - TapeFixture.MicrosPerSecond, Price, Price, Price, Price, 1);

        added.ShouldBeFalse();
        writer.Refusal!.Code.ShouldBe(TapeErrors.Refused);
    }

    [Fact]
    public void ABarAfterTheCloseIsRefused()
    {
        var writer = new S1Writer(TapeFixture.Info());

        var added = writer.Add(TapeFixture.CloseMicros + TapeFixture.MicrosPerSecond, Price, Price, Price, Price, 1);

        added.ShouldBeFalse();
    }

    [Fact]
    public void TwoBarsInOneSecondAreRefused()
    {
        var writer = new S1Writer(TapeFixture.Info());

        writer.Add(TapeFixture.OpenMicros, Price, Price, Price, Price, 1);

        var added = writer.Add(TapeFixture.OpenMicros, Price, Price, Price, Price, 1);

        added.ShouldBeFalse();
    }

    [Fact]
    public void ABarOffTheSecondIsRefused()
    {
        var writer = new S1Writer(TapeFixture.Info());

        var added = writer.Add(TapeFixture.OpenMicros + 1, Price, Price, Price, Price, 1);

        added.ShouldBeFalse();
    }

    [Theory]
    [InlineData(100, 99, 100, 100)]
    [InlineData(100, 100, 101, 100)]
    [InlineData(100, 105, 95, 106)]
    [InlineData(100, 105, 95, 94)]
    public void ABarMustHoldItsOpenAndClose(int open, int high, int low, int close)
    {
        var writer = new S1Writer(TapeFixture.Info());

        var added = writer.Add(TapeFixture.OpenMicros, open, high, low, close, 1);

        added.ShouldBeFalse();
    }

    [Fact]
    public void TradeTimeCannotRunBackward()
    {
        var writer = new TradeWriter(TapeFixture.Info());

        writer.Add(TapeFixture.OpenMicros + 5, Price, 1, TradeSide.Buyer);

        var added = writer.Add(TapeFixture.OpenMicros + 4, Price, 1, TradeSide.Buyer);

        added.ShouldBeFalse();
    }

    [Fact]
    public void L1TimeCannotRunBackward()
    {
        var writer = new L1Writer(TapeFixture.Info());

        writer.Trade(TapeFixture.OpenMicros + 5, Price, 1, TradeSide.Buyer);

        var added = writer.Quote(TapeFixture.OpenMicros + 4, Price, 1, Price, 1);

        added.ShouldBeFalse();
    }

    [Fact]
    public void TheReservedSideIsRefused()
    {
        var writer = new TradeWriter(TapeFixture.Info());

        var added = writer.Add(TapeFixture.OpenMicros, Price, 1, (TradeSide)3);

        added.ShouldBeFalse();
    }

    [Fact]
    public void ActionZeroIsRefused()
    {
        var writer = new L1Writer(TapeFixture.Info());

        var added = writer.State(TapeFixture.OpenMicros, 0);

        added.ShouldBeFalse();
    }

    [Theory]
    [InlineData("A-DATASET-NAME-TOO-LONG", TapeFixture.Symbol, TapeFixture.Root)]
    [InlineData(TapeFixture.Dataset, "A-SYMBOL-NAME-TOO-LONG", TapeFixture.Root)]
    [InlineData(TapeFixture.Dataset, TapeFixture.Symbol, "ROOT-TOO-LONG")]
    [InlineData(TapeFixture.Dataset, "", TapeFixture.Root)]
    [InlineData(TapeFixture.Dataset, "ES Z6", TapeFixture.Root)]
    public void NamesMustFitTheHeader(string dataset, string symbol, string root)
    {
        var info = TapeFixture.Info() with { Dataset = dataset, Symbol = symbol, Root = root };

        var writer = new TradeWriter(info);

        writer.Refusal!.Code.ShouldBe(TapeErrors.Refused);
    }

    [Fact]
    public void ASessionMustOpenBeforeItCloses()
    {
        var info = TapeFixture.Info();

        var writer = new TradeWriter(info with { CloseTime = info.OpenTime - 1 });

        writer.Refusal!.Code.ShouldBe(TapeErrors.Refused);
    }

    [Fact]
    public void ARefusalStopsTheWriterForGood()
    {
        var writer = new TradeWriter(TapeFixture.Info());

        writer.Add(TapeFixture.OpenMicros + 5, Price, 1, TradeSide.Buyer);
        writer.Add(TapeFixture.OpenMicros, Price, 1, TradeSide.Buyer);

        var count = writer.EventCount;
        var added = writer.Add(TapeFixture.OpenMicros + 9, Price, 1, TradeSide.Buyer);

        added.ShouldBeFalse();
        writer.EventCount.ShouldBe(count);
        writer.MarkRthStart().ShouldBeFalse();
        writer.MarkRthEnd().ShouldBeFalse();
    }

    [Fact]
    public async Task ARefusedTapeLeavesNoFile()
    {
        var path = Path.Combine(folder, TapeName);
        var writer = new TradeWriter(TapeFixture.Info());

        writer.Add(TapeFixture.OpenMicros - 1, Price, 1, TradeSide.Buyer);

        var saved = await writer.SaveAsync(path, CancellationToken.None);

        saved.Error!.Code.ShouldBe(TapeErrors.Refused);
        Directory.GetFiles(folder).ShouldBeEmpty();
    }

    [Fact]
    public async Task ATapeWithNoEventsIsNotWritten()
    {
        var path = Path.Combine(folder, TapeName);
        var writer = new L1Writer(TapeFixture.Info());

        var saved = await writer.SaveAsync(path, CancellationToken.None);

        saved.Error!.Code.ShouldBe(TapeErrors.Empty);
        Directory.GetFiles(folder).ShouldBeEmpty();
    }

    [Fact]
    public async Task SavingLeavesOnlyTheTape()
    {
        var path = Path.Combine(folder, TapeName);
        var writer = Filled();

        var saved = await writer.SaveAsync(path, CancellationToken.None);
        var loaded = await Tape.LoadAsync(path, CancellationToken.None);

        saved.IsSuccess.ShouldBeTrue();
        loaded.Value.Header.ShouldBe(saved.Value);
        Directory.GetFiles(folder).ShouldBe([path]);
    }

    [Fact]
    public async Task SavingReplacesAnOldTape()
    {
        var path = Path.Combine(folder, TapeName);

        await Filled().SaveAsync(path, CancellationToken.None);

        var writer = new TradeWriter(TapeFixture.Info());

        writer.Add(TapeFixture.OpenMicros, Price, 1, TradeSide.Buyer);

        await writer.SaveAsync(path, CancellationToken.None);

        var loaded = await Tape.LoadAsync(path, CancellationToken.None);

        loaded.Value.Header.Kind.ShouldBe(TapeKind.Trade);
    }

    [Fact]
    public async Task ACancelledSaveLeavesNoFile()
    {
        var path = Path.Combine(folder, TapeName);
        using var source = new CancellationTokenSource();

        await source.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => Filled().SaveAsync(path, source.Token));

        Directory.GetFiles(folder).ShouldBeEmpty();
    }

    [Fact]
    public void RthCannotEndBeforeItStarts()
    {
        var writer = new L1Writer(TapeFixture.Info());

        var marked = writer.MarkRthEnd();

        marked.ShouldBeFalse();
    }

    [Fact]
    public void RthCannotStartTwice()
    {
        var writer = new L1Writer(TapeFixture.Info());

        writer.MarkRthStart();

        var marked = writer.MarkRthStart();

        marked.ShouldBeFalse();
    }

    [Fact]
    public void RthCannotEndTwice()
    {
        var writer = new L1Writer(TapeFixture.Info());

        writer.MarkRthStart();
        writer.MarkRthEnd();

        var marked = writer.MarkRthEnd();

        marked.ShouldBeFalse();
    }

    [Fact]
    public async Task ATapeWithoutMarksSaysSo()
    {
        var tape = await TapeFixture.RoundTripAsync(Filled());

        tape.Header.HasRth.ShouldBeFalse();
    }

    [Fact]
    public async Task AnUnmarkedEndFallsAtTheClose()
    {
        var writer = new TradeWriter(TapeFixture.Info());

        writer.MarkRthStart();
        writer.Add(TapeFixture.OpenMicros, Price, 1, TradeSide.Buyer);

        var tape = await TapeFixture.RoundTripAsync(writer);

        tape.Header.RthEndIndex.ShouldBe(1);
        tape.Header.RthEndOffset.ShouldBe(tape.Header.BodyLength);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnS1MarkResumesExactly(bool compress)
    {
        var bars = S1CursorTests.RandomStream(Seed, EventsBeforeRth + EventsInRth + EventsAfterRth);
        var writer = new S1Writer(TapeFixture.Info(compress));

        for (var i = 0; i < bars.Count; i++)
        {
            if (i == EventsBeforeRth)
                writer.MarkRthStart();

            if (i == EventsBeforeRth + EventsInRth)
                writer.MarkRthEnd();

            var bar = bars[i];

            writer.Add(bar.Time, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume);
        }

        var tape = await TapeFixture.RoundTripAsync(writer);
        var header = tape.Header;
        var cursor = new S1Cursor(tape.Body, header.RthStart, header.RthEndIndex, header.RthEndOffset);
        var read = new List<S1CursorTests.Bar>();

        while (cursor.MoveNext())
            read.Add(new S1CursorTests.Bar(cursor.Time, cursor.Open, cursor.High, cursor.Low, cursor.Close, cursor.Volume));

        read.ShouldBe(bars.Skip(EventsBeforeRth).Take(EventsInRth));
    }

    [Fact]
    public async Task ATradeMarkResumesExactly()
    {
        var prints = TradeCursorTests.RandomStream(Seed, EventsBeforeRth + EventsInRth + EventsAfterRth);
        var writer = new TradeWriter(TapeFixture.Info());

        for (var i = 0; i < prints.Count; i++)
        {
            if (i == EventsBeforeRth)
                writer.MarkRthStart();

            if (i == EventsBeforeRth + EventsInRth)
                writer.MarkRthEnd();

            var print = prints[i];

            writer.Add(print.Time, print.Price, print.Size, print.Side);
        }

        var tape = await TapeFixture.RoundTripAsync(writer);
        var header = tape.Header;
        var cursor = new TradeCursor(tape.Body, header.RthStart, header.RthEndIndex, header.RthEndOffset);
        var read = new List<TradeCursorTests.Print>();

        while (cursor.MoveNext())
            read.Add(new TradeCursorTests.Print(cursor.Time, cursor.Price, cursor.Size, cursor.Side));

        read.ShouldBe(prints.Skip(EventsBeforeRth).Take(EventsInRth));
    }

    [Fact]
    public async Task AnL1MarkResumesExactly()
    {
        var writer = new L1Writer(TapeFixture.Info());

        var before = L1CursorTests.FillRandom(writer, Seed, EventsBeforeRth);

        writer.MarkRthStart();

        var tape = await TapeFixture.RoundTripAsync(writer);
        var start = tape.Header.RthStart;

        start.EventIndex.ShouldBe(before.Count);
        start.BodyOffset.ShouldBe(tape.Header.BodyLength);
        start.Time.ShouldBe(before[^1].Time);
        start.BidPrice.ShouldBe(before[^1].BidPrice);
        start.AskPrice.ShouldBe(before[^1].AskPrice);
        start.BidSize.ShouldBe(before[^1].BidSize);
        start.AskSize.ShouldBe(before[^1].AskSize);
        start.LastPrice.ShouldBe(before[^1].TradePrice);
        start.MarketState.ShouldBe(before[^1].State);
    }

    [Fact]
    public async Task AnL1MarkInMidStreamResumesExactly()
    {
        var writer = new L1Writer(TapeFixture.Info());

        writer.Quote(TapeFixture.OpenMicros, Price, 4, Price + 1, 5);
        writer.Trade(TapeFixture.OpenMicros + 1, Price + 1, 2, TradeSide.Buyer);
        writer.State(TapeFixture.OpenMicros + 2, 7);
        writer.MarkRthStart();
        writer.Trade(TapeFixture.OpenMicros + 3, Price + 1, 1, TradeSide.Buyer);
        writer.Quote(TapeFixture.OpenMicros + 3, Price, 4, Price + 1, 2);
        writer.Trade(TapeFixture.OpenMicros + 4, Price, 1, TradeSide.Seller);
        writer.MarkRthEnd();
        writer.Quote(TapeFixture.OpenMicros + 9, Price, 3, Price + 1, 2);

        var tape = await TapeFixture.RoundTripAsync(writer);
        var header = tape.Header;
        var whole = L1CursorTests.Read(tape);
        var cursor = new L1Cursor(tape.Body, header.RthStart, header.RthEndIndex, header.RthEndOffset);
        var read = new List<(L1Kind, long, int, uint, int, uint, int)>();

        while (cursor.MoveNext())
            read.Add((cursor.Kind, cursor.Time, cursor.BidPrice, cursor.BidSize, cursor.AskPrice, cursor.AskSize, cursor.TradePrice));

        read.ShouldBe(whole.Skip(3).Take(3).Select(s => (s.Kind, s.Time, s.BidPrice, s.BidSize, s.AskPrice, s.AskSize, s.TradePrice)));
    }

    private static L1Writer Filled()
    {
        var writer = new L1Writer(TapeFixture.Info());

        L1CursorTests.FillRandom(writer, Seed, EventsBeforeRth);

        return writer;
    }
}
