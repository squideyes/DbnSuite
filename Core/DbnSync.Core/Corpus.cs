namespace DbnSync.Core;

/// <summary>A day as the store holds it: a data file of some size, or an empty marker.</summary>
public sealed record StoredDay(DateOnly Day, long Bytes, bool Empty);

/// <summary>Where one day of one series stands.</summary>
public enum DayState
{
    /// <summary>A data file is stored.</summary>
    Held,

    /// <summary>Asked for; Databento had nothing (weekends, holidays).</summary>
    Empty,

    /// <summary>In a submitted job not yet stored.</summary>
    Pending,

    /// <summary>Planned, not yet asked for. The next passes will take it, within the spend policy.</summary>
    Owed,

    /// <summary>Aged out of the free window without being stored. Only money brings it back.</summary>
    Lost
}

/// <summary>One schema and root, day by day, from its first day to the newest complete one.</summary>
public sealed record Series(
    string Root,
    SchemaSpec Schema,
    DayRange? Span,
    DateOnly FreeFrom,
    IReadOnlyDictionary<DateOnly, DayState> States,
    IReadOnlyDictionary<DateOnly, long> Bytes)
{
    public long TotalBytes => Bytes.Values.Sum();

    public int Count(DayState state) => States.Values.Count(s => s == state);

    /// <summary>Runs of consecutive days in one state, oldest first.</summary>
    public IReadOnlyList<(DayState State, DayRange Range)> Runs(params DayState[] states)
    {
        var runs = new List<(DayState, DayRange)>();

        if (Span is not { } span)
            return runs;

        (DayState State, DateOnly From)? open = null;

        foreach (var day in span.Each())
        {
            var state = States[day];

            if (open is { } run && run.State == state)
                continue;

            if (open is { } closed && states.Contains(closed.State))
                runs.Add((closed.State, new DayRange(closed.From, day.AddDays(-1))));

            open = (state, day);
        }

        if (open is { } last && states.Contains(last.State))
            runs.Add((last.State, new DayRange(last.From, span.Upto)));

        return runs;
    }
}

public static class Corpus
{
    /// <summary>
    /// Classifies every day from the series' first day to its newest complete
    /// one. The first day is the oldest planned (the start of the free window,
    /// or <paramref name="reachBackTo"/> before it) or the oldest stored,
    /// whichever is earlier, so a hole that aged out shows as lost rather than
    /// disappearing off the left edge.
    /// </summary>
    public static Series Build(string root, SchemaSpec schema, IReadOnlyList<StoredDay> stored,
        IReadOnlyList<DayRange> pending, DateOnly today, DateOnly lastComplete, DateOnly historyStart,
        DateOnly? reachBackTo = null)
    {
        var (planFrom, freeFrom) = new SyncOptions { HistoryStart = historyStart, ReachBackTo = reachBackTo }
            .Window(schema.Level, today);
        // A day can hold a data file and an empty marker at once, if data arrived for a day once thought
        // empty. The data wins; the dashboard must not fail over it.
        var byDay = stored.GroupBy(d => d.Day).ToDictionary(g => g.Key, g => g.OrderBy(d => d.Empty).First());

        var first = byDay.Count > 0 && byDay.Keys.Min() < planFrom ? byDay.Keys.Min() : planFrom;
        var last = byDay.Count > 0 && byDay.Keys.Max() > lastComplete ? byDay.Keys.Max() : lastComplete;

        var states = new Dictionary<DateOnly, DayState>();
        var bytes = new Dictionary<DateOnly, long>();

        if (first > last)
            return new Series(root, schema, null, freeFrom, states, bytes);

        var span = new DayRange(first, last);

        foreach (var day in span.Each())
        {
            if (byDay.TryGetValue(day, out var held))
            {
                states[day] = held.Empty ? DayState.Empty : DayState.Held;

                if (!held.Empty)
                    bytes[day] = held.Bytes;
            }
            else if (pending.Any(p => p.Contains(day)))
            {
                states[day] = DayState.Pending;
            }
            else
            {
                states[day] = day < planFrom ? DayState.Lost : DayState.Owed;
            }
        }

        return new Series(root, schema, span, freeFrom, states, bytes);
    }
}
