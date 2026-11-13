namespace DbnSuite.Sync.Core;

/// <summary>
/// How far back each level is free on the subscription, set by each
/// <see cref="IDatasetProfile.Included"/>. Outside it Databento does not
/// refuse, it bills, so this only decides what to <em>ask</em> about; the
/// quote before every job, held to the spend policy, is the guard.
/// </summary>
/// <param name="L1Days">Free lookback for L1, in days.</param>
/// <param name="BookDays">Free lookback for L2 and L3, in days.</param>
public sealed record Entitlement(int L1Days, int BookDays)
{
    public DateOnly EarliestFor(Level level, DateOnly today, DateOnly historyStart)
    {
        var earliest = level switch
        {
            Level.L0 => historyStart,
            Level.L1 => today.AddDays(-L1Days),
            _ => today.AddDays(-BookDays)
        };

        return earliest < historyStart ? historyStart : earliest;
    }

    /// <summary>
    /// The newest UTC day wholly inside the dataset, given where its data ends.
    /// Only whole days are fetched, so every stored file is final.
    /// </summary>
    public static DateOnly LastCompleteDay(DateTimeOffset availableEnd) =>
        DateOnly.FromDateTime(availableEnd.UtcDateTime).AddDays(-1);
}
