namespace DbnSpool;

/// <summary>What a root's next pass has to do, worked out from what the archive and the shelf hold.</summary>
public static class SpoolPlan
{
    /// <summary>The last UTC day every schema has reached: later days may still be arriving.</summary>
    /// <returns>Null until every schema has something, because no date can be complete before then.</returns>
    public static DateOnly? Through(IReadOnlyList<IReadOnlyCollection<DateOnly>> schemas) =>
        schemas.Count == 0 || schemas.Any(s => s.Count == 0) ? null : schemas.Min(s => s.Max());

    /// <summary>
    /// The trade dates to spool, oldest first: those the shelf lacks, and those it holds
    /// without tick tapes whose <c>mbo</c> files have arrived since. A trade date ends in
    /// the UTC day of its own name, so a later one can't be complete yet.
    /// </summary>
    public static IReadOnlyList<DateOnly> Pending(IEnumerable<DateOnly> sessions, SpoolLedger ledger,
        IReadOnlySet<DateOnly> mboDays, DateOnly through) =>
        [.. sessions
            .Where(d => d <= through)
            .Where(d => ledger.Spooled(d) is not { } done || !done.Ticks && HasTicks(d, mboDays))
            .Order()];

    /// <summary>True when <c>mbo</c> covers the whole trade date, which spans two UTC files.</summary>
    public static bool HasTicks(DateOnly tradeDate, IReadOnlySet<DateOnly> mboDays) =>
        mboDays.Contains(tradeDate.AddDays(-1)) && mboDays.Contains(tradeDate);
}
