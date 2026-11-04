namespace DbnSync.Core;

/// <summary>
/// How long until every free day is stored, judged by how fast recent passes
/// settled days. Lost days are not counted: no pass can fetch them.
/// </summary>
public sealed record Backfill(int DaysLeft, double DaysPerHour, DateTimeOffset? DoneAt)
{
    /// <summary>How far back the rate looks; the pass history keeps at least this much.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(1);

    public bool CaughtUp => DaysLeft == 0;

    /// <summary>
    /// The rate is the days stored or found empty by the passes that finished
    /// within <see cref="Window"/>, over the time since the first of them
    /// started. With nothing settled lately there is no rate, and no end in sight.
    /// </summary>
    public static Backfill Estimate(int daysLeft, IEnumerable<PassSummary> passes, DateTimeOffset now)
    {
        var recent = passes.Where(p => now - p.FinishedAt <= Window).ToList();
        var settled = recent.Sum(p => p.FilesStored + p.EmptyDays);

        if (settled == 0)
            return new(daysLeft, 0, daysLeft == 0 ? now : null);

        var hours = Math.Max((now - recent.Min(p => p.StartedAt)).TotalHours, 1.0 / 60);
        var rate = settled / hours;

        return new(daysLeft, rate, now + TimeSpan.FromHours(daysLeft / rate));
    }
}
