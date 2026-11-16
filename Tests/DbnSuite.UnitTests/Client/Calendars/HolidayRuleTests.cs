using DbnSuite.Client.Calendars;
using Shouldly;
using static DbnSuite.UnitTests.CalendarTests.CalendarFixture;

namespace DbnSuite.UnitTests.CalendarTests;

/// <summary>Holiday rules, which project a schedule into any year, and announcements, which override them.</summary>
public sealed class HolidayRuleTests
{
    private const string Thanksgiving = """{ "name": "Thanksgiving", "nth": 4, "weekday": "thursday", "month": 11, "kind": "earlyClose", "at": "12:00" }""";

    private static HolidayRule Rule(HolidayDay day, int offset = 0, IReadOnlyList<DayOfWeek>? weekdays = null,
        (int, int)? dayOfMonth = null, int? since = null) =>
        new("Test", day, offset, CalendarChangeKind.Closed, null, weekdays, dayOfMonth, since);

    private static TimeOnly CloseOf(RuleCalendar calendar, DateOnly date) => Clock(Day(calendar, date).Close);

    [Theory]
    [InlineData(2025, 11, 27)]
    [InlineData(2026, 11, 26)]
    [InlineData(2027, 11, 25)]
    public void Nth_weekday_finds_thanksgiving(int year, int month, int day)
    {
        Rule(new NthWeekday(11, DayOfWeek.Thursday, 4)).On(year).ShouldBe(new DateOnly(year, month, day));
    }

    [Theory]
    [InlineData(2025, 5, 26)]
    [InlineData(2026, 5, 25)]
    [InlineData(2027, 5, 31)]
    public void Nth_weekday_minus_one_finds_the_last(int year, int month, int day)
    {
        Rule(new NthWeekday(5, DayOfWeek.Monday, -1)).On(year).ShouldBe(new DateOnly(year, month, day));
    }

    [Fact]
    public void A_fifth_weekday_the_month_lacks_is_no_day()
    {
        Rule(new NthWeekday(2, DayOfWeek.Monday, 5)).On(2027).ShouldBeNull();
        Rule(new NthWeekday(3, DayOfWeek.Monday, 5)).On(2027).ShouldBe(new DateOnly(2027, 3, 29));
    }

    [Theory]
    [InlineData(2024, 3, 29)]
    [InlineData(2025, 4, 18)]
    [InlineData(2026, 4, 3)]
    [InlineData(2027, 3, 26)]
    public void Easter_minus_two_is_good_friday(int year, int month, int day)
    {
        Rule(new Easter(), offset: -2).On(year).ShouldBe(new DateOnly(year, month, day));
    }

    [Theory]
    [InlineData(2020, 7, 3)]
    [InlineData(2021, 7, 5)]
    [InlineData(2025, 7, 4)]
    public void Nearest_weekday_moves_saturday_back_and_sunday_forward(int year, int month, int day)
    {
        Rule(new FixedDay(7, 4, Observance.NearestWeekday)).On(year).ShouldBe(new DateOnly(year, month, day));
    }

    [Fact]
    public void Sunday_to_monday_moves_only_sunday()
    {
        var newYears = Rule(new FixedDay(1, 1, Observance.SundayToMonday));

        newYears.On(2023).ShouldBe(new DateOnly(2023, 1, 2));
        newYears.On(2022).ShouldBeNull();
    }

    [Fact]
    public void A_fixed_day_with_no_observance_does_nothing_on_a_weekend()
    {
        Rule(new FixedDay(12, 24, Observance.None)).On(2022).ShouldBeNull();
    }

    [Fact]
    public void February_29_falls_only_in_a_leap_year()
    {
        Rule(new FixedDay(2, 29, Observance.None)).On(2028).ShouldBe(new DateOnly(2028, 2, 29));
        Rule(new FixedDay(2, 29, Observance.None)).On(2027).ShouldBeNull();
    }

    [Fact]
    public void Weekdays_and_day_of_month_filter_and_since_starts_a_rule()
    {
        Rule(new FixedDay(12, 24, Observance.None), weekdays: [DayOfWeek.Thursday]).On(2026).ShouldBe(new DateOnly(2026, 12, 24));
        Rule(new FixedDay(12, 24, Observance.None), weekdays: [DayOfWeek.Thursday]).On(2025).ShouldBeNull();
        Rule(new Easter(), offset: -2, dayOfMonth: (1, 7)).On(2026).ShouldBe(new DateOnly(2026, 4, 3));
        Rule(new Easter(), offset: -2, dayOfMonth: (1, 7)).On(2025).ShouldBeNull();
        Rule(new FixedDay(6, 19, Observance.NearestWeekday), since: 2022).On(2021).ShouldBeNull();
    }

