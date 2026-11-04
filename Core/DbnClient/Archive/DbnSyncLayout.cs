using System.Globalization;

namespace DbnClient.Archive;

/// <summary>
/// Where the DbnSync worker puts things, relative to the archive's prefix.
/// <code>
/// GLBX.MDP3/mbo/MNQ/2026/MNQ.mbo.20261028.dbn.zst   Databento's file, byte for byte
/// GLBX.MDP3/mbo/MNQ/2026/MNQ.mbo.20261031.empty     asked for, nothing there
/// </code>
/// One file per UTC day. A day is held when either object exists; the empty
/// marker is what tells a Saturday from a day nobody fetched.
/// </summary>
public static class DbnSyncLayout
{
    public const string Name = "dbnsync";
    public const string DataSuffix = ".dbn.zst";
    public const string EmptySuffix = ".empty";

    /// <summary>The file an archive names its layout in.</summary>
    public const string LayoutKey = "_dbnsync/layout.json";

    public static string Folder(string dataset, string schema, string root) =>
        $"{dataset}/{schema}/{root}/";

    public static string Data(string dataset, string schema, string root, DateOnly day) =>
        Day(dataset, schema, root, day) + DataSuffix;

    public static string Empty(string dataset, string schema, string root, DateOnly day) =>
        Day(dataset, schema, root, day) + EmptySuffix;

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

    public static bool IsEmpty(string key) => key.EndsWith(EmptySuffix, StringComparison.Ordinal);

    private static string Day(string dataset, string schema, string root, DateOnly day) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{Folder(dataset, schema, root)}{day:yyyy}/{root}.{schema}.{day:yyyyMMdd}");
}
