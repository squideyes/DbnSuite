using DbnClient.Export;

namespace DbnClient.Tape;

/// <summary>A bar, finished or forming. Prices are in ticks, and times in microseconds since the epoch.</summary>
public struct TapeBar
{
    public long Start;
    public long End;
    public int Open;
    public int High;
    public int Low;
    public int Close;
    public long Volume;

    /// <summary>How many source events it took in.</summary>
    public long Count;

    /// <summary>The window ended before the bar's full length did.</summary>
    public bool IsShort;

    /// <summary>The first time that belongs to the next bar.</summary>
    public long Limit;

    /// <summary>The trade date, week or month it belongs to, as its rule counts them.</summary>
    public long Period;
}

/// <summary>Where bars come from: one-second bars, or trades seen as bars of one price.</summary>
public interface IBarSource
{
    long Time { get; }

    int Open { get; }

    int High { get; }

    int Low { get; }

    int Close { get; }

    uint Volume { get; }

    bool MoveNext();
}

/// <summary>Decides where bars begin and end. As a small struct, the JIT can inline it.</summary>
public interface IBarRule
{
    /// <summary>Whether something at this time must start a new bar.</summary>
    bool Splits(in TapeBar forming, long time);

    /// <summary>Whether the bar is finished now it has taken in another event.</summary>
    bool IsFull(in TapeBar forming);

    /// <summary>Sets the times of a bar opening at this time.</summary>
    void Open(ref TapeBar bar, long time);

    /// <summary>The bar's end, now it has taken in something at this time.</summary>
    long EndAfter(in TapeBar forming, long time);

    /// <summary>Sets up the empty bar after <paramref name="last"/>, if one fits before this time.</summary>
    bool FillsGap(in TapeBar last, long time, ref TapeBar bar);

    /// <summary>Whether a bar can run on into the next trade date.</summary>
    bool SpansDates { get; }
}

/// <summary>The rule behind every size <see cref="BarSpec"/> parses. It counts time from the window's open, as BarBuilder does.</summary>
public struct SpecRule : IBarRule
{
    private const long MicrosPerSecond = 1_000_000;

    private readonly BarUnit unit;
    private readonly long size;

    private long anchor;
    private long close;
    private long period;

    public SpecRule(BarSpec spec)
    {
        unit = spec.Unit;
        size = spec.Unit == BarUnit.Seconds ? spec.Size * MicrosPerSecond : spec.Size;
    }

    public readonly bool SpansDates => unit is BarUnit.Week or BarUnit.Month;

    /// <summary>Counts from a new window: its open and close, in microseconds.</summary>
    internal void Begin(long open, long end, DateOnly tradeDate)
    {
        anchor = open;
        close = end;
        period = unit switch
        {
            BarUnit.Week => tradeDate.AddDays(-(((int)tradeDate.DayOfWeek + 6) % 7)).DayNumber,
            BarUnit.Month => tradeDate.Year * 12L + tradeDate.Month,
            _ => tradeDate.DayNumber
        };
    }

    public readonly bool Splits(in TapeBar forming, long time) => time >= forming.Limit || forming.Period != period;

    public readonly bool IsFull(in TapeBar forming) => unit switch
    {
        BarUnit.Ticks => forming.Count >= size,
        BarUnit.Volume => forming.Volume >= size,
        _ => false
    };

    public readonly void Open(ref TapeBar bar, long time)
    {
        bar.Period = period;
        bar.IsShort = false;

        switch (unit)
        {
            case BarUnit.Seconds:
                var start = anchor + (time - anchor) / size * size;
                var limit = start + size;

                bar.Start = start;
                bar.Limit = limit;
                bar.IsShort = close > start && close < limit;
                bar.End = bar.IsShort ? close : limit;
                break;

            case BarUnit.Day or BarUnit.Week or BarUnit.Month:
                bar.Start = anchor;
                bar.End = close;
                bar.Limit = long.MaxValue;
                break;

            default:
                bar.Start = time;
                bar.End = time;
                bar.Limit = long.MaxValue;
                break;
        }
    }

    public readonly long EndAfter(in TapeBar forming, long time) => unit switch
    {
        BarUnit.Seconds => forming.End,
        BarUnit.Day or BarUnit.Week or BarUnit.Month => Math.Max(forming.End, close),
        _ => time
    };

    public readonly bool FillsGap(in TapeBar last, long time, ref TapeBar bar)
    {
        if (unit != BarUnit.Seconds || last.Period != period || last.Limit >= time - (time - anchor) % size)
            return false;

        Open(ref bar, last.Limit);

        return true;
    }
}

