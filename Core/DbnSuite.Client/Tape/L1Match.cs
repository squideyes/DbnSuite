namespace DbnSuite.Client.Tape;

/// <summary>One row of the L1 stream DbnClient's own transform gives, in a tape's ticks.</summary>
internal readonly record struct L1Row(bool IsTrade, long Time, int Price, uint Size, TradeSide Side, int Bid, uint BidSize, int Ask, uint AskSize);

/// <summary>
/// Walks an L1 tape beside the transform's rows as they arrive, event for event. Each row
/// is checked and dropped, so memory stays flat however busy the day was.
/// </summary>
internal sealed class L1Match
{
    private const byte TradingAction = 7;

    private readonly Tape tape;

    private L1Place? place;
    private bool pending;
    private int bid;
    private int ask;
    private uint bidSize;
    private uint askSize;
    private long rowsPastEnd;
    private string? failure;

    public L1Match(Tape l1)
    {
        tape = l1;

        Advance(opening: true);
    }

    /// <summary>Checks the transform's next row against the tape.</summary>
    public void Row(in L1Row row)
    {
        if (failure is not null)
            return;

        if (!pending)
        {
            if (rowsPastEnd > 0 || !Unwritten(row, TradingAction, bid, bidSize, ask, askSize))
                rowsPastEnd++;

            return;
        }

        var at = place!.Value;

        if (Unwritten(row, at.State, bid, bidSize, ask, askSize))
            return;

        if (at.Kind == L1Kind.Trade)
        {
            if (!row.IsTrade || row.Time != at.Time || row.Price != at.TradePrice || row.Size != at.TradeSize || row.Side != at.Side)
            {
                failure = $"the trade at {at.Time} differs: {row}";
                return;
            }
        }
        else
        {
            (bid, bidSize, ask, askSize) = (at.BidPrice, at.BidSize, at.AskPrice, at.AskSize);

            if (row.IsTrade || row.Time != at.Time || !SameTop(row, bid, bidSize, ask, askSize))
            {
                failure = $"the quote at {at.Time} differs: {row}";
                return;
            }
        }

        Advance(opening: false);
    }

    /// <summary>
    /// After a snapshot in mid-session, the tape quotes the book's top at the next event even
    /// when that event moved nothing, since the snapshot itself moved it. The transform compares
    /// with the book as the snapshot left it, so it gives no row. That quote is accepted when it
    /// is the tape's next event, at <paramref name="top"/>'s time, with the book's top.
    /// </summary>
    /// <param name="top">The book's top after the event, as a quote row.</param>
    public void AfterSnapshot(in L1Row top)
    {
        if (failure is not null || !pending)
            return;

        var at = place!.Value;

        if (at.Kind != L1Kind.Quote || at.Time != top.Time || !SameTop(top, at.BidPrice, at.BidSize, at.AskPrice, at.AskSize))
            return;

        (bid, bidSize, ask, askSize) = (at.BidPrice, at.BidSize, at.AskPrice, at.AskSize);

        Advance(opening: false);
    }

    /// <summary>Why the tape and the rows part, or null when they agree to the end.</summary>
    public string? Finish()
    {
        if (failure is not null)
            return failure;

        if (pending)
            return $"the tape runs on past the transform at {place!.Value.Time}";

        return rowsPastEnd > 0 ? $"the transform has {rowsPastEnd:N0} rows past the tape's end" : null;
    }

    /// <summary>Moves to the tape's next trade or quote. States only steer the rows' checks.</summary>
    private void Advance(bool opening)
    {
        var cursor = tape.L1Cursor();

        if (place is { } from)
            cursor.MoveTo(from);

        pending = false;

        while (cursor.MoveNext())
        {
            if (cursor.Kind == L1Kind.State)
                continue;

            // The tape's opening quote is the book the transform only ever kept to itself.
            if (opening && cursor.Kind == L1Kind.Quote)
            {
                opening = false;
                (bid, bidSize, ask, askSize) = (cursor.BidPrice, cursor.BidSize, cursor.AskPrice, cursor.AskSize);

                continue;
            }

            pending = true;
            break;
        }

        place = cursor.Here;
    }

    /// <summary>A row the tape rightly has no event for: only its order count moved, or the open's match is still clearing it.</summary>
    private static bool Unwritten(in L1Row row, byte state, int bid, uint bidSize, int ask, uint askSize) =>
        !row.IsTrade && (SameTop(row, bid, bidSize, ask, askSize)
            || (state == TradingAction && row.BidSize > 0 && row.AskSize > 0 && row.Bid >= row.Ask));

    private static bool SameTop(in L1Row row, int bid, uint bidSize, int ask, uint askSize) =>
        row.BidSize == bidSize && row.AskSize == askSize
        && (bidSize == 0 || row.Bid == bid)
        && (askSize == 0 || row.Ask == ask);
}
