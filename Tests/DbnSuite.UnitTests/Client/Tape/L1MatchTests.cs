using DbnSuite.Client.Tape;
using Shouldly;

namespace DbnSuite.UnitTests.TapeFormatTests;

public sealed class L1MatchTests
{
    private const int RandomDays = 2_000;
    private const int MostSteps = 40;
    private const byte Trading = 7;
    private const byte Halt = 8;
    private const uint Lots = 10;

    [Fact]
    public async Task RowsThatMatchTheTapePass()
    {
        var writer = new L1Writer(TapeFixture.Info());
        var time = TapeFixture.OpenMicros;

        writer.Quote(time, TapeFixture.BasePrice, Lots, TapeFixture.BasePrice + 1, Lots);
        writer.State(time, Trading);
        writer.Trade(time + 1, TapeFixture.BasePrice + 1, 2, TradeSide.Buyer);
        writer.Quote(time + 1, TapeFixture.BasePrice, Lots, TapeFixture.BasePrice + 1, Lots - 2);

        var match = new L1Match(await TapeFixture.RoundTripAsync(writer));

        match.Row(Trade(time + 1, TapeFixture.BasePrice + 1, 2, TradeSide.Buyer));
        match.Row(Quote(time + 1, TapeFixture.BasePrice, Lots, TapeFixture.BasePrice + 1, Lots));
        match.Row(Quote(time + 1, TapeFixture.BasePrice, Lots, TapeFixture.BasePrice + 1, Lots - 2));

        match.Finish().ShouldBeNull();
    }

    [Fact]
    public async Task ATradeOfAnotherSizeFails()
    {
        var writer = new L1Writer(TapeFixture.Info());
        var time = TapeFixture.OpenMicros;

        writer.Trade(time, TapeFixture.BasePrice, 2, TradeSide.Seller);

        var match = new L1Match(await TapeFixture.RoundTripAsync(writer));

        match.Row(Trade(time, TapeFixture.BasePrice, 3, TradeSide.Seller));

        match.Finish().ShouldNotBeNull().ShouldStartWith($"the trade at {time} differs");
    }

    [Fact]
    public async Task RowsAfterTheTapeEndsAreCounted()
    {
        var writer = new L1Writer(TapeFixture.Info());
        var time = TapeFixture.OpenMicros;

        writer.Trade(time, TapeFixture.BasePrice, 2, TradeSide.Seller);

        var match = new L1Match(await TapeFixture.RoundTripAsync(writer));

        match.Row(Trade(time, TapeFixture.BasePrice, 2, TradeSide.Seller));
        match.Row(Trade(time, TapeFixture.BasePrice, 1, TradeSide.Seller));
        match.Row(Quote(time, TapeFixture.BasePrice, Lots, TapeFixture.BasePrice + 1, Lots));

        match.Finish().ShouldBe("the transform has 2 rows past the tape's end");
    }

    [Fact]
    public async Task AQuoteASnapshotMovedIsAcceptedAtTheNextEvent()
    {
        var writer = new L1Writer(TapeFixture.Info());
        var time = TapeFixture.OpenMicros;
        var moved = TapeFixture.BasePrice - 1;

        writer.Quote(time, TapeFixture.BasePrice, Lots, TapeFixture.BasePrice + 1, Lots);
        writer.Quote(time + 5, moved, Lots, TapeFixture.BasePrice + 1, Lots);
        writer.Trade(time + 9, TapeFixture.BasePrice + 1, 2, TradeSide.Buyer);

        var match = new L1Match(await TapeFixture.RoundTripAsync(writer));

        match.AfterSnapshot(Quote(time + 5, moved, Lots, TapeFixture.BasePrice + 1, Lots));
        match.Row(Trade(time + 9, TapeFixture.BasePrice + 1, 2, TradeSide.Buyer));

        match.Finish().ShouldBeNull();
    }

    [Fact]
    public async Task AQuoteAfterASnapshotUnlikeTheBookStillFails()
    {
        var writer = new L1Writer(TapeFixture.Info());
        var time = TapeFixture.OpenMicros;

        writer.Quote(time, TapeFixture.BasePrice, Lots, TapeFixture.BasePrice + 1, Lots);
        writer.Quote(time + 5, TapeFixture.BasePrice - 1, Lots, TapeFixture.BasePrice + 1, Lots);

        var match = new L1Match(await TapeFixture.RoundTripAsync(writer));

        match.AfterSnapshot(Quote(time + 5, TapeFixture.BasePrice - 2, Lots, TapeFixture.BasePrice + 1, Lots));

        match.Finish().ShouldBe($"the tape runs on past the transform at {time + 5}");
    }

    [Fact]
    public async Task ItAgreesWithCheckingTheWholeDayAtOnce()
    {
        var random = new Random(20261107);
        var failures = 0;

        for (var day = 0; day < RandomDays; day++)
        {
            var (tape, rows) = await DayAsync(random);
            var match = new L1Match(tape);

            foreach (var row in rows)
                match.Row(row);

            var streamed = match.Finish();

            streamed.ShouldBe(WholeDay(tape, rows), $"day {day}");

            if (streamed is not null)
                failures++;
        }

        failures.ShouldBeInRange(RandomDays / 10, RandomDays * 9 / 10);
    }

