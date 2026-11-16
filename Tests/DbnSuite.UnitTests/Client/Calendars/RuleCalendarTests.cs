using DbnSuite.Client;
using DbnSuite.Client.Calendars;
using DbnSuite.Client.Tape;
using Shouldly;
using static DbnSuite.UnitTests.CalendarTests.CalendarFixture;

namespace DbnSuite.UnitTests.CalendarTests;

/// <summary>The calendar's rules, on small calendars written for each case. Ported from AlgoSpec's, and Flonk's before it.</summary>
public sealed class RuleCalendarTests
{
    private const string Crude = "CL";
    private const string NoHoursRoot = "ZZ";

    [Fact]
    public void A_session_opens_the_evening_before_at_1700_and_closes_at_1600_across_the_fall_back_change()
    {
        var calendar = Cme();

        var friday = Day(calendar, new DateOnly(2025, 10, 31));
        var monday = Day(calendar, new DateOnly(2025, 11, 3));

        friday.Open.ShouldBe(Utc(2025, 10, 30, 22));
        friday.Close.ShouldBe(Utc(2025, 10, 31, 21));
        monday.Open.ShouldBe(Utc(2025, 11, 2, 23));
        monday.Close.ShouldBe(Utc(2025, 11, 3, 22));
    }

    [Fact]
    public void A_session_moves_an_hour_earlier_in_utc_across_the_spring_forward_change()
    {
        var calendar = Cme();

        Day(calendar, new DateOnly(2026, 3, 6)).Open.ShouldBe(Utc(2026, 3, 5, 23));
        Day(calendar, new DateOnly(2026, 3, 9)).Open.ShouldBe(Utc(2026, 3, 8, 22));
    }

    [Fact]
    public void A_session_that_spans_the_change_has_its_real_length()
    {
        var calendar = Calendar("""{ "open": "17:00", "openDayOffset": -1, "close": "16:00", "tradingDays": ["sunday"] }""");

        var spring = Day(calendar, new DateOnly(2026, 3, 8));
        var fall = Day(calendar, new DateOnly(2025, 11, 2));

        TimeSpan.FromTicks((spring.Close - spring.Open) / 100).ShouldBe(TimeSpan.FromHours(22));
        TimeSpan.FromTicks((fall.Close - fall.Open) / 100).ShouldBe(TimeSpan.FromHours(24));
    }

    [Fact]
    public void A_time_in_the_spring_forward_gap_gives_no_session_and_says_why()
    {
        var calendar = Calendar("""{ "open": "02:30", "close": "04:00", "tradingDays": ["sunday"] }""");

        NoSession(calendar, new DateOnly(2026, 3, 8)).ShouldContain("whose clocks skip it");
        Day(calendar, new DateOnly(2026, 3, 1)).Open.ShouldBe(Utc(2026, 3, 1, 8, 30));
    }

    [Fact]
    public void An_ambiguous_fall_back_time_is_read_as_standard_time()
    {
        var calendar = Calendar("""{ "open": "01:30", "close": "03:00", "tradingDays": ["sunday"] }""");

        Day(calendar, new DateOnly(2025, 11, 2)).Open.ShouldBe(Utc(2025, 11, 2, 7, 30));
    }

    [Fact]
    public void Rth_is_the_root_s_regular_hours_from_0830_to_1500_chicago()
    {
        var day = Day(Cme(), new DateOnly(2026, 7, 21));

        day.RthOpen.ShouldBe(Utc(2026, 7, 21, 13, 30));
        day.RthClose.ShouldBe(Utc(2026, 7, 21, 20));
        day.Breaks.ShouldBeEmpty();
    }

    [Fact]
    public void A_root_with_no_regular_hours_has_no_rth()
    {
        var calendar = Calendar(Globex, roots: $"""["{NoHoursRoot}"]""");
        var day = Day(calendar, new DateOnly(2026, 7, 21), NoHoursRoot);

        (day.RthOpen, day.RthClose).ShouldBe((null, null));
    }

    [Theory]
    [InlineData("ES")]
    [InlineData("es")]
    [InlineData("ESZ6")]
    [InlineData("ESZ26")]
    [InlineData("ES.v.0")]
    public void Any_symbol_of_a_root_it_is_for_has_the_root_s_session(string symbol)
    {
        Day(Cme(), new DateOnly(2026, 7, 21), symbol).ShouldBe(Day(Cme(), new DateOnly(2026, 7, 21)), new SessionDayComparer());
    }

