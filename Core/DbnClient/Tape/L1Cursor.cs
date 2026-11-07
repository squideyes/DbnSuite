namespace DbnClient.Tape;

/// <summary>An <see cref="L1Cursor"/>'s place and the event it stands on.</summary>
internal readonly record struct L1Place(
    int Position, long Index, L1Kind Kind, long Time, int BidPrice, int AskPrice,
    uint BidSize, uint AskSize, int TradePrice, uint TradeSize, TradeSide Side, byte State);

/// <summary>Walks an L1 tape. Nothing is created per event, and it lives only in the replay loop.</summary>
public ref struct L1Cursor
{
    private const int QuoteChanges = 0x3C;
    private const int ReservedQuoteBits = 0xC0;

    private readonly ReadOnlySpan<byte> body;
    private readonly long endIndex;
    private readonly int endOffset;
    private readonly long from;
    private readonly long until;
    private readonly long rthAfter;
    private readonly long rthThrough;

    private int position;
    private long index;

    internal L1Cursor(ReadOnlySpan<byte> body, RthMark start, long endIndex, long endOffset)
        : this(body, ReplaySpan.Whole(start, endIndex, endOffset))
    {
    }

    internal L1Cursor(ReadOnlySpan<byte> body, ReplaySpan span)
    {
        this.body = body;

        endIndex = span.EndIndex;
        endOffset = (int)span.EndOffset;
        from = span.From;
        until = span.Until;
        rthAfter = span.RthAfter;
        rthThrough = span.RthThrough;
        position = (int)span.Start.BodyOffset;
        index = span.Start.EventIndex;
        Time = span.Start.Time;
        BidPrice = span.Start.BidPrice;
        AskPrice = span.Start.AskPrice;
        BidSize = span.Start.BidSize;
        AskSize = span.Start.AskSize;
        TradePrice = span.Start.LastPrice;
        State = span.Start.MarketState;
    }

    public L1Kind Kind { get; private set; }

    /// <summary>Microseconds since the epoch.</summary>
    public long Time { get; private set; }

    /// <summary>Test <see cref="BidSize"/> first: an empty side keeps its last price.</summary>
    public int BidPrice { get; private set; }

    /// <summary>Test <see cref="AskSize"/> first: an empty side keeps its last price.</summary>
    public int AskPrice { get; private set; }

    public uint BidSize { get; private set; }

    public uint AskSize { get; private set; }

    public int TradePrice { get; private set; }

    public uint TradeSize { get; private set; }

    public TradeSide Side { get; private set; }

    /// <summary>The market's status, as Databento's action code. 0 until the first state event.</summary>
    public byte State { get; private set; }

    /// <summary>Whether this event falls in RTH.</summary>
    public readonly bool InRth => index > rthAfter && index <= rthThrough;

    /// <summary>Where the cursor stands, to carry past the loop that a ref struct can't leave.</summary>
    internal readonly L1Place Here => new(position, index, Kind, Time, BidPrice, AskPrice, BidSize, AskSize, TradePrice, TradeSize, Side, State);

    /// <summary>Takes up a place another cursor on the same tape and span saved.</summary>
    internal void MoveTo(in L1Place place)
    {
        position = place.Position;
        index = place.Index;
        Kind = place.Kind;
        Time = place.Time;
        BidPrice = place.BidPrice;
        AskPrice = place.AskPrice;
        BidSize = place.BidSize;
        AskSize = place.AskSize;
        TradePrice = place.TradePrice;
        TradeSize = place.TradeSize;
        Side = place.Side;
        State = place.State;
    }

    /// <summary>Moves to the next event, or returns false at the end.</summary>
    /// <exception cref="InvalidDataException">The body is damaged.</exception>
    public bool MoveNext()
    {
        while (true)
        {
            if (index == endIndex)
                return TapeReading.AtEnd(position, endOffset);

            Step();

            if (Time < from)
                continue;

            if (Time < until)
                return true;

            index = endIndex;
            position = endOffset;

            return false;
        }
    }

    private void Step()
    {
        var control = TapeReading.Control(body, ref position);

        index++;

        if ((control & 2) != 0)
            Time = TapeReading.AddTime(Time, TapeReading.Gap(body, ref position, 0));

        if ((control & 1) != 0)
            ReadTrade(control);
        else if ((control & ReservedQuoteBits) != 0)
            Varint.Corrupt("A quote sets a reserved bit.");
        else if ((control & QuoteChanges) != 0)
            ReadQuote(control);
        else
            ReadState();
    }

    private void ReadTrade(byte control)
    {
        Kind = L1Kind.Trade;
        Side = TapeReading.Side((control >> 2) & 3);

        if ((control & 16) == 0)
            TradePrice = TapeReading.AddPrice(TradePrice, body, ref position);
        else if (Side == TradeSide.Buyer)
            TradePrice = AskPrice;
        else if (Side == TradeSide.Seller)
            TradePrice = BidPrice;
        else
            Varint.Corrupt("An implied trade has no side.");

        var size = (uint)(control >> 5);

        TradeSize = size != 0 ? size : TapeReading.Size(body, ref position);
    }

    private void ReadQuote(byte control)
    {
        Kind = L1Kind.Quote;

        var bidMoved = (control & 4) != 0;
        var askMoved = (control & 8) != 0;

        if (bidMoved)
            BidPrice = TapeReading.AddPrice(BidPrice, body, ref position);

        if (askMoved)
            AskPrice = TapeReading.AddPrice(AskPrice, body, ref position);

        if ((control & 16) != 0)
            BidSize = bidMoved ? TapeReading.Size(body, ref position) : TapeReading.AddSize(BidSize, body, ref position);

        if ((control & 32) != 0)
            AskSize = askMoved ? TapeReading.Size(body, ref position) : TapeReading.AddSize(AskSize, body, ref position);
    }

    private void ReadState()
    {
        Kind = L1Kind.State;

        var action = TapeReading.Control(body, ref position);

        if (action == 0)
            Varint.Corrupt("A state event has no action.");

        State = action;
    }
}
