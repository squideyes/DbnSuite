namespace DbnSuite.Spool;

/// <summary>What a root's next pass has to do, worked out from what the archive and the shelf hold.</summary>
public static class SpoolPlan
{
    /// <summary>The last UTC day every schema has reached: later days may still be arriving.</summary>
    /// <returns>Null until every schema has something, because no date can be complete before then.</returns>
    public static DateOnly? Through(IReadOnlyList<IReadOnlyCollection<DateOnly>> schemas) =>
        schemas.Count == 0 || schemas.Any(s => s.Count == 0) ? null : schemas.Min(s => s.Max());

    /// <summary>How many days <c>mbo</c> may trail the other schemas before a root stops waiting for it.</summary>
    public const int MboPatience = 7;

    /// <summary>
    /// True while <c>mbo</c> is close enough behind the other schemas to wait for. It arrives about a day
    /// after them, and a weekend or a holiday adds a day or two. Further behind than that it has stopped,
    /// or was only ever collected for older days, and waiting would hold the root's bar tapes back for ever.
    /// </summary>
    public static bool StillComing(IReadOnlyCollection<DateOnly> mboDays, DateOnly reached) =>
        mboDays.Count > 0 && mboDays.Max() >= reached.AddDays(-MboPatience);

    /// <summary>
    /// The trade dates to spool, oldest first: those the shelf lacks, and those it holds without
    /// tick tapes whose <c>mbo</c> files have arrived, or changed, since. A trade date ends in the
    /// UTC day of its own name, so a later one can't be complete yet.
    /// </summary>
    /// <param name="mboStamp">
    /// A stamp of a date's two <c>mbo</c> files, so a date whose <c>mbo</c> held no tape of its
    /// contract is tried again once those files are fetched again, and not on every pass.
    /// </param>
    public static IReadOnlyList<DateOnly> Pending(IEnumerable<DateOnly> sessions, SpoolLedger ledger,
        IReadOnlySet<DateOnly> mboDays, DateOnly through, Func<DateOnly, string?>? mboStamp = null) =>
        [.. sessions
            .Where(d => d <= through)
            .Where(d => ledger.Spooled(d) is not { } done
                || !done.Ticks && HasTicks(d, mboDays) && (done.Mbo is null || mboStamp?.Invoke(d) != done.Mbo))
            .Order()];

    /// <summary>True when <c>mbo</c> covers the whole trade date, which spans two UTC files.</summary>
    public static bool HasTicks(DateOnly tradeDate, IReadOnlySet<DateOnly> mboDays) =>
        mboDays.Contains(tradeDate.AddDays(-1)) && mboDays.Contains(tradeDate);
}
