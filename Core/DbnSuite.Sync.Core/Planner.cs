namespace DbnSuite.Sync.Core;

/// <summary>What one root and schema is owed, and what is already held or on its way.</summary>
/// <param name="BeyondPlan">The days are older than the plan includes, so likely to cost money.</param>
public sealed record Coverage(
    string Root,
    SchemaSpec Schema,
    DayRange Window,
    IReadOnlySet<DateOnly> Held,
    IReadOnlyList<DayRange> Pending,
    bool BeyondPlan = false);

/// <summary>One batch job worth asking for.</summary>
public sealed record JobRequest(string Root, SchemaSpec Schema, DayRange Range, bool BeyondPlan = false);

/// <summary>
/// Turns coverage into jobs. Every pass plans from what the store actually
/// holds, day by day, so a hole left by a failed job is planned again rather
/// than hidden behind a range that once completed.
/// </summary>
public static class Planner
{
    /// <summary>The runs of days in the window that are neither held nor pending.</summary>
    public static IReadOnlyList<DayRange> Missing(DayRange window,
        IReadOnlySet<DateOnly> held, IReadOnlyList<DayRange> pending)
    {
        var runs = new List<DayRange>();

        DateOnly? start = null;

        foreach (var day in window.Each())
        {
            var owed = !held.Contains(day) && !pending.Any(p => p.Contains(day));

            if (owed)
            {
                start ??= day;
            }
            else if (start is { } open)
            {
                runs.Add(new DayRange(open, day.AddDays(-1)));

                start = null;
            }
        }

        if (start is { } last)
            runs.Add(new DayRange(last, window.Upto));

        return runs;
    }

    public static IEnumerable<DayRange> Chunk(DayRange run, int days)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(days, 1);

        for (var from = run.From; from <= run.Upto; from = from.AddDays(days))
        {
            var upto = from.AddDays(days - 1);

            yield return new DayRange(from, upto > run.Upto ? run.Upto : upto);
        }
    }

    /// <summary>
    /// Every job owed, most perishable first: the book schemas, whose free
    /// window is a month, then L1, then L0, which never stops being free.
    /// Within a level the oldest days go first, since they age out first.
    /// Days older than the plan includes come after all of those, newest
    /// first, so a budget is never spent while an included day waits.
    /// </summary>
    public static IReadOnlyList<JobRequest> Plan(IEnumerable<Coverage> coverage)
    {
        var jobs = coverage
            .SelectMany(c => Missing(c.Window, c.Held, c.Pending)
                .SelectMany(run => Chunk(run, c.Schema.ChunkDays))
                .Select(range => new JobRequest(c.Root, c.Schema, range, c.BeyondPlan)))
            .ToList();

        return
        [
            .. jobs.Where(j => !j.BeyondPlan)
                .OrderByDescending(j => j.Schema.Level)
                .ThenBy(j => j.Range.From)
                .ThenBy(j => j.Root, StringComparer.Ordinal)
                .ThenBy(j => j.Schema.Name, StringComparer.Ordinal),
            .. jobs.Where(j => j.BeyondPlan)
                .OrderByDescending(j => j.Schema.Level)
                .ThenByDescending(j => j.Range.From)
                .ThenBy(j => j.Root, StringComparer.Ordinal)
                .ThenBy(j => j.Schema.Name, StringComparer.Ordinal)
        ];
    }
}
