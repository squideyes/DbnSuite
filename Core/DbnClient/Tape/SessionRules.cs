using DbnClient.Time;

namespace DbnClient.Tape;

/// <summary>One status record of one instrument, reduced to what sessions need.</summary>
/// <param name="Time">The record's ts_event, in nanoseconds since the UNIX epoch.</param>
/// <param name="Action">Databento's action code. 0 means no change.</param>
/// <param name="IsTrading">Null when the record does not say.</param>
internal readonly record struct StatusPoint(long Time, ushort Action, bool? IsTrading);

/// <summary>A stretch of time a contract traded. Times are nanoseconds since the UNIX epoch.</summary>
internal readonly record struct TradingSpan(long Start, long End);

/// <summary>
/// Turns status records into sessions. Fixed hours appear only for RTH, and in
/// <see cref="Repair"/>, for the few dates the status records themselves are incomplete.
/// </summary>
internal static class SessionRules
{
    private const long RepairSlack = 60_000_000_000;

    /// <summary>
    /// Follows one instrument's status through time. Each UTC file repeats the state it
    /// opens in, stamped with the older event's time, which a repeat of the same state ignores.
    /// </summary>
    public sealed class SpanTracker
    {
        private long? start;

        public List<TradingSpan> Spans { get; } = [];

        public void Add(StatusPoint point)
        {
            if (point.IsTrading is not { } trading)
                return;

            if (trading)
            {
                start ??= point.Time;
            }
            else if (start is { } opened)
            {
                Spans.Add(new TradingSpan(opened, point.Time));
                start = null;
            }
        }
    }

    /// <summary>
    /// Fills in what the status records lost, using CME's usual hours. A span that runs through
    /// the next session's open missed a close. A first bar well before the first span shows a
    /// missed open. A date with bars and no session at all lost both, unless it is still unfinished.
    /// </summary>
    /// <param name="spans">Each instrument's spans, oldest first.</param>
    /// <param name="firstBars">Each instrument's first bar on each trade date, in nanoseconds.</param>
    public static Dictionary<uint, List<TradingSpan>> Repair(
        IReadOnlyDictionary<uint, List<TradingSpan>> spans,
        IReadOnlyDictionary<uint, Dictionary<DateOnly, long>> firstBars)
    {
        var repaired = spans.ToDictionary(s => s.Key, s => s.Value.SelectMany(Split).ToList());
        var sessions = repaired.Values.SelectMany(l => l).Select(s => DateOf(s.Start)).ToHashSet();
        var finished = repaired.Values.SelectMany(l => l).Select(s => s.End).DefaultIfEmpty().Max();

        foreach (var (id, days) in firstBars)
        {
            if (!repaired.TryGetValue(id, out var list))
                repaired[id] = list = [];

            foreach (var (date, first) in days)
            {
                var index = list.FindIndex(s => DateOf(s.Start) == date);

                if (index >= 0)
                {
                    if (first < list[index].Start - RepairSlack)
                        list[index] = list[index] with { Start = Nanos(Sessions.SessionStart(date)) };
                }
                else if (!sessions.Contains(date) && Nanos(Sessions.SessionEnd(date)) <= finished)
                {
                    list.Add(new TradingSpan(Nanos(Sessions.SessionStart(date)), Nanos(Sessions.SessionEnd(date))));
                }
            }
        }

        return repaired;
    }

    /// <summary>Groups an instrument's spans into sessions. A span belongs to the trade date it starts in.</summary>
    public static IEnumerable<SessionDay> Days(string root, IEnumerable<TradingSpan> spans)
    {
        foreach (var group in spans.GroupBy(s => DateOf(s.Start)).OrderBy(g => g.Key))
            yield return Day(root, group.Key, [.. group.OrderBy(s => s.Start)]);
    }

    /// <summary>
    /// The state changes a session's L1 tape holds, oldest first: no-change records
    /// and repeats dropped, and nothing outside the session.
    /// </summary>
    public static IReadOnlyList<(long Time, byte Action)> States(IEnumerable<StatusPoint> points, long open, long close)
    {
        var states = new List<(long Time, byte Action)>();
        var last = -1;

        foreach (var point in points.Where(p => p.Action != 0 && p.Action <= byte.MaxValue).OrderBy(p => p.Time))
        {
            if (point.Time > close)
                break;

            if (point.Action == last)
                continue;

            last = point.Action;

            if (point.Time >= open)
                states.Add((point.Time, (byte)point.Action));
        }

        return states;
    }

    private static IEnumerable<TradingSpan> Split(TradingSpan span)
    {
        var rest = span;

        while (true)
        {
            var date = DateOf(rest.Start);
            var next = Sessions.FirstTradeDate(date.AddDays(1));

            if (rest.End <= Nanos(Sessions.SessionStart(next)))
                break;

            yield return rest with { End = Nanos(Sessions.SessionEnd(date)) };

            rest = rest with { Start = Nanos(Sessions.SessionStart(next)) };
        }

        yield return rest;
    }

    private static DateOnly DateOf(long nanos) => Sessions.TradeDate((ulong)nanos);

    private static long Nanos(DateTimeOffset time) => (time - DateTimeOffset.UnixEpoch).Ticks * 100;

    private static SessionDay Day(string root, DateOnly tradeDate, IReadOnlyList<TradingSpan> spans)
    {
        var open = spans[0].Start;
        var close = spans[^1].End;
        var breaks = new List<SessionBreak>();

        for (var i = 1; i < spans.Count; i++)
            breaks.Add(new SessionBreak(spans[i - 1].End, spans[i].Start));

        var (rthOpen, rthClose) = Rth(root, tradeDate, open, close);

        return new SessionDay(tradeDate, open, close, breaks, rthOpen, rthClose);
    }

    /// <summary>A root's regular hours on a date, cut to the session. Null when the two do not meet.</summary>
    public static (long? Open, long? Close) Rth(string root, DateOnly tradeDate, long open, long close)
    {
        if (Sessions.RegularHours(root) is not { } hours)
            return (null, null);

        var rthOpen = Math.Max(open, Nanos(tradeDate, hours.Start));
        var rthClose = Math.Min(close, Nanos(tradeDate, hours.End));

        return rthOpen < rthClose ? (rthOpen, rthClose) : (null, null);
    }

    private static long Nanos(DateOnly tradeDate, TimeOnly time)
    {
        var chicago = Zones.Find(Zones.Default).Value;
        var local = tradeDate.ToDateTime(time);

        return Nanos(new DateTimeOffset(local, chicago.GetUtcOffset(local)));
    }
}
