namespace DbnSuite.Client.Calendars;

/// <summary>How a fixed-date holiday that falls on a weekend moves to a weekday.</summary>
public enum Observance
{
    /// <summary>It doesn't move: on a weekend it has no effect.</summary>
    None = 1,

    /// <summary>Saturday moves to Friday, Sunday to Monday, as for July 4 and Christmas.</summary>
    NearestWeekday = 2,

    /// <summary>Only Sunday moves, to Monday, as for New Year's Day: a Saturday New Year's moves nowhere.</summary>
    SundayToMonday = 3
}

/// <summary>Which day of a year a holiday rule starts from.</summary>
public abstract record HolidayDay
{
    /// <summary>The day in a year, or null when there is none that year.</summary>
    public abstract DateOnly? In(int year);
}

/// <summary>A fixed date, such as December 25.</summary>
public sealed record FixedDay(int Month, int Day, Observance Observed) : HolidayDay
{
    public override DateOnly? In(int year)
    {
        if (Day > DateTime.DaysInMonth(year, Month))
            return null;

        var date = new DateOnly(year, Month, Day);

        return (date.DayOfWeek, Observed) switch
        {
            (DayOfWeek.Saturday, Observance.NearestWeekday) => date.AddDays(-1),
            (DayOfWeek.Sunday, Observance.NearestWeekday or Observance.SundayToMonday) => date.AddDays(1),
            (DayOfWeek.Saturday or DayOfWeek.Sunday, _) => null,
            _ => date
        };
    }
}

/// <summary>The nth weekday of a month, such as the fourth Thursday of November. <c>N = -1</c> is the last.</summary>
public sealed record NthWeekday(int Month, DayOfWeek Weekday, int N) : HolidayDay
{
    public override DateOnly? In(int year)
    {
        if (N < 0)
        {
            var last = new DateOnly(year, Month, 1).AddMonths(1).AddDays(-1);

            return last.AddDays(-(((int)last.DayOfWeek - (int)Weekday + 7) % 7));
        }

        var first = new DateOnly(year, Month, 1);
        var date = first.AddDays(((int)Weekday - (int)first.DayOfWeek + 7) % 7 + 7 * (N - 1));

        // A fifth Monday the month doesn't have.
        return date.Month == Month ? date : null;
    }
}

/// <summary>Easter Sunday, from which Good Friday is two days back.</summary>
public sealed record Easter : HolidayDay
{
    // The anonymous Gregorian algorithm (Meeus/Jones/Butcher).
    public override DateOnly? In(int year)
    {
        int a = year % 19, b = year / 100, c = year % 100, d = b / 4, e = b % 4;
        int f = (b + 8) / 25, g = (b - f + 1) / 3, h = (19 * a + b - d - g + 15) % 30;
        int i = c / 4, k = c % 4, l = (32 + 2 * e + 2 * i - h - k) % 7, m = (a + 11 * h + 22 * l) / 451;
        int month = (h + l - 7 * m + 114) / 31, day = (h + l - 7 * m + 114) % 31 + 1;

        return new DateOnly(year, month, day);
    }
}

/// <summary>
/// A recurring change to the usual hours, such as "Thanksgiving closes at 12:00". Rules project the schedule
/// into any year; a dated <see cref="CalendarChange"/> replaces what they say for its date.
/// </summary>
/// <param name="Offset">Days from <paramref name="Day"/>, as -2 from Easter for Good Friday.</param>
/// <param name="Kind">Closed, an early close or a late open.</param>
/// <param name="At">The local time an early close closes or a late open opens, on the holiday's date.</param>
/// <param name="Weekdays">When given, the rule applies only if the date falls on one of these.</param>
/// <param name="DayOfMonth">When given, the rule applies only if the date's day of the month is in this range.</param>
/// <param name="Since">The first year it applies, such as 2022 for Juneteenth.</param>
/// <param name="Until">The last year it applies.</param>
public sealed record HolidayRule(
    string Name,
    HolidayDay Day,
    int Offset,
    CalendarChangeKind Kind,
    TimeOnly? At,
    IReadOnlyList<DayOfWeek>? Weekdays = null,
    (int From, int To)? DayOfMonth = null,
    int? Since = null,
    int? Until = null)
{
    /// <summary>The date the rule falls on in a year, or null when it doesn't apply that year.</summary>
    public DateOnly? On(int year)
    {
        // A year at either end of the calendar has no room for an offset or an observed day.
        if (year < Since || year > Until || year is < 2 or > 9998)
            return null;

        if (Day.In(year)?.AddDays(Offset) is not { } date)
            return null;

        if (Weekdays is { } weekdays && !weekdays.Contains(date.DayOfWeek))
            return null;

        if (DayOfMonth is { } range && (date.Day < range.From || date.Day > range.To))
            return null;

        return date;
    }
}
