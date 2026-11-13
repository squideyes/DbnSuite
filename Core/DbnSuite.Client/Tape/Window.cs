namespace DbnSuite.Client.Tape;

/// <summary>Which part of a session to replay. The default is the whole of it.</summary>
public readonly record struct Window
{
    private Window(WindowKind kind, TimeOnly from, TimeOnly to)
    {
        Kind = kind;
        From = from;
        To = to;
    }

    /// <summary>The full session.</summary>
    public static Window Eth => default;

    /// <summary>From the RTH mark to the end mark. Empty on a date with no regular session.</summary>
    public static Window Rth => new(WindowKind.Rth, default, default);

    internal WindowKind Kind { get; }

    internal TimeOnly From { get; }

    internal TimeOnly To { get; }

    /// <summary>
    /// Hours of your own, Chicago time. Hours that end before they start run past midnight, and hours
    /// from 17:00 on are the evening the session opens, the day before the trade date's name.
    /// </summary>
    public static Window Hours(TimeOnly from, TimeOnly to) => new(WindowKind.Hours, from, to);
}

internal enum WindowKind
{
    Eth,
    Rth,
    Hours
}

/// <summary>What a cursor walks: where it starts and stops in the body, and in time.</summary>
/// <param name="From">The first time wanted, in microseconds since the epoch.</param>
/// <param name="Until">The first time no longer wanted.</param>
/// <param name="RthAfter">The event number RTH starts after.</param>
/// <param name="RthThrough">The last event number in RTH.</param>
internal readonly record struct ReplaySpan(RthMark Start, long EndIndex, long EndOffset, long From, long Until, long RthAfter, long RthThrough)
{
    public static ReplaySpan Whole(RthMark start, long endIndex, long endOffset) =>
        new(start, endIndex, endOffset, long.MinValue, long.MaxValue, 0, 0);
}
