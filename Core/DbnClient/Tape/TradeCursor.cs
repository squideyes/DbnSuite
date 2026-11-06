namespace DbnClient.Tape;

/// <summary>Walks a Trade tape. Nothing is created per trade, and it lives only in the replay loop.</summary>
public ref struct TradeCursor
{
    private readonly ReadOnlySpan<byte> body;
    private readonly long endIndex;
    private readonly int endOffset;
    private readonly long from;
    private readonly long until;
    private readonly long rthAfter;
    private readonly long rthThrough;

    private int position;
    private long index;

    internal TradeCursor(ReadOnlySpan<byte> body, RthMark start, long endIndex, long endOffset)
        : this(body, ReplaySpan.Whole(start, endIndex, endOffset))
    {
    }

    internal TradeCursor(ReadOnlySpan<byte> body, ReplaySpan span)
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
        Price = span.Start.LastPrice;
    }

    /// <summary>Microseconds since the epoch.</summary>
    public long Time { get; private set; }

    public int Price { get; private set; }

    public uint Size { get; private set; }

    public TradeSide Side { get; private set; }

    /// <summary>Whether this trade falls in RTH.</summary>
    public readonly bool InRth => index > rthAfter && index <= rthThrough;

    /// <summary>Moves to the next trade, or returns false at the end.</summary>
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

        Side = TapeReading.Side(control & 3);

        if ((control & 4) != 0)
            Time = TapeReading.AddTime(Time, TapeReading.Gap(body, ref position, 0));

        if ((control & 8) != 0)
            Price = TapeReading.AddPrice(Price, body, ref position);

        var size = (uint)(control >> 4);

        Size = size != 0 ? size : TapeReading.Size(body, ref position);
    }
}

/// <summary>A Trade cursor seen as a bar source: each trade is a bar of one price.</summary>
public ref struct TradeBarSource : IBarSource
{
    private TradeCursor cursor;

    internal TradeBarSource(TradeCursor cursor) => this.cursor = cursor;

    public readonly long Time => cursor.Time;

    public readonly int Open => cursor.Price;

    public readonly int High => cursor.Price;

    public readonly int Low => cursor.Price;

    public readonly int Close => cursor.Price;

    public readonly uint Volume => cursor.Size;

    public bool MoveNext() => cursor.MoveNext();
}