    [Fact]
    public void A_rule_shortens_the_day_it_projects_and_says_so()
    {
        var calendar = Calendar(Globex, Thanksgiving);
        var date = new DateOnly(2027, 11, 25);

        CloseOf(calendar, date).ShouldBe(new TimeOnly(12, 0));
        calendar.ChangesOn(date).ShouldHaveSingleItem().ShouldBe(new CalendarChange(
            date, CalendarChangeKind.EarlyClose, new DateTime(2027, 11, 25, 12, 0, 0), null, null, "Thanksgiving", RuleCalendar.RuleSource, false));
    }

    [Fact]
    public void A_rule_whose_day_crosses_the_year_end_still_applies()
    {
        var calendar = Calendar(Globex, """{ "name": "New Year's Eve", "fixed": "01-01", "offset": -1, "kind": "closed" }""");

        // January 1 2027 is a Friday, so its rule falls on Thursday, December 31 2026.
        NoSession(calendar, new DateOnly(2026, 12, 31)).ShouldBe("2026-12-31 is closed: New Year's Eve.");
    }

    [Fact]
    public void A_dated_change_replaces_the_rule_for_its_date_but_a_dated_halt_adds_to_it()
    {
        var calendar = Calendar(
            Globex,
            """{ "name": "Good Friday", "easter": true, "offset": -2, "kind": "closed" }""",
            """
            { "tradeDate": "2027-03-26", "kind": "earlyClose", "at": "2027-03-26T08:15", "note": "Jobs report", "source": "CME notice" },
            { "tradeDate": "2028-04-14", "kind": "halt", "from": "2028-04-14T10:00", "to": "2028-04-14T10:05", "note": "Halt" }
            """);

        CloseOf(calendar, new DateOnly(2027, 3, 26)).ShouldBe(new TimeOnly(8, 15));
        NoSession(calendar, new DateOnly(2028, 4, 14)).ShouldBe("2028-04-14 is closed: Good Friday.");
    }

    [Fact]
    public void Announcements_override_the_projection_and_the_calendar_s_own_changes()
    {
        var calendar = Calendar(
            Globex,
            """{ "name": "Labor Day", "nth": 1, "weekday": "monday", "month": 9, "kind": "earlyClose", "at": "12:00" }""",
            """{ "tradeDate": "2027-12-31", "kind": "earlyClose", "at": "2027-12-31T12:15", "note": "Guess", "source": "Projection" }""");

        var announced = RuleCalendar.ParseAnnouncements("""
            {
              "changes": [
                { "tradeDate": "2027-09-06", "kind": "regular", "note": "Normal hours", "source": "CME notice 1" },
                { "tradeDate": "2027-12-31", "kind": "closed", "note": "Closed", "source": "CME notice 2" },
                { "tradeDate": "2027-10-11", "kind": "lateOpen", "at": "2027-10-10T18:00", "note": "Maintenance", "source": "CME notice 3" }
              ]
            }
            """);

        announced.Error.ShouldBeNull();

        var updated = calendar.With(announced.Value);

        updated.Error.ShouldBeNull();
        CloseOf(updated.Value, new DateOnly(2027, 9, 6)).ShouldBe(new TimeOnly(16, 0));
        NoSession(updated.Value, new DateOnly(2027, 12, 31)).ShouldBe("2027-12-31 is closed: Closed.");
        Local(Day(updated.Value, new DateOnly(2027, 10, 11)).Open).ShouldBe(new DateTime(2027, 10, 10, 18, 0, 0));
        updated.Value.ChangesOn(new DateOnly(2027, 9, 6)).ShouldHaveSingleItem().Source.ShouldBe("CME notice 1");

        // The original is unchanged.
        CloseOf(calendar, new DateOnly(2027, 9, 6)).ShouldBe(new TimeOnly(12, 0));
    }

