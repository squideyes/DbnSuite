using System.Buffers.Binary;
using DbnClient.Dbn;

namespace DbnClient.Tape;

/// <summary>
/// One contract's way through a trade date's <c>mbo</c> records: the book warms up
/// silently, then trades, quotes and states are written until the session's close.
/// </summary>
internal sealed class ContractRun
{
    private const long NanosPerMicro = 1_000;
    private const byte TradingAction = 7;

    private readonly TapeInfo info;
    private readonly SessionDay day;
    private readonly IReadOnlyList<(long Time, byte Action)> states;
    private readonly TickBook book = new();
    private readonly long rthOpen;
    private readonly long rthClose;

    private int nextState;
    private byte marketState;
    private bool opened;
    private bool finished;
    private bool inSnapshot;
    private bool rthStarted;
    private bool rthEnded;
    private bool uncrossing;
    private BookTop beforeSnapshot;

    public ContractRun(TapeInfo info, SessionDay day, IReadOnlyList<(long Time, byte Action)> states)
    {
        this.info = info;
        this.day = day;
        this.states = states;

        rthOpen = (day.RthOpen ?? long.MaxValue) / NanosPerMicro;
        rthClose = (day.RthClose ?? long.MaxValue) / NanosPerMicro;
    }

    /// <summary>The first thing that stopped this contract, or null.</summary>
    public Error? Refusal { get; private set; }

    /// <summary>Records seen since the last <see cref="NextFile"/>.</summary>
    public long RecordsInFile { get; private set; }

    /// <summary>How many UTC files in a row have held this contract.</summary>
    public int FilesHeld { get; private set; }

    /// <summary>Times a file's opening snapshot changed the top of the book in mid-session.</summary>
    public int BoundaryChanges { get; private set; }

    public L1Writer? L1 { get; private set; }

    public TradeWriter? Trades { get; private set; }

    public void NextFile()
    {
        if (RecordsInFile > 0)
            FilesHeld++;

        RecordsInFile = 0;
    }

    /// <summary>Feeds one <c>mbo</c> record of this contract, as stored.</summary>
    public void Feed(ReadOnlySpan<byte> record)
    {
        RecordsInFile++;

        if (finished || Refusal is not null)
            return;

        var time = (long)BinaryPrimitives.ReadUInt64LittleEndian(record[8..]);
        var flags = record[36];
        var action = (char)record[38];
        var side = (char)record[39];
        var snapshot = (flags & MboRecord.SnapshotFlag) != 0 || action == 'R';

        if (snapshot)
        {
            if (!inSnapshot)
                beforeSnapshot = book.Top();

            inSnapshot = true;
        }
        else
        {
            if (inSnapshot)
                EndSnapshot();

            if (time >= day.Close)
            {
                Finish();
                return;
            }

            if (!opened && time >= day.Open)
                Open();

            if (opened)
                WriteStatesUpTo(time);
        }

        var raw = BinaryPrimitives.ReadInt64LittleEndian(record[24..]);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(record[32..]);

        int? price = null;

        if (action is 'A' or 'M' or 'T' && raw != DbnPrice.Undefined)
        {
            if (raw % info.TickRaw != 0)
            {
                Refuse($"Price {raw} at {time} is not on a whole tick of {info.TickRaw}.");
                return;
            }

            price = checked((int)(raw / info.TickRaw));
        }

        if (action == 'T' && !snapshot && opened)
        {
            if (price is not { } traded)
            {
                Refuse($"The trade at {time} has no price.");
                return;
            }

            WriteTrade(time / NanosPerMicro, traded, size, side);
        }

        book.Apply(action, BinaryPrimitives.ReadUInt64LittleEndian(record[16..]), price, size, side);

        if ((flags & MboRecord.LastFlag) != 0 && !snapshot && opened)
            WriteQuote(time / NanosPerMicro);
    }

    /// <summary>Ends the session, writing whatever states are left up to the close.</summary>
    public void Finish()
    {
        if (finished)
            return;

        finished = true;

        if (Refusal is null && opened)
            WriteStatesUpTo(day.Close);

        Refusal ??= L1?.Refusal ?? Trades?.Refusal;
    }

    private void Open()
    {
        opened = true;

        var top = book.Top();
        var basePrice = top.BidSize > 0 ? top.BidPrice : top.AskSize > 0 ? top.AskPrice : 0;
        var withBase = info with { BasePrice = basePrice };

        L1 = new L1Writer(withBase);
        Trades = new TradeWriter(withBase);

        while (nextState < states.Count && states[nextState].Time <= day.Open)
            Enter(states[nextState++].Action);

        var open = day.Open / NanosPerMicro;

        Mark(open);

        if (marketState != 0)
            L1.State(open, marketState);

        WriteQuote(open);
    }

    private void WriteStatesUpTo(long nanos)
    {
        while (nextState < states.Count && states[nextState].Time <= nanos)
        {
            var (time, action) = states[nextState++];
            var micros = time / NanosPerMicro;

            Mark(micros);
            Enter(action);

            L1!.State(micros, action);
        }
    }

    private void WriteTrade(long time, int price, uint size, char side)
    {
        var aggressor = side switch
        {
            'B' => TradeSide.Buyer,
            'A' => TradeSide.Seller,
            _ => TradeSide.None
        };

        Mark(time);

        L1!.Trade(time, price, size, aggressor);
        Trades!.Add(time, price, size, aggressor);
    }

    private void WriteQuote(long time)
    {
        var top = book.Top();
        var crossed = top.BidSize > 0 && top.AskSize > 0 && top.BidPrice >= top.AskPrice;

        if (marketState == TradingAction && crossed)
        {
            // Orders cross freely before an open, and its match clears them just after.
            if (!uncrossing)
                Refuse($"The book is crossed at {time} during trading: {top.BidPrice} bid, {top.AskPrice} offered.");

            return;
        }

        if (!crossed)
            uncrossing = false;

        Mark(time);

        L1!.Quote(time, top.BidPrice, top.BidSize, top.AskPrice, top.AskSize);
    }

    private void Enter(byte action)
    {
        if (action == TradingAction && marketState != TradingAction)
            uncrossing = true;

        marketState = action;
    }

    /// <summary>Notes the RTH marks as they pass, ahead of the event at this time.</summary>
    private void Mark(long time)
    {
        if (!rthStarted && time >= rthOpen && time < rthClose)
        {
            rthStarted = true;

            L1!.MarkRthStart();
            Trades!.MarkRthStart();
        }

        if (rthStarted && !rthEnded && time >= rthClose)
        {
            rthEnded = true;

            L1!.MarkRthEnd();
            Trades!.MarkRthEnd();
        }
    }

    private void EndSnapshot()
    {
        inSnapshot = false;

        if (opened && book.Top() != beforeSnapshot)
            BoundaryChanges++;
    }

    private void Refuse(string message) => Refusal ??= new Error(TapeErrors.Refused, message);
}