    [Fact]
    public void A_root_it_is_not_for_has_no_session_and_says_so()
    {
        var calendar = Cme();
        var found = calendar.Find(Crude, new DateOnly(2026, 7, 21));

        calendar.For(Crude, new DateOnly(2026, 7, 21)).ShouldBeNull();
        found.Error.ShouldNotBeNull();
        found.Error.Code.ShouldBe(CalendarErrors.NotCovered);
        found.Error.Message.ShouldBe("The test calendar is not for CL.");
    }

    [Fact]
    public void A_symbol_that_isnt_one_is_refused()
    {
        Cme().Find("not a symbol", new DateOnly(2026, 7, 21)).Error.ShouldNotBeNull().Code.ShouldBe(Errors.BadSymbol);
    }

    [Fact]
    public void A_weekend_has_no_session()
    {
        Cme().For(Es, new DateOnly(2026, 7, 18)).ShouldBeNull();
        NoSession(Cme(), new DateOnly(2026, 7, 18)).ShouldBe("2026-07-18 is a Saturday, not a trading day.");
    }

    [Fact]
    public void A_closed_day_has_no_session()
    {
        var calendar = Cme("""{ "tradeDate": "2025-12-25", "kind": "closed", "note": "Christmas" }""");

        NoSession(calendar, new DateOnly(2025, 12, 25)).ShouldBe("2025-12-25 is closed: Christmas.");
    }

    [Fact]
    public void An_early_close_shortens_the_session_and_rth_with_it()
    {
        var calendar = Cme("""{ "tradeDate": "2026-07-03", "kind": "earlyClose", "at": "2026-07-03T12:00", "note": "Independence Day" }""");
        var day = Day(calendar, new DateOnly(2026, 7, 3));

        day.Close.ShouldBe(Utc(2026, 7, 3, 17));
        day.RthClose.ShouldBe(Utc(2026, 7, 3, 17));
    }

    [Fact]
    public void An_early_close_before_rth_opens_leaves_no_rth()
    {
        var calendar = Cme("""{ "tradeDate": "2026-04-03", "kind": "earlyClose", "at": "2026-04-03T08:15", "note": "Good Friday" }""");
        var day = Day(calendar, new DateOnly(2026, 4, 3));

        day.Close.ShouldBe(Utc(2026, 4, 3, 13, 15));
        (day.RthOpen, day.RthClose).ShouldBe((null, null));
    }

    [Fact]
    public void An_early_close_before_the_open_leaves_no_session_and_says_why()
    {
        var calendar = Cme("""{ "tradeDate": "2026-07-21", "kind": "earlyClose", "at": "2026-07-20T16:00", "note": "Shut" }""");

        NoSession(calendar, new DateOnly(2026, 7, 21)).ShouldBe("2026-07-21 has no session: Shut leaves none.");
    }

    [Fact]
    public void An_early_close_later_than_the_close_changes_nothing()
    {
        var calendar = Cme("""{ "tradeDate": "2026-07-21", "kind": "earlyClose", "at": "2026-07-21T16:30", "note": "Late" }""");

        Day(calendar, new DateOnly(2026, 7, 21)).Close.ShouldBe(Utc(2026, 7, 21, 21));
    }

    [Fact]
    public void A_late_open_moves_the_open_later_and_the_latest_wins()
    {
        var calendar = Cme("""
            { "tradeDate": "2025-04-03", "kind": "lateOpen", "at": "2025-04-02T17:02", "note": "Late open" },
            { "tradeDate": "2025-04-03", "kind": "lateOpen", "at": "2025-04-02T17:05", "note": "Later open" },
            { "tradeDate": "2025-04-03", "kind": "lateOpen", "at": "2025-04-02T16:00", "note": "Earlier than the open" }
            """);
        var day = Day(calendar, new DateOnly(2025, 4, 3));

        day.Open.ShouldBe(Utc(2025, 4, 2, 22, 5));
        day.RthOpen.ShouldBe(Utc(2025, 4, 3, 13, 30));
    }

    [Fact]
    public void A_late_open_after_0830_moves_rth_with_it()
    {
        var calendar = Cme("""{ "tradeDate": "2026-07-21", "kind": "lateOpen", "at": "2026-07-21T10:00", "note": "Outage" }""");

        Day(calendar, new DateOnly(2026, 7, 21)).RthOpen.ShouldBe(Utc(2026, 7, 21, 15));
    }

