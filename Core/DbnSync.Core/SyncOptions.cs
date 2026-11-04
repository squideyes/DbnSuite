namespace DbnSync.Core;

/// <summary>What to keep, and how hard to work at it. Bound from <c>DbnSync</c>.</summary>
public sealed class SyncOptions
{
    public const string Section = "DbnSync";

    /// <summary>The Databento dataset to keep; it must have a profile in <see cref="DatasetProfiles"/>.</summary>
    public string Dataset { get; set; } = "GLBX.MDP3";

    /// <summary>
    /// Whether to start on a dataset whose profile is
    /// <see cref="ProfileStatus.Contributed"/>: written and tested by someone
    /// else, never run by the maintainer.
    /// </summary>
    public bool AllowContributedProfile { get; set; }

    /// <summary>
    /// Comma-separated groups of the dataset's instruments, which for futures
    /// are roots, e.g. <c>ES,MES,NQ,MNQ</c>.
    /// </summary>
    public string Roots { get; set; } = "";

    /// <summary>Comma-separated Databento schema names.</summary>
    public string Schemas { get; set; } = "";

    /// <summary>
    /// Per-schema root lists that replace <see cref="Roots"/> for that schema,
    /// e.g. <c>mbo: MNQ,MES</c> to keep the book for fewer roots than the bars.
    /// </summary>
    public Dictionary<string, string> RootsBySchema { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The oldest day ever asked for, whatever the window allows.</summary>
    public DateOnly HistoryStart { get; set; } = new(2020, 1, 1);

    /// <summary>
    /// The oldest day asked for at L1 and above, when it is older than the
    /// Standard plan includes. Unset, those levels keep to the plan's windows.
    /// Days before the windows are asked for only after every day inside them,
    /// and go only if <see cref="Spend"/> admits their quote: $0 on a plan that
    /// includes them, a price on Standard.
    /// </summary>
    public DateOnly? ReachBackTo { get; set; }

    /// <summary>What may be spent at Databento. Spends nothing unless changed.</summary>
    public SpendPolicy Spend { get; set; } = new();

    /// <summary>How long to wait between passes once there is nothing left to do.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>How long to wait while jobs are still pending or owed.</summary>
    public TimeSpan BusyInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Jobs submitted and not yet stored, at most.</summary>
    public int MaxPendingJobs { get; set; } = 30;

    public int DownloadConcurrency { get; set; } = 2;

    /// <summary>Leading outrights kept for <see cref="Selection.Leading"/> schemas.</summary>
    public int Depth { get; set; } = 2;

    /// <summary>Where a file sits between download and upload.</summary>
    public string ScratchPath { get; set; } = Path.Combine(Path.GetTempPath(), "dbnsync");

    /// <summary>
    /// The days to plan for one level: from the oldest asked for to the start
    /// of what the plan includes, which is where the free window begins.
    /// </summary>
    public (DateOnly PlanFrom, DateOnly IncludedFrom) Window(Level level, DateOnly today)
    {
        var included = Profile().Included.EarliestFor(level, today, HistoryStart);

        return level != Level.L0 && ReachBackTo is { } reach && reach < included
            ? (reach < HistoryStart ? HistoryStart : reach, included)
            : (included, included);
    }

    /// <summary>
    /// The profile for <see cref="Dataset"/>. Validation refuses to start on a
    /// dataset without one, so past startup this does not throw.
    /// </summary>
    public IDatasetProfile Profile() =>
        DatasetProfiles.Find(Dataset)
        ?? throw new InvalidOperationException($"DbnSync has no profile for the dataset '{Dataset}'.");

    public IReadOnlyList<string> RootList => Split(Roots, upper: true);

    public IReadOnlyList<string> SchemaList => Split(Schemas, upper: false);

    /// <summary>Every root and schema pair to keep.</summary>
    public IEnumerable<(string Root, SchemaSpec Schema)> Wanted()
    {
        var known = Profile().Schemas;

        foreach (var name in SchemaList)
        {
            if (!known.TryGetValue(name, out var spec))
                continue;

            var roots = RootsBySchema.TryGetValue(name, out var only)
                ? Split(only, upper: true)
                : RootList;

            foreach (var root in roots)
                yield return (root, spec);
        }
    }

    private static string[] Split(string list, bool upper) =>
    [
        .. list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => upper ? s.ToUpperInvariant() : s.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
    ];
}
