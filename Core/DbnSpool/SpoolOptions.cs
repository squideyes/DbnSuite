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
    /// that ends the process, before it is set aside. It is tried again once DbnSpool's code changes.
    /// </summary>
    public int MostAttempts { get; set; } = 2;

    public IReadOnlyList<string> RootList =>
        [.. Roots.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(r => r.ToUpperInvariant())];

    /// <summary>Where tapes are made before they go to the shelf.</summary>
    public string TapeFolder => Path.Combine(WorkPath, "tapes");

    /// <summary>Where archive files are kept once downloaded.</summary>
    public string CacheFolder => Path.Combine(WorkPath, "cache");
}
