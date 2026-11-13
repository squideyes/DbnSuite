using System.Globalization;

namespace DbnSuite.Sync.Core;

/// <summary>
/// What DbnSync may spend at Databento. Bound from <c>DbnSync:Spend</c>.
/// <para>
/// The default spends nothing: every job is quoted first and submitted only at
/// exactly $0. Paid jobs need <see cref="AllowPaidJobs"/> <em>and</em> all three
/// limits above zero, and a job goes only if its quote fits all three, counting
/// what the ledger says was already spent today and this month.
/// </para>
/// <para>
/// <strong>Use at your own risk.</strong> Quotes are Databento's, limits are
/// checked against DbnSync's own ledger, and nothing here is a guarantee of what
/// Databento will bill. A bug, a changed price or a misread setting can cost
/// real money; DbnSync is provided as is, with no warranty, under the MIT licence.
/// </para>
/// </summary>
public sealed class SpendPolicy
{
    /// <summary>Off unless set: without it every limit counts as zero.</summary>
    public bool AllowPaidJobs { get; set; }

    /// <summary>The most one job may be quoted at, in US dollars.</summary>
    public decimal MaxPerJob { get; set; }

    /// <summary>The most all jobs submitted in one UTC day may be quoted at, together.</summary>
    public decimal MaxPerDay { get; set; }

    /// <summary>The most all jobs submitted in one UTC calendar month may be quoted at, together.</summary>
    public decimal MaxPerMonth { get; set; }

    /// <summary>True only when paid jobs are allowed and every limit is above zero.</summary>
    public bool Paid => AllowPaidJobs && MaxPerJob > 0 && MaxPerDay > 0 && MaxPerMonth > 0;

    /// <summary>The most the next job may cost, after what has been spent today and this month.</summary>
    public decimal Allowance(decimal spentToday, decimal spentThisMonth) =>
        Paid
            ? Math.Max(0m, Math.Min(MaxPerJob, Math.Min(MaxPerDay - spentToday, MaxPerMonth - spentThisMonth)))
            : 0m;

    /// <summary>The policy in a line, for the log and the dashboard.</summary>
    public string Describe() =>
        Paid
            ? string.Create(CultureInfo.InvariantCulture,
                $"paid jobs allowed: up to ${MaxPerJob:0.##} a job, ${MaxPerDay:0.##} a day, ${MaxPerMonth:0.##} a month")
            : "free jobs only: nothing is submitted unless Databento quotes it at $0";
}

/// <summary>
/// One paid job in the spend ledger, written <em>before</em> the job is
/// submitted, so a pass that dies between the two over-counts rather than
/// under-counts. <see cref="JobId"/> is filled in once Databento accepts it.
/// </summary>
public sealed record SpendEntry(
    string Key,
    DateTimeOffset At,
    string Root,
    string Schema,
    DayRange Range,
    decimal Cost,
    string? JobId = null);

public static class SpendLedger
{
    /// <summary>What the entries of one month add up to, that month and on <paramref name="today"/>.</summary>
    public static (decimal Today, decimal Month) Totals(IEnumerable<SpendEntry> month, DateOnly today)
    {
        decimal day = 0, total = 0;

        foreach (var entry in month)
        {
            total += entry.Cost;

            if (DateOnly.FromDateTime(entry.At.UtcDateTime) == today)
                day += entry.Cost;
        }

        return (day, total);
    }
}
