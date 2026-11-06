namespace DbnClient.Tape;

/// <summary>Walks one-second bars. Nothing is created per bar, and it lives only in the replay loop.</summary>
public ref struct S1Cursor : IBarSource
{
    private const long MicrosecondsPerSecond = 1_000_000;

    private readonly ReadOnlySpan<byte> body;
    private readonly long endIndex;
    private readonly int endOffset;
    private readonly long from;
    private readonly long until;
    private readonly long rthAfter;
    private readonly long rthThrough;

    private int position;
    private long index;
    private long seconds;

    internal S1Cursor(ReadOnlySpan<byte> body, RthMark start, long endIndex, long endOffset)
        : this(body, ReplaySpan.Whole(start, endIndex, endOffset))
    {
    }

    internal S1Cursor(ReadOnlySpan<byte> body, ReplaySpan span)
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
        seconds = span.Start.Time;
        Close = span.Start.LastPrice;
    }

    /// <summary>The start of the bar's second, in microseconds since the epoch.</summary>
    public readonly long Time => seconds * MicrosecondsPerSecond;

    public int Open { get; private set; }

    public int High { get; private set; }

    public int Low { get; private set; }

    public int Close { get; private set; }

    public uint Volume { get; private set; }

    /// <summary>Whether this bar falls in RTH.</summary>
    public readonly bool InRth => index > rthAfter && index <= rthThrough;

    /// <summary>Moves to the next bar, or returns false at the end.</summary>
    /// <exception cref="InvalidDataException">The body is damaged.</exception>
    public bool MoveNext()
    {
        while (true)
        {
            if (index == endIndex)
                return TapeReading.AtEnd(position, endOffset);

            Step();

            var time = Time;

            if (time < from)
                continue;

            if (time < until)
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

        var step = 1L;

        if ((control & 1) != 0)
            step = TapeReading.Gap(body, ref position, 2);

        seconds = TapeReading.AddTime(seconds, step);

        var open = (control & 2) != 0 ? TapeReading.AddPrice(Close, body, ref position) : Close;
        var close = (control & 4) != 0 ? TapeReading.AddPrice(open, body, ref position) : open;
        var top = Math.Max(open, close);
        var bottom = Math.Min(open, close);

        High = (control & 8) != 0 ? TapeReading.Above(top, body, ref position) : top;
        Low = (control & 16) != 0 ? TapeReading.Below(bottom, body, ref position) : bottom;
        Open = open;
        Close = close;

        var volume = (uint)(control >> 5);

        Volume = volume != 0 ? volume : TapeReading.Size(body, ref position);
    }
}
