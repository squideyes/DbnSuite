using System.Buffers.Binary;
using DbnClient.Tape;
using Shouldly;

namespace DbnSync.UnitTests.TapeFormatTests;

public sealed class ContractRunTests
{
    private const byte MboType = 0xA0;
    private const byte Last = 0x80;
    private const byte Snapshot = 0x20;
    private const byte Trading = 7;
    private const byte Halt = 8;
    private const byte CloseAction = 12;
    private const int Bid = 30_842;
    private const int Ask = 30_843;
    private const long Micro = 1_000;

    private static readonly long Open = TapeFixture.OpenSeconds * TapeFixture.NanosPerSecond;
    private static readonly long Close = TapeFixture.CloseSeconds * TapeFixture.NanosPerSecond;
    private static readonly long RthOpen = Open + 15 * 3_600 * TapeFixture.NanosPerSecond;
    private static readonly long RthClose = RthOpen + 6 * 3_600 * TapeFixture.NanosPerSecond;

    [Fact]
    public async Task TheBookWarmsUpSilentlyAndTheTapeOpensWithStateAndQuote()
    {
        var run = Run();

        Warm(run);
        run.Feed(Record(Open + 5 * Micro, 'A', 5, Ask + 1, 9, 'A', Last));
        run.Finish();

        var steps = await StepsAsync(run);

        steps[0].Kind.ShouldBe(L1Kind.State);
        steps[0].Time.ShouldBe(Open / Micro);
        steps[0].State.ShouldBe(Trading);
        steps[1].Kind.ShouldBe(L1Kind.Quote);
        steps[1].Time.ShouldBe(Open / Micro);
        steps[1].BidPrice.ShouldBe(Bid);
        steps[1].AskPrice.ShouldBe(Ask);
    }

    [Fact]
    public async Task ATradeComesBeforeTheQuoteItCauses()
    {
        var run = Run();
        var time = Open + 800 * Micro;

        Warm(run);
        run.Feed(Record(time, 'T', 0, Ask, 2, 'B', 0));
        run.Feed(Record(time, 'F', 2, Ask, 2, 'A', Last));
        run.Finish();

        var steps = await StepsAsync(run);

        steps[2].Kind.ShouldBe(L1Kind.Trade);
        steps[2].TradePrice.ShouldBe(Ask);
        steps[2].Side.ShouldBe(TradeSide.Buyer);
        steps[3].Kind.ShouldBe(L1Kind.Quote);
        steps[3].AskSize.ShouldBe(23u);
    }

    [Fact]
    public async Task AQuoteWaitsForTheEventsLastRecord()
    {
        var run = Run();
        var time = Open + 800 * Micro;

        Warm(run);
        run.Feed(Record(time, 'C', 2, Ask, 5, 'A', 0));

        run.L1!.EventCount.ShouldBe(2);

        run.Feed(Record(time, 'C', 1, Bid, 10, 'B', Last));
        run.Finish();

        var steps = await StepsAsync(run);

        steps.Count(s => s.Kind == L1Kind.Quote).ShouldBe(2);
        steps[2].AskSize.ShouldBe(20u);
        steps[2].BidSize.ShouldBe(30u);
    }

    [Fact]
    public async Task TheSessionsCloseEndsTheTape()
    {
        var run = Run();

        Warm(run);
        run.Feed(Record(Close, 'T', 0, Ask, 2, 'B', Last));
        run.Feed(Record(Close + Micro, 'T', 0, Ask, 2, 'B', Last));

        var steps = await StepsAsync(run);

        steps.Count(s => s.Kind == L1Kind.Trade).ShouldBe(0);
        steps[^1].Kind.ShouldBe(L1Kind.State);
        steps[^1].State.ShouldBe(CloseAction);
    }

