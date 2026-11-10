namespace DbnSpool;

/// <summary>What DbnSpool keeps on tape, and how often it looks.</summary>
public sealed class SpoolOptions
{
    public const string Section = "DbnSpool";
    public const string ArchiveSection = "Archive";
    public const string TapesSection = "Tapes";

    /// <summary>The roots to spool, separated by commas.</summary>
    public string Roots { get; set; } = "ES,MES,NQ,MNQ,YM,MYM,RTY,M2K,CL,MCL,GC,MGC";

    /// <summary>How long to wait between looks at the archive.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>A folder for tapes being built and the archive files they draw on.</summary>
    public string WorkPath { get; set; } = "/work";

    /// <summary>Compress tape bodies with zstd, for storage.</summary>
    public bool Compress { get; set; } = true;

    /// <summary>Prove each date against the archive, and upload only what passes.</summary>
    public bool Prove { get; set; } = true;

    /// <summary>
    /// How many times a date may stop DbnSpool, by running out of memory or anything else
    /// that ends the process, before it is set aside. It is tried again once DbnSpool's code or its
    /// memory limit changes.
    /// </summary>
    public int MostAttempts { get; set; } = 2;

    /// <summary>What is wrong with these settings, in words for whoever set them, or null.</summary>
    public string? Problem()
    {
        if (RootList.Count == 0)
            return "DbnSpool:Roots names no root, so there is nothing to spool.";

        // Task.Delay takes no more than about 49 days, and under a minute would only spin.
        if (Interval < TimeSpan.FromMinutes(1) || Interval > TimeSpan.FromDays(30))
            return $"DbnSpool:Interval is {Interval}; it must be from one minute to 30 days.";

        if (MostAttempts < 1)
            return $"DbnSpool:MostAttempts is {MostAttempts}; it must be 1 or more.";

        return string.IsNullOrWhiteSpace(WorkPath) ? "DbnSpool:WorkPath is empty; it names the folder tapes are built in." : null;
    }

    /// <summary>
    /// True when two stores are the same place. The tape store must not be the archive: tapes, catalogs and
    /// the ledger would land among the archive's own folders.
    /// </summary>
    public static bool SamePlace(DbnClient.Archive.ArchiveOptions one, DbnClient.Archive.ArchiveOptions other) =>
        one.IsFolder == other.IsFolder
        && (one.IsFolder
            ? string.Equals(Path.GetFullPath(one.Location).TrimEnd('/', '\\'), Path.GetFullPath(other.Location).TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase)
            : string.Equals(one.Location.TrimEnd('/'), other.Location.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
                && one.Bucket == other.Bucket
                && one.Prefix.Trim('/') == other.Prefix.Trim('/'));

    public IReadOnlyList<string> RootList =>
        [.. Roots.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(r => r.ToUpperInvariant())];

    /// <summary>Where tapes are made before they go to the shelf.</summary>
    public string TapeFolder => Path.Combine(WorkPath, "tapes");

    /// <summary>Where archive files are kept once downloaded.</summary>
    public string CacheFolder => Path.Combine(WorkPath, "cache");
}