    [Fact]
    public void Halts_are_cut_to_the_session_ordered_and_empty_ones_dropped()
    {
        var calendar = Cme("""
            { "tradeDate": "2025-11-28", "kind": "halt", "from": "2025-11-28T10:00", "to": "2025-11-28T10:05", "note": "Second" },
            { "tradeDate": "2025-11-28", "kind": "halt", "from": "2025-11-27T20:44:27", "to": "2025-11-28T07:30", "note": "Outage" },
            { "tradeDate": "2025-11-28", "kind": "halt", "from": "2025-11-28T15:55", "to": "2025-11-28T17:00", "note": "Runs past the close" },
            { "tradeDate": "2025-11-28", "kind": "halt", "from": "2025-11-27T12:00", "to": "2025-11-27T13:00", "note": "Before the open" }
            """);

        Day(calendar, new DateOnly(2025, 11, 28)).Breaks.ShouldBe(
        [
            new SessionBreak(Utc(2025, 11, 28, 2, 44, 27), Utc(2025, 11, 28, 13, 30)),
            new SessionBreak(Utc(2025, 11, 28, 16), Utc(2025, 11, 28, 16, 5)),
            new SessionBreak(Utc(2025, 11, 28, 21, 55), Utc(2025, 11, 28, 22)),
        ]);
    }

    [Fact]
    public void A_daily_halt_applies_only_between_its_dates()
    {
        var calendar = Calendar($$"""
            { "open": "17:00", "openDayOffset": -1, "close": "16:00", "tradingDays": {{Weekdays}},
              "dailyHalts": [ { "from": "15:15", "to": "15:30", "since": "2021-06-01", "until": "2021-06-25" } ] }
            """);

        Day(calendar, new DateOnly(2021, 5, 28)).Breaks.ShouldBeEmpty();
        Day(calendar, new DateOnly(2021, 6, 25)).Breaks.ShouldBe([new SessionBreak(Utc(2021, 6, 25, 20, 15), Utc(2021, 6, 25, 20, 30))]);
        Day(calendar, new DateOnly(2021, 6, 28)).Breaks.ShouldBeEmpty();
    }

    [Fact]
    public void At_finds_the_session_a_moment_belongs_to_across_midnight()
    {
        var calendar = Cme();

        // Sunday 18:00 Chicago is in Monday's session.
        calendar.At(Es, Utc(2026, 7, 19, 23)).ShouldNotBeNull().TradeDate.ShouldBe(new DateOnly(2026, 7, 20));

        // Monday night belongs to Tuesday.
        calendar.At(Es, Utc(2026, 7, 21, 3)).ShouldNotBeNull().TradeDate.ShouldBe(new DateOnly(2026, 7, 21));
        calendar.At(Es, Utc(2026, 7, 21, 7)).ShouldNotBeNull().TradeDate.ShouldBe(new DateOnly(2026, 7, 21));
    }

    [Fact]
    public void At_is_half_open_and_finds_nothing_outside_every_session()
    {
        var calendar = Cme();

        calendar.At(Es, Utc(2026, 7, 20, 22)).ShouldNotBeNull().TradeDate.ShouldBe(new DateOnly(2026, 7, 21));
        calendar.At(Es, Utc(2026, 7, 21, 21)).ShouldBeNull();
        calendar.At(Es, Utc(2026, 7, 21, 21, 30)).ShouldBeNull();
        calendar.At(Es, Utc(2026, 7, 18, 12)).ShouldBeNull();
        calendar.At(Crude, Utc(2026, 7, 21, 15)).ShouldBeNull();
    }

    [Fact]
    public void Days_lists_the_sessions_between_two_dates()
    {
        var calendar = Cme("""{ "tradeDate": "2026-07-22", "kind": "closed", "note": "Closed" }""");

        calendar.Days(Es, new DateOnly(2026, 7, 18), new DateOnly(2026, 7, 24)).Select(d => d.TradeDate.Day).ShouldBe([20, 21, 23, 24]);
    }

