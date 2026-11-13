using DbnSuite.Client;
using DbnSuite.Client.Calendars;
using DbnSuite.Client.Tape;
using Shouldly;

namespace DbnSuite.UnitTests.CalendarTests;

/// <summary>Small calendars written for each case, and times as the tests read them.</summary>
internal static class CalendarFixture
{
    public const string Es = "ES";
    public const string Weekdays = """["monday", "tuesday", "wednesday", "thursday", "friday"]""";
    public const string Globex = $$"""{ "open": "17:00", "openDayOffset": -1, "close": "16:00", "tradingDays": {{Weekdays}} }""";

    public static readonly TimeZoneInfo Chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");

    public static string Json(string session, string holidays = "", string changes = "", string roots = """["ES"]""") => $$"""
        {
          "id": "test",
          "timeZone": "America/Chicago",
          "roots": {{roots}},
          "session": {{session}},
          "holidays": [ {{holidays}} ],
          "changes": [ {{changes}} ]
        }
        """;

    public static RuleCalendar Calendar(string session, string holidays = "", string changes = "", string roots = """["ES"]""")
    {
        var read = RuleCalendar.Parse(Json(session, holidays, changes, roots));

        read.Error.ShouldBeNull();

        return read.Value;
    }

    public static RuleCalendar Cme(string changes = "") => Calendar(Globex, changes: changes);

    public static SessionDay Day(RuleCalendar calendar, DateOnly date, string symbol = Es)
    {
        var day = calendar.Find(symbol, date);

        day.Error.ShouldBeNull();

        return day.Value;
    }

    public static string NoSession(RuleCalendar calendar, DateOnly date, string symbol = Es)
    {
        var day = calendar.Find(symbol, date);

        day.Error.ShouldNotBeNull();
        day.Error.Code.ShouldBe(CalendarErrors.NoSession);

        return day.Error.Message;
    }

    public static string[] Problems(Result<RuleCalendar> read)
    {
        read.Error.ShouldNotBeNull();
        read.Error.Code.ShouldBe(CalendarErrors.Invalid);

        return read.Error.Message.Split(Environment.NewLine);
    }

    public static long Utc(int year, int month, int day, int hour, int minute = 0, int second = 0) =>
        (new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc) - DateTime.UnixEpoch).Ticks * 100;

    public static DateTime Local(long nanos) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.UnixEpoch.AddTicks(nanos / 100), Chicago);

    public static TimeOnly Clock(long nanos) => TimeOnly.FromDateTime(Local(nanos));

    public static TimeOnly? Clock(long? nanos) => nanos is { } n ? Clock(n) : null;
}