    [Fact]
    public void Parse_reports_every_problem_in_the_rules()
    {
        var read = RuleCalendar.Parse(Json(Globex, """
            { "name": "Two days", "fixed": "01-01", "easter": true, "kind": "closed" },
            { "name": "No kind", "fixed": "01-01" },
            { "name": "Bad date", "fixed": "13-01", "kind": "closed" },
            { "name": "No weekday", "nth": 3, "month": 1, "kind": "closed" },
            { "name": "Range", "fixed": "07-04", "kind": "closed", "dayOfMonth": [1] }
            """));

        Problems(read).ShouldBe(
        [
            "Two days: give exactly one of fixed, nth or easter.",
            "No kind: needs a kind.",
            "Bad date: fixed must be MM-dd, not '13-01'.",
            "No weekday: nth needs a weekday and a month.",
            "Range: dayOfMonth is [from, to].",
        ]);

        var checks = RuleCalendar.Parse(Json(Globex, """
            { "name": "Sixth", "nth": 6, "weekday": "monday", "month": 1, "kind": "closed" },
            { "name": "No time", "fixed": "07-04", "kind": "earlyClose" },
            { "name": "Halt", "fixed": "07-04", "kind": "halt" },
            { "name": "Closed at noon", "fixed": "07-04", "kind": "closed", "at": "12:00" },
            { "name": "Backwards", "fixed": "07-04", "kind": "closed", "dayOfMonth": [7, 1], "since": 2030, "until": 2020 },
            { "name": "No days", "fixed": "07-04", "kind": "closed", "weekdays": [] },
            { "fixed": "07-04", "kind": "closed" }
            """));

        Problems(checks).ShouldBe(
        [
            "Sixth: nth is 1 to 5, or -1 for the last, in a month from 1 to 12.",
            "No time: needs at.",
            "Halt: a holiday rule closes the day, closes it early or opens it late.",
            "Closed at noon: a closed day takes no time.",
            "Backwards: dayOfMonth must run from 1 to 31, low to high.",
            "Backwards: since comes after until.",
            "No days: weekdays, when given, can't be empty.",
            "Every holiday rule needs a name.",
        ]);
    }

    [Fact]
    public void Create_refuses_a_fixed_day_that_isnt_one()
    {
        var rule = new HolidayRule("April 31", new FixedDay(4, 31, Observance.None), 0, CalendarChangeKind.Closed, null);

        Problems(RuleCalendar.Create("x", "America/Chicago", [Es], Cme().Session, [rule], []))
            .ShouldBe(["April 31: 04-31 is not a day of the year."]);
    }

    [Fact]
    public void Parse_announcements_reports_its_problems()
    {
        var missing = RuleCalendar.ParseAnnouncements("""{ "changes": [ { "kind": "closed" } ] }""");
        var timed = RuleCalendar.ParseAnnouncements("""{ "changes": [ { "tradeDate": "2027-09-06", "kind": "closed", "at": "2027-09-06T12:00", "note": "Closed" } ] }""");
        var unknown = RuleCalendar.ParseAnnouncements("""{ "rules": [] }""");

        missing.Error.ShouldNotBeNull().Message.ShouldBe("Every change needs a tradeDate and a kind.");
        timed.Error.ShouldNotBeNull().Message.ShouldBe("2027-09-06 Closed: takes no times.");
        unknown.Error.ShouldNotBeNull().Code.ShouldBe(CalendarErrors.Invalid);
    }

    [Fact]
    public async Task LoadAnnouncementsAsync_reads_a_file_and_names_it_in_every_problem()
    {
        var good = Path.Combine(Path.GetTempPath(), $"dbnsuite-announcements-{Guid.NewGuid():N}.json");
        var bad = Path.Combine(Path.GetTempPath(), $"dbnsuite-announcements-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(good, """{ "changes": [ { "tradeDate": "2027-03-26", "kind": "earlyClose", "at": "2027-03-26T08:15", "note": "Jobs report" } ] }""", CancellationToken.None);
        await File.WriteAllTextAsync(bad, """{ "changes": [ { "kind": "closed" } ] }""", CancellationToken.None);

        try
        {
            var read = await RuleCalendar.LoadAnnouncementsAsync(good, CancellationToken.None);
            var refused = await RuleCalendar.LoadAnnouncementsAsync(bad, CancellationToken.None);

            read.Error.ShouldBeNull();
            read.Value.Changes.ShouldHaveSingleItem().Note.ShouldBe("Jobs report");
            refused.Error.ShouldNotBeNull().Message.ShouldBe($"{bad}: Every change needs a tradeDate and a kind.");
        }
        finally
        {
            File.Delete(good);
            File.Delete(bad);
        }
    }

    [Fact]
    public void An_announced_regular_day_with_times_is_refused()
    {
        var announced = new Announcements([new CalendarChange(new DateOnly(2027, 9, 6), CalendarChangeKind.Regular,
            new DateTime(2027, 9, 6, 12, 0, 0), null, null, "Normal", "CME", false)]);

        Problems(Cme().With(announced)).ShouldBe(["2027-09-06 Regular: takes no times."]);
    }
}
