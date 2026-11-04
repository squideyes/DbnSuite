using System.Globalization;

namespace DbnFetch;

/// <summary>How dbnf writes numbers, sizes and days at the terminal.</summary>
public static class Text
{
    public static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Day(DateTimeOffset? time) =>
        time is { } t ? t.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "-";

    /// <summary>Decimal units, as disks and S3 consoles count them.</summary>
    public static string Bytes(long bytes) => bytes switch
    {
        >= 1_000_000_000_000 => Invariant($"{bytes / 1e12:0.00} TB"),
        >= 1_000_000_000 => Invariant($"{bytes / 1e9:0.0} GB"),
        >= 1_000_000 => Invariant($"{bytes / 1e6:0.0} MB"),
        >= 1_000 => Invariant($"{bytes / 1e3:0.0} kB"),
        _ => Invariant($"{bytes} B")
    };

    public static string Count(long count) => count.ToString("N0", CultureInfo.InvariantCulture);

    public static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    /// <summary>Rows as aligned columns under a header; numbers right-aligned when <paramref name="right"/> says so.</summary>
    public static IEnumerable<string> Table(string[] header, IReadOnlyList<string[]> rows, bool[]? right = null)
    {
        var widths = header.Select((h, i) => Math.Max(h.Length, rows.Count == 0 ? 0 : rows.Max(r => r[i].Length))).ToArray();

        string Line(string[] cells) => string.Join("  ", cells.Select((c, i) =>
            right is not null && right[i] ? c.PadLeft(widths[i]) : c.PadRight(widths[i]))).TrimEnd();

        yield return Line(header);

        foreach (var row in rows)
            yield return Line(row);
    }
}
