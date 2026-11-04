using System.Globalization;

namespace DbnClient.Time;

/// <summary>A range of days, both ends included.</summary>
public sealed record DateSpan(DateOnly From, DateOnly To)
{
    public bool Contains(DateOnly day) => day >= From && day <= To;

    public int Days => To.DayNumber - From.DayNumber + 1;

    /// <summary>
    /// The range a user asked for: <paramref name="from"/> and <paramref name="to"/>;
    /// or <paramref name="last"/>, such as <c>30d</c>, <c>8w</c>, <c>6m</c> or
    /// <c>1y</c>, ending <paramref name="today"/>; or <paramref name="since"/> to
    /// <paramref name="today"/>. With only <paramref name="from"/>, that one day.
    /// </summary>
    public static Result<DateSpan> Resolve(DateOnly? from, DateOnly? to, string? last, DateOnly? since, DateOnly today)
    {
        var ways = (from is not null ? 1 : 0) + (last is not null ? 1 : 0) + (since is not null ? 1 : 0);

        if (ways != 1)
            return new Error("input.bad_range", "Give the days as one of --from (with --to), --last or --since.");

        if (to is not null && from is null)
            return new Error("input.bad_range", "--to needs --from.");

        if (since is { } start)
            return Checked(start, today);

        if (last is not null)
        {
            var length = Length(last, today);

            return length is { } begin
                ? Checked(begin, today)
                : new Error("input.bad_range", $"\"{last}\" is not a length such as 30d, 8w, 6m or 1y.");
        }

        return Checked(from!.Value, to ?? from.Value);
    }

    private static Result<DateSpan> Checked(DateOnly from, DateOnly to) =>
        from <= to
            ? new DateSpan(from, to)
            : new Error("input.bad_range", $"The range starts {from:yyyy-MM-dd}, after it ends ({to:yyyy-MM-dd}).");

    /// <summary>The first day of a span of the given length ending <paramref name="today"/>.</summary>
    private static DateOnly? Length(string text, DateOnly today)
    {
        if (text.Length < 2 || !int.TryParse(text[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1)
            return null;

        return char.ToLowerInvariant(text[^1]) switch
        {
            'd' => today.AddDays(1 - n),
            'w' => today.AddDays(1 - 7 * n),
            'm' => today.AddMonths(-n).AddDays(1),
            'y' => today.AddYears(-n).AddDays(1),
            _ => null
        };
    }
}