    [Fact]
    public void Parse_reports_every_problem_at_once()
    {
        var read = RuleCalendar.Parse("""
            {
              "id": "",
              "timeZone": "Mars/Olympus",
              "roots": ["ES", "ESZ6"],
              "session": { "open": "17:00", "openDayOffset": -2, "close": "16:00", "tradingDays": [],
                           "dailyHalts": [ { "from": "10:00", "to": "09:00" } ] },
              "changes": [
                { "tradeDate": "2026-07-21", "kind": "earlyClose", "note": "No time" },
                { "tradeDate": "2026-07-21", "kind": "halt", "from": "2026-07-21T10:00", "to": "2026-07-21T09:00", "note": "Backwards" },
                { "tradeDate": "2026-07-21", "kind": "closed", "at": "2026-07-21T12:00", "note": "Closed with a time" },
                { "tradeDate": "2026-07-21", "kind": "lateOpen", "at": "2026-07-21T09:00", "note": "" }
              ]
            }
            """);

        Problems(read).ShouldBe(
        [
            "The calendar has no id.",
            "Unknown time zone 'Mars/Olympus'.",
            "'ESZ6' is not a root.",
            "The session's openDayOffset must be 0 or -1.",
            "The session has no trading days.",
            "The daily halt from 10:00 must end after it starts.",
            "2026-07-21 EarlyClose: needs at.",
            "2026-07-21 Halt: needs from before to.",
            "2026-07-21 Closed: takes no times.",
            "2026-07-21 LateOpen: needs a note saying what it is.",
        ]);
    }

    [Fact]
    public void Parse_needs_a_session_and_roots()
    {
        Problems(RuleCalendar.Parse("""{ "id": "x", "timeZone": "America/Chicago", "roots": ["ES"] }"""))
            .ShouldBe(["The calendar has no session."]);
        Problems(RuleCalendar.Parse(Json(Globex, roots: "[]"))).ShouldBe(["The calendar names no roots."]);
        Problems(RuleCalendar.Parse(Json("""{ "open": "08:30", "close": "08:00", "tradingDays": ["monday"] }""")))
            .ShouldBe(["A session that opens on its trade date must open before it closes."]);
    }

    [Theory]
    [InlineData("", "The input does not contain any JSON tokens")]
    [InlineData("null", "The file is empty.")]
    [InlineData("""{ "id": "x", "timezone": "America/Chicago" }""", "timezone")]
    [InlineData("""{ "id": "x", "timeZone": "America/Chicago", "session": { "open": "5pm" } }""", "$.session.open")]
    [InlineData("""{ "id": "x", "timeZone": "America/Chicago", "session": { "tradingDays": [1] } }""", "$.session.tradingDays")]
    [InlineData("""{ "id": "x", "timeZone": "America/Chicago", "session": { "tradingDays": ["funday"] } }""", "$.session.tradingDays")]
    public void Parse_fails_closed(string json, string expected)
    {
        var read = RuleCalendar.Parse(json);

        read.Error.ShouldNotBeNull().Code.ShouldBe(CalendarErrors.Invalid);
        read.Error.Message.ShouldContain(expected);
    }

    [Fact]
    public void Parse_accepts_comments_and_trailing_commas()
    {
        var read = RuleCalendar.Parse("""
            {
              // a comment
              "id": "x",
              "timeZone": "America/Chicago",
              "roots": ["ES",],
              "session": { "open": "17:00", "openDayOffset": -1, "close": "16:00", "tradingDays": ["monday",], },
            }
            """);

        read.Error.ShouldBeNull();
        read.Value.Id.ShouldBe("x");
    }

    [Fact]
    public async Task LoadAsync_names_the_file_in_every_problem()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dbnsuite-calendar-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """{ "id": "", "timeZone": "Mars/Olympus", "roots": ["ES"], "session": { "open": "17:00", "openDayOffset": -1, "close": "16:00", "tradingDays": ["monday"] } }""", CancellationToken.None);

        try
        {
            var problems = Problems(await RuleCalendar.LoadAsync(path, CancellationToken.None));

            problems.Length.ShouldBe(2);
            problems.ShouldAllBe(p => p.StartsWith(path + ": "));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LoadAsync_reports_a_missing_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dbnsuite-calendar-missing-{Guid.NewGuid():N}.json");
        var read = await RuleCalendar.LoadAsync(path, CancellationToken.None);

        read.Error.ShouldNotBeNull().Code.ShouldBe(CalendarErrors.Io);
        read.Error.Message.ShouldStartWith($"Could not read {path}: ");
    }

    /// <summary>Records holding lists compare their lists by reference; this compares their contents.</summary>
    private sealed class SessionDayComparer : IEqualityComparer<SessionDay>
    {
        public bool Equals(SessionDay? x, SessionDay? y) =>
            x is not null && y is not null
            && (x.TradeDate, x.Open, x.Close, x.RthOpen, x.RthClose) == (y.TradeDate, y.Open, y.Close, y.RthOpen, y.RthClose)
            && x.Breaks.SequenceEqual(y.Breaks);

        public int GetHashCode(SessionDay day) => day.TradeDate.GetHashCode();
    }
}
