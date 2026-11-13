using System.Globalization;

namespace DbnSuite.Sync.Core;

/// <summary>
/// Where things live in the archive, relative to the store's prefix.
/// <code>
/// GLBX.MDP3/mbo/MNQ/2026/MNQ.mbo.20261028.dbn.zst   Databento's file, byte for byte
/// GLBX.MDP3/mbo/MNQ/2026/MNQ.mbo.20261031.empty     asked for, nothing there
/// _dbnsync/jobs/GLBX-20261028-UBTJS4DLA4.json       a job submitted, not yet stored
/// _dbnsync/spend/2026-10/20261028140501123-1a2b.json a paid job, recorded before it was submitted
/// </code>
/// A day is held when either object exists. The empty marker is what tells a
/// Saturday from a day nobody fetched.
/// </summary>
public static class ArchiveKeys
{
    public const string DataSuffix = ".dbn.zst";
    public const string EmptySuffix = ".empty";
    public const string JobsFolder = "_dbnsync/jobs/";

    public static string Folder(string dataset, string schema, string root) =>
        $"{dataset}/{schema}/{root}/";

    public static string Data(string dataset, string schema, string root, DateOnly day) =>
        Day(dataset, schema, root, day) + DataSuffix;

    public static string Empty(string dataset, string schema, string root, DateOnly day) =>
        Day(dataset, schema, root, day) + EmptySuffix;

    public static string Job(string id) => $"{JobsFolder}{id}.json";

    public const string SpendFolder = "_dbnsync/spend/";

    /// <summary>One folder per UTC month, so a month's spending is one listing.</summary>
    public static string SpendMonth(DateOnly month) =>
        string.Create(CultureInfo.InvariantCulture, $"{SpendFolder}{month:yyyy-MM}/");

    public static string Spend(SpendEntry entry) =>
        $"{SpendMonth(DateOnly.FromDateTime(entry.At.UtcDateTime))}{entry.Key}.json";

    public const string PassesFolder = "_dbnsync/passes/";

    /// <summary>One folder per UTC day, so a recent history is a few short listings.</summary>
    public static string PassDay(DateOnly day) =>
        string.Create(CultureInfo.InvariantCulture, $"{PassesFolder}{day:yyyy-MM-dd}/");

    public static string Pass(DateTimeOffset startedAt) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{PassDay(DateOnly.FromDateTime(startedAt.UtcDateTime))}{startedAt.UtcDateTime:HHmmssfff}.json");

    /// <summary>The day a data or empty key holds, or null for anything else.</summary>
    public static DateOnly? DayOf(string key)
    {
        var name = key[(key.LastIndexOf('/') + 1)..];

        var stem = name.EndsWith(DataSuffix, StringComparison.Ordinal)
            ? name[..^DataSuffix.Length]
            : name.EndsWith(EmptySuffix, StringComparison.Ordinal)
                ? name[..^EmptySuffix.Length]
                : null;

        if (stem is null)
            return null;

        var date = stem[(stem.LastIndexOf('.') + 1)..];

        return DateOnly.TryParseExact(date, "yyyyMMdd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var day) ? day : null;
    }

    private static string Day(string dataset, string schema, string root, DateOnly day) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{Folder(dataset, schema, root)}{day:yyyy}/{root}.{schema}.{day:yyyyMMdd}");
}
