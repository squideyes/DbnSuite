using System.Globalization;

namespace DbnSuite.Client.Time;

/// <summary>Time zones by IANA name, and DBN's nanosecond times shown in them.</summary>
public static class Zones
{
    /// <summary>CME's own time, which DbnSync uses unless told otherwise.</summary>
    public const string Default = "America/Chicago";

    /// <summary>
    /// The zone an IANA name such as <c>America/New_York</c> names, or
    /// <c>UTC</c>, or <c>local</c> for this machine's own.
    /// </summary>
    public static Result<TimeZoneInfo> Find(string name)
    {
        if (string.Equals(name, "local", StringComparison.OrdinalIgnoreCase))
            return TimeZoneInfo.Local;

        if (string.Equals(name, "UTC", StringComparison.OrdinalIgnoreCase))
            return TimeZoneInfo.Utc;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(name);
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return new Error("input.bad_zone",
                $"\"{name}\" is not a time zone this computer knows; use an IANA name such as America/Chicago, or UTC.");
        }
    }

    /// <summary>
    /// ISO 8601 in <paramref name="zone"/> with all nine fractional digits and
    /// the offset, such as <c>2026-10-28T08:30:00.123456789-05:00</c>.
    /// </summary>
    public static string Format(ulong nanos, TimeZoneInfo zone)
    {
        var utc = DateTimeOffset.UnixEpoch.AddSeconds(nanos / 1_000_000_000);
        var local = TimeZoneInfo.ConvertTime(utc, zone);
        var offset = local.Offset;
        var sign = offset < TimeSpan.Zero ? '-' : '+';

        return string.Create(CultureInfo.InvariantCulture,
            $"{local:yyyy-MM-ddTHH:mm:ss}.{nanos % 1_000_000_000:D9}{(offset == TimeSpan.Zero && zone == TimeZoneInfo.Utc ? "Z" : $"{sign}{offset.Duration():hh\\:mm}")}");
    }
}
