using System.Globalization;
using DbnSync.Core;

namespace DbnSync.Worker;

/// <summary>How the dashboard spells sizes, times and states.</summary>
public static class Format
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Bytes(long bytes)
    {
        double value = bytes;
        var unit = 0;

        while (value >= 1000 && unit < Units.Length - 1)
        {
            value /= 1000;
            unit++;
        }

        return unit == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes} B")
            : string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {Units[unit]}");
    }

    public static string Count(int count) => count.ToString("N0", CultureInfo.InvariantCulture);

    public static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Time(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "Z";

    /// <summary>"3 min ago", "in 4 min": coarse, since passes run minutes apart.</summary>
    public static string Relative(DateTimeOffset at, DateTimeOffset now)
    {
        var span = at - now;
        var minutes = (int)Math.Round(Math.Abs(span.TotalMinutes));

        var text = minutes switch
        {
            0 => "now",
            < 90 => $"{minutes} min",
            < 48 * 60 => $"{minutes / 60} h",
            _ => $"{minutes / 1440} d"
        };

        return text == "now" ? text : span < TimeSpan.Zero ? $"{text} ago" : $"in {text}";
    }

    public static string Name(DayState state) => state switch
    {
        DayState.Held => "Stored",
        DayState.Empty => "No data",
        DayState.Pending => "Pending",
        DayState.Owed => "Owed",
        DayState.Lost => "Lost",
        _ => state.ToString()
    };

    public static string Css(DayState state) => $"state-{state.ToString().ToLowerInvariant()}";
}