    [Fact]
    public async Task AStateFallsInOrderBetweenEvents()
    {
        var halted = Open + 10 * Micro;
        var run = Run(states: [(Open, Trading), (halted, Halt), (Close, CloseAction)]);

        Warm(run);
        run.Feed(Record(Open + 5 * Micro, 'C', 2, Ask, 5, 'A', Last));
        run.Feed(Record(Open + 15 * Micro, 'C', 1, Bid, 5, 'B', Last));
        run.Finish();

        var steps = await StepsAsync(run);

        steps.Select(s => s.Kind).ShouldBe([L1Kind.State, L1Kind.Quote, L1Kind.Quote, L1Kind.State, L1Kind.Quote, L1Kind.State]);
        steps[3].State.ShouldBe(Halt);
        steps[3].Time.ShouldBe(halted / Micro);
    }

    [Fact]
    public async Task ACrossedOpenWaitsForItsMatch()
    {
        var run = Run();
        var time = Open + 100 * Micro;

        run.Feed(Record(Open - Micro, 'A', 1, Ask + 5, 10, 'B', Last));
        run.Feed(Record(Open - Micro, 'A', 2, Ask, 10, 'A', Last));
        run.Feed(Record(time, 'T', 0, Ask, 10, 'B', 0));
        run.Feed(Record(time, 'F', 2, Ask, 10, 'A', 0));
        run.Feed(Record(time, 'C', 1, Ask + 5, 10, 'B', 0));
        run.Feed(Record(time, 'A', 3, Bid, 4, 'B', 0));
        run.Feed(Record(time, 'A', 4, Ask, 6, 'A', Last));
        run.Finish();

        run.Refusal.ShouldBeNull();

        var steps = await StepsAsync(run);

        steps.Select(s => s.Kind).ShouldBe([L1Kind.State, L1Kind.Trade, L1Kind.Quote, L1Kind.State]);
        steps[2].BidPrice.ShouldBe(Bid);
        steps[2].AskPrice.ShouldBe(Ask);
    }

    [Fact]
    public void ABookThatCrossesDuringTradingIsRefused()
    {
        var run = Run();
        var time = Open + 100 * Micro;

        Warm(run);
        run.Feed(Record(time, 'A', 3, Ask + 1, 1, 'B', Last));

        run.Refusal!.Code.ShouldBe(TapeErrors.Refused);
    }

    [Fact]
    public async Task ACrossedBookOutsideTradingIsAllowed()
    {
        var halted = Open + 10 * Micro;
        var run = Run(states: [(Open, Trading), (halted, Halt)]);

        Warm(run);
        run.Feed(Record(halted + Micro, 'A', 3, Ask + 1, 1, 'B', Last));
        run.Finish();

        run.Refusal.ShouldBeNull();

        var steps = await StepsAsync(run);

        steps[^1].BidPrice.ShouldBe(Ask + 1);
    }

    [Fact]
    public async Task ASnapshotAtTheFileBoundaryWritesNothingWhenTheTopHolds()
    {
        var run = Run();

        Warm(run);

        var before = run.L1!.EventCount;

        Resnapshot(run, Bid, Ask);
        run.Feed(Record(Open + 20 * Micro, 'N', 0, 0, 0, 'N', Last));

        run.BoundaryChanges.ShouldBe(0);
        run.L1.EventCount.ShouldBe(before);
    }

    [Fact]
    public async Task ASnapshotThatMovesTheTopIsCountedAndQuoted()
    {
        var run = Run();

        Warm(run);
        Resnapshot(run, Bid - 1, Ask);
        run.Feed(Record(Open + 20 * Micro, 'N', 0, 0, 0, 'N', Last));
        run.Finish();

        run.BoundaryChanges.ShouldBe(1);

        var steps = await StepsAsync(run);

        steps[^1].BidPrice.ShouldBe(Bid - 1);
    }

    [Fact]
    public void APriceOffTheTickIsRefused()
    {
        var run = Run();

        run.Feed(RawRecord(Open - Micro, 'A', 1, TapeFixture.TickRaw * Bid + 1, 5, 'B', Last));

        run.Refusal!.Code.ShouldBe(TapeErrors.Refused);
    }

    [Fact]
    public void ARecordWithNoPriceMovesNothing()
    {
        var run = Run();

        run.Feed(RawRecord(Open - Micro, 'A', 1, long.MaxValue, 5, 'B', Last));

        run.Refusal.ShouldBeNull();
    }