    /// <summary>A random tape and the transform's rows for it, sometimes spoiled.</summary>
    private static async Task<(Tape Tape, List<L1Row> Rows)> DayAsync(Random random)
    {
        var writer = new L1Writer(TapeFixture.Info());
        var rows = new List<L1Row>();
        var time = TapeFixture.OpenMicros;
        var bid = TapeFixture.BasePrice;
        var ask = TapeFixture.BasePrice + 1;
        var bidLots = Lots;
        var askLots = Lots;

        if (random.Next(2) == 0)
            writer.Quote(time, bid, bidLots, ask, askLots);

        for (var step = random.Next(MostSteps); step > 0; step--)
        {
            time += random.Next(3);

            switch (random.Next(6))
            {
                case 0:
                    writer.State(time, random.Next(2) == 0 ? Trading : Halt);
                    break;

                case 1:
                    var price = bid + random.Next(-1, 3);
                    var size = (uint)random.Next(1, 12);
                    var side = (TradeSide)random.Next(3);

                    writer.Trade(time, price, size, side);
                    rows.Add(Trade(time, price, size, side));
                    break;

                case 2 or 3:
                    bid += random.Next(-1, 2);
                    ask = Math.Max(ask + random.Next(-1, 2), bid + 1);
                    bidLots = (uint)random.Next(0, 4) * Lots;
                    askLots = (uint)random.Next(0, 4) * Lots;

                    writer.Quote(time, bid, bidLots, ask, askLots);
                    rows.Add(Quote(time, bid, bidLots, ask, askLots));
                    break;

                case 4:
                    rows.Add(Quote(time, bid, bidLots, ask, askLots));
                    break;

                default:
                    rows.Add(Quote(time, ask + 1, Lots, ask, Lots));
                    break;
            }
        }

        if (writer.EventCount == 0)
            writer.State(time, Trading);

        Spoil(random, rows);

        return (await TapeFixture.RoundTripAsync(writer), rows);
    }

    private static void Spoil(Random random, List<L1Row> rows)
    {
        var at = rows.Count == 0 ? 0 : random.Next(rows.Count);

        switch (random.Next(8))
        {
            case 0 when rows.Count > 0:
                rows.RemoveAt(at);
                break;

            case 1 when rows.Count > 0:
                rows.Insert(at, rows[at]);
                break;

            case 2 when rows.Count > 0:
                rows[at] = rows[at] with { Time = rows[at].Time + 1 };
                break;

            case 3 when rows.Count > 0:
                rows[at] = rows[at] with { Size = rows[at].Size + 1, BidSize = rows[at].BidSize + 1 };
                break;

            case 4:
                rows.Add(Trade(TapeFixture.OpenMicros, TapeFixture.BasePrice, 1, TradeSide.None));
                break;
        }
    }

    /// <summary>The check as it was: every row collected first, then the tape walked against them.</summary>
    private static string? WholeDay(Tape tape, List<L1Row> expected)
    {
        var cursor = tape.L1Cursor();
        var bid = 0;
        var ask = 0;
        var bidSize = 0u;
        var askSize = 0u;
        var index = 0;
        var opening = true;

        while (cursor.MoveNext())
        {
            if (cursor.Kind == L1Kind.State)
                continue;

            if (opening && cursor.Kind == L1Kind.Quote)
            {
                opening = false;
                (bid, bidSize, ask, askSize) = (cursor.BidPrice, cursor.BidSize, cursor.AskPrice, cursor.AskSize);

                continue;
            }

            opening = false;

            while (index < expected.Count && Unwritten(expected[index], cursor.State, bid, bidSize, ask, askSize))
                index++;

            if (index == expected.Count)
                return $"the tape runs on past the transform at {cursor.Time}";

            var next = expected[index++];

            if (cursor.Kind == L1Kind.Trade)
            {
                if (!next.IsTrade || next.Time != cursor.Time || next.Price != cursor.TradePrice || next.Size != cursor.TradeSize || next.Side != cursor.Side)
                    return $"the trade at {cursor.Time} differs: {next}";

                continue;
            }

            (bid, bidSize, ask, askSize) = (cursor.BidPrice, cursor.BidSize, cursor.AskPrice, cursor.AskSize);

            if (next.IsTrade || next.Time != cursor.Time || !SameTop(next, bid, bidSize, ask, askSize))
                return $"the quote at {cursor.Time} differs: {next}";
        }

        while (index < expected.Count && Unwritten(expected[index], Trading, bid, bidSize, ask, askSize))
            index++;

        return index == expected.Count ? null : $"the transform has {expected.Count - index:N0} rows past the tape's end";
    }

    private static bool Unwritten(in L1Row row, byte state, int bid, uint bidSize, int ask, uint askSize) =>
        !row.IsTrade && (SameTop(row, bid, bidSize, ask, askSize)
            || (state == Trading && row.BidSize > 0 && row.AskSize > 0 && row.Bid >= row.Ask));

    private static bool SameTop(in L1Row row, int bid, uint bidSize, int ask, uint askSize) =>
        row.BidSize == bidSize && row.AskSize == askSize
        && (bidSize == 0 || row.Bid == bid)
        && (askSize == 0 || row.Ask == ask);

    private static L1Row Trade(long time, int price, uint size, TradeSide side) => new(true, time, price, size, side, 0, 0, 0, 0);

    private static L1Row Quote(long time, int bid, uint bidSize, int ask, uint askSize) => new(false, time, 0, 0, TradeSide.None, bid, bidSize, ask, askSize);
}
