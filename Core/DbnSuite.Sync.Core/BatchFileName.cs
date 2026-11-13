using System.Globalization;

namespace DbnSuite.Sync.Core;

/// <summary>Reads the names Databento gives a batch job's files.</summary>
public static class BatchFileName
{
    /// <summary>
    /// Manifests, condition reports and metadata: useful to Databento, not data.
    /// </summary>
    public static bool IsSupport(string name) =>
        name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The day a daily-split file holds, read from its name:
    /// <c>glbx-mdp3-20261028.mbo.dbn.zst</c>. Null for a file spanning a range
    /// (<c>glbx-mdp3-20260901-20261027...</c>), which a daily job never makes.
    /// </summary>
    public static DateOnly? DayOf(string name)
    {
        var parts = name.Split('.')[0].Split('-');

        if (parts.Length < 3 || IsDate(parts[^2], out _))
            return null;

        return IsDate(parts[^1], out var day) ? day : null;
    }

    private static bool IsDate(string text, out DateOnly day) =>
        DateOnly.TryParseExact(text, "yyyyMMdd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out day);
}