    [Fact]
    public void AnOrderFarFromTheMarketIsNoProblem()
    {
        var run = Run();

        Warm(run);
        run.Feed(Record(Open + Micro, 'A', 3, 31_397_600, 1, 'A', Last));

        run.Refusal.ShouldBeNull();
    }

    [Fact]
    public async Task RthMarksFollowTheHours()
    {
        var run = Run();

        Warm(run);
        run.Feed(Record(RthOpen - Micro, 'C', 2, Ask, 1, 'A', Last));
        run.Feed(Record(RthOpen, 'C', 2, Ask, 1, 'A', Last));
        run.Feed(Record(RthOpen + Micro, 'T', 0, Ask, 1, 'B', Last));
        run.Feed(Record(RthClose, 'C', 2, Ask, 1, 'A', Last));
        run.Finish();

        var l1 = await TapeFixture.RoundTripAsync(run.L1!);
        var trades = await TapeFixture.RoundTripAsync(run.Trades!);

        l1.Header.HasRth.ShouldBeTrue();
        l1.Header.RthStart.EventIndex.ShouldBe(3);
        l1.Header.RthEndIndex.ShouldBe(5);
        trades.Header.RthStart.EventIndex.ShouldBe(0);
        trades.Header.RthEndIndex.ShouldBe(1);
    }

    [Fact]
    public void FilesThatHoldTheContractAreCounted()
    {
        var run = Run();

        Warm(run);
        run.NextFile();
        run.NextFile();

        run.FilesHeld.ShouldBe(1);
    }

    [Fact]
    public async Task TheWritersChecksumMatchesTheDecodedTape()
    {
        var run = Run();

        Warm(run);
        run.Feed(Record(Open + 800 * Micro, 'T', 0, Ask, 2, 'B', 0));
        run.Feed(Record(Open + 800 * Micro, 'F', 2, Ask, 2, 'A', Last));
        run.Finish();

        var tape = await TapeFixture.RoundTripAsync(run.L1!);

        TapeChecksum.Of(tape).ShouldBe(run.L1!.Checksum);
    }

    private static ContractRun Run(IReadOnlyList<(long Time, byte Action)>? states = null)
    {
        var day = new SessionDay(TapeFixture.TradeDate, Open, Close, [], RthOpen, RthClose);

        return new ContractRun(TapeFixture.Info(), day, states ?? [(Open, Trading), (Close, CloseAction)]);
    }

    /// <summary>A book of 40 at the bid and 25 at the offer, then the open, with one event just after it.</summary>
    private static void Warm(ContractRun run)
    {
        run.Feed(Record(Open - Micro, 'A', 1, Bid, 40, 'B', Last));
        run.Feed(Record(Open - Micro, 'A', 2, Ask, 25, 'A', Last));
        run.Feed(Record(Open, 'N', 0, 0, 0, 'N', Last));
    }

    private static void Resnapshot(ContractRun run, int bid, int ask)
    {
        run.Feed(Record(Open, 'R', 0, 0, 0, 'N', Snapshot));
        run.Feed(Record(Open, 'A', 1, bid, 40, 'B', Snapshot));
        run.Feed(Record(Open, 'A', 2, ask, 25, 'A', Snapshot));
    }

    private static async Task<List<L1CursorTests.Step>> StepsAsync(ContractRun run) =>
        L1CursorTests.Read(await TapeFixture.RoundTripAsync(run.L1!));

    private static byte[] Record(long time, char action, ulong orderId, int price, uint size, char side, byte flags) =>
        RawRecord(time, action, orderId, price * TapeFixture.TickRaw, size, side, flags);

    private static byte[] RawRecord(long time, char action, ulong orderId, long raw, uint size, char side, byte flags)
    {
        var record = new byte[56];

        record[0] = 14;
        record[1] = MboType;
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4), TapeFixture.InstrumentId);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(8), time);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(16), orderId);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(24), raw);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(32), size);
        record[36] = flags;
        record[38] = (byte)action;
        record[39] = (byte)side;
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(40), time);

        return record;
    }
}
