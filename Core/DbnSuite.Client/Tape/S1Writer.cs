namespace DbnSuite.Client.Tape;

/// <summary>Encodes one-second bars.</summary>
public sealed class S1Writer(TapeInfo info) : TapeWriter(TapeKind.S1, info)
{
    private const long MicrosecondsPerSecond = 1_000_000;
    private const byte GapBit = 1;
    private const byte OpenBit = 2;
    private const byte CloseBit = 4;
    private const byte HighBit = 8;
    private const byte LowBit = 16;
    private const int VolumeShift = 5;
    private const uint LargestInlineVolume = 7;

    private int lastClose = info.BasePrice;

    /// <summary>Adds a bar. Time is the start of its second, in microseconds since the epoch.</summary>
    /// <returns>False once the writer has refused; see <see cref="TapeWriter.Refusal"/>.</returns>
    public bool Add(long time, int open, int high, int low, int close, uint volume)
    {
        if (Refusal is not null)
            return false;

        if (time % MicrosecondsPerSecond != 0)
            return Refuse($"Bar time {time} is not a whole second.");

        var seconds = time / MicrosecondsPerSecond;

        if (seconds <= LastTime)
            return Refuse($"Bar second {seconds} does not follow {LastTime}.");

        if (!InSequence(seconds))
            return false;

        var top = Math.Max(open, close);
        var bottom = Math.Min(open, close);

        if (high < top || low > bottom)
            return Refuse($"Bar at {seconds} has a high or low outside its open and close.");

        var gap = seconds - LastTime;
        var inlineVolume = volume is >= 1 and <= LargestInlineVolume ? (int)volume : 0;

        var control = (byte)(
            (gap > 1 ? GapBit : 0)
            | (open != lastClose ? OpenBit : 0)
            | (close != open ? CloseBit : 0)
            | (high > top ? HighBit : 0)
            | (low < bottom ? LowBit : 0)
            | (inlineVolume << VolumeShift));

        BeginEvent(control);

        if (gap > 1)
            PutVarint((ulong)(gap - 2));

        if (open != lastClose)
            PutZigzag((long)open - lastClose);

        if (close != open)
            PutZigzag((long)close - open);

        if (high > top)
            PutVarint((ulong)((long)high - top - 1));

        if (low < bottom)
            PutVarint((ulong)((long)bottom - low - 1));

        if (inlineVolume == 0)
            PutVarint(volume);

        LastTime = seconds;
        lastClose = close;
        Checksum = TapeChecksum.Bar(Checksum, time, open, high, low, close, volume);

        return true;
    }

    private protected override RthMark Capture() => new() { Time = LastTime, LastPrice = lastClose };
}