/// <summary>
/// Rolls events up into bars. It holds one bar forming and at most one finished, so it
/// allocates nothing as it runs, and it can carry a bar from one tape to the next.
/// </summary>
public sealed class BarRoller<TRule>(TRule rule, bool fillGaps = false)
    where TRule : struct, IBarRule
{
    private TRule rule = rule;
    private TapeBar forming;
    private TapeBar last;
    private TapeBar finished;
    private bool isForming;
    private bool hasLast;
    private bool hasFinished;
    private bool hasHeld;
    private long heldTime;
    private int heldOpen;
    private int heldHigh;
    private int heldLow;
    private int heldClose;
    private uint heldVolume;

    /// <summary>The bar so far. Its volume is 0 when nothing has opened yet.</summary>
    public TapeBar Forming => isForming ? forming : default;

    internal ref TRule Rule => ref rule;

    internal bool Busy => hasHeld || hasFinished;

    internal bool SpansDates => rule.SpansDates;

    /// <summary>Hands in the next event. Call <see cref="Take"/> until it says false before the next.</summary>
    internal void Offer(long time, int open, int high, int low, int close, uint volume)
    {
        heldTime = time;
        heldOpen = open;
        heldHigh = high;
        heldLow = low;
        heldClose = close;
        heldVolume = volume;
        hasHeld = true;
    }

    /// <summary>Gives the next finished bar, if the event handed in finished one.</summary>
    internal bool Take(out TapeBar bar)
    {
        if (hasFinished)
        {
            hasFinished = false;
            bar = finished;

            return true;
        }

        if (!hasHeld)
        {
            bar = default;

            return false;
        }

        if (isForming && rule.Splits(forming, heldTime))
        {
            bar = Finish();

            return true;
        }

        if (!isForming && fillGaps && hasLast && Filler(out bar))
            return true;

        Join();

        hasHeld = false;

        if (rule.IsFull(forming))
        {
            bar = Finish();

            return true;
        }

        bar = default;

        return false;
    }

    /// <summary>Finishes the bar forming, at the end of a window or contract.</summary>
    internal bool Flush(out TapeBar bar)
    {
        if (isForming)
        {
            bar = Finish();

            return true;
        }

        bar = default;

        return false;
    }

    /// <summary>Finishes the bar forming, to be given by the next <see cref="Take"/>.</summary>
    internal void Seal()
    {
        if (!isForming)
            return;

        finished = Finish();
        hasFinished = true;
        hasLast = false;
    }

    private bool Filler(out TapeBar bar)
    {
        bar = default;

        if (!rule.FillsGap(last, heldTime, ref bar))
            return false;

        bar.Open = bar.High = bar.Low = bar.Close = last.Close;
        last = bar;

        return true;
    }

    private void Join()
    {
        if (!isForming)
        {
            isForming = true;
            forming.Open = heldOpen;
            forming.High = heldHigh;
            forming.Low = heldLow;
            forming.Volume = 0;
            forming.Count = 0;

            rule.Open(ref forming, heldTime);
        }

        if (heldHigh > forming.High)
            forming.High = heldHigh;

        if (heldLow < forming.Low)
            forming.Low = heldLow;

        forming.Close = heldClose;
        forming.Volume += heldVolume;
        forming.Count++;
        forming.End = rule.EndAfter(forming, heldTime);
    }

    private TapeBar Finish()
    {
        isForming = false;
        last = forming;
        hasLast = true;

        return forming;
    }
}

/// <summary>Streams bars from a tape. Nothing is created per bar, and it lives only in the replay loop.</summary>
public ref struct BarStream<TSource, TRule>
    where TSource : IBarSource, allows ref struct
    where TRule : struct, IBarRule
{
    private readonly BarRoller<TRule> roller;
    private readonly bool flushAtEnd;

    private TSource source;
    private TapeBar bar;
    private bool ended;

    internal BarStream(TSource source, BarRoller<TRule> roller, bool flushAtEnd)
    {
        this.source = source;
        this.roller = roller;
        this.flushAtEnd = flushAtEnd;
    }

    public readonly long Start => bar.Start;

    public readonly long End => bar.End;

    public readonly int Open => bar.Open;

    public readonly int High => bar.High;

    public readonly int Low => bar.Low;

    public readonly int Close => bar.Close;

    public readonly long Volume => bar.Volume;

    /// <summary>The window ended before this bar's full length did.</summary>
    public readonly bool IsShort => bar.IsShort;

    /// <summary>The finished bar, whole.</summary>
    public readonly TapeBar Bar => bar;

    /// <summary>The bar so far, for logic that needs it before the close. Use it with <see cref="Step"/>.</summary>
    public readonly TapeBar Forming => roller.Forming;

    /// <summary>Whether the last <see cref="Step"/> finished a bar.</summary>
    public bool HasBar { get; private set; }

    /// <summary>Moves to the next finished bar, or returns false at the end.</summary>
    public bool MoveNext()
    {
        while (Step())
        {
            if (HasBar)
                return true;
        }

        return false;
    }

    /// <summary>Takes in one source event, or returns false at the end.</summary>
    public bool Step()
    {
        HasBar = false;

        if (roller.Busy)
        {
            HasBar = roller.Take(out bar);

            return true;
        }

        if (ended)
            return false;

        if (!source.MoveNext())
        {
            ended = true;
            HasBar = flushAtEnd && roller.Flush(out bar);

            return HasBar;
        }

        roller.Offer(source.Time, source.Open, source.High, source.Low, source.Close, source.Volume);

        HasBar = roller.Take(out bar);

        return true;
    }
}

/// <summary>Feeds several bar sizes from one pass over an S1 tape.</summary>
public ref struct BarFan
{
    private readonly BarRoller<SpecRule>[] rollers;

    private S1Cursor source;
    private TapeBar bar;
    private int next;
    private bool ended;

    internal BarFan(S1Cursor source, BarRoller<SpecRule>[] rollers)
    {
        this.source = source;
        this.rollers = rollers;

        next = rollers.Length;
    }

    /// <summary>Which of the sizes asked for this bar is.</summary>
    public int Index { get; private set; }

    public readonly TapeBar Bar => bar;

    /// <summary>Moves to the next finished bar of any size, or returns false at the end.</summary>
    public bool MoveNext()
    {
        while (true)
        {
            for (; next < rollers.Length; next++)
            {
                var roller = rollers[next];

                if (ended ? roller.Flush(out bar) : roller.Take(out bar))
                {
                    Index = next;

                    return true;
                }
            }

            if (ended)
                return false;

            next = 0;

            if (source.MoveNext())
            {
                foreach (var roller in rollers)
                    roller.Offer(source.Time, source.Open, source.High, source.Low, source.Close, source.Volume);
            }
            else
            {
                ended = true;
            }
        }
    }
}
