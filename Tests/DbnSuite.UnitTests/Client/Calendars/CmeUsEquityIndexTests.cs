using DbnSuite.Client.Calendars;
using Shouldly;
using static DbnSuite.UnitTests.CalendarTests.CalendarFixture;

namespace DbnSuite.UnitTests.CalendarTests;

/// <summary>The calendar the package carries for CME US equity index futures, on days worth remembering.</summary>
public sealed class CmeUsEquityIndexTests
{
    private const string Crude = "CL";

    private static RuleCalendar Cme => RuleCalendar.CmeUsEquityIndex;

    private static TimeOnly CloseOf(int year, int month, int day) => Clock(Day(Cme, new DateOnly(year, month, day)).Close);

    [Fact]
    public void It_reads_and_names_the_roots_it_is_for()
    {
        Cme.Id.ShouldBe("cme-us-equity-index");
        Cme.Zone.Id.ShouldBe("America/Chicago");
        Cme.Roots.ShouldBe(["ES", "MES", "NQ", "MNQ", "YM", "MYM", "RTY", "M2K"]);
    }

    [Theory]
    [InlineData("ES")]
    [InlineData("MES")]
    [InlineData("NQ")]
    [InlineData("MNQ")]
    [InlineData("YM")]
    [InlineData("MYM")]
    [InlineData("RTY")]
    [InlineData("M2K")]
    [InlineData("M2KZ6")]
    [InlineData("MNQ.v.0")]
    public void Every_equity_index_root_has_its_sessions(string symbol)
    {
        var day = Day(Cme, new DateOnly(2026, 7, 21), symbol);

        (Clock(day.RthOpen), Clock(day.RthClose)).ShouldBe((new TimeOnly(8, 30), new TimeOnly(15, 0)));
    }

    [Fact]
    public void It_is_not_for_other_roots()
    {
        Cme.For(Crude, new DateOnly(2026, 7, 21)).ShouldBeNull();
    }

    [Fact]
    public void Its_dated_changes_are_only_what_no_rule_could_know_and_all_are_verified()
    {
        Cme.Changes.Select(x => x.Note).Distinct().ShouldBe(
            ["Market-wide circuit breaker", "National Day of Mourning for President Carter", "CME Globex outage"]);
        Cme.Changes.ShouldAllBe(x => x.Verified && x.Source.Length > 0);
    }

    [Fact]
    public void A_regular_day_runs_from_1700_the_evening_before_to_1600_with_rth_from_0830_to_1500()
    {
        var day = Day(Cme, new DateOnly(2026, 7, 21));

        Local(day.Open).ShouldBe(new DateTime(2026, 7, 20, 17, 0, 0));
        Clock(day.Close).ShouldBe(new TimeOnly(16, 0));
        (Clock(day.RthOpen), Clock(day.RthClose)).ShouldBe((new TimeOnly(8, 30), new TimeOnly(15, 0)));
        day.Breaks.ShouldBeEmpty();
    }

    [Fact]
    public void Thanksgiving_2025_closes_at_noon_and_the_next_day_at_1215_after_the_outage()
    {
        CloseOf(2025, 11, 27).ShouldBe(new TimeOnly(12, 0));

        var friday = Day(Cme, new DateOnly(2025, 11, 28));

        Clock(friday.Close).ShouldBe(new TimeOnly(12, 15));
        friday.Breaks.Select(b => (Clock(b.Start), Clock(b.End))).ShouldBe([(new TimeOnly(20, 44, 27), new TimeOnly(7, 30))]);

        // The outage ended before RTH opened.
        (Clock(friday.RthOpen), Clock(friday.RthClose)).ShouldBe((new TimeOnly(8, 30), new TimeOnly(12, 15)));
    }

    [Fact]
    public void Good_friday_closes_or_on_a_jobs_report_friday_closes_at_0815_with_no_rth()
    {
        NoSession(Cme, new DateOnly(2025, 4, 18)).ShouldBe("2025-04-18 is closed: Good Friday.");

        var jobs = Day(Cme, new DateOnly(2026, 4, 3));

        Clock(jobs.Close).ShouldBe(new TimeOnly(8, 15));
        (jobs.RthOpen, jobs.RthClose).ShouldBe((null, null));
    }

    [Fact]
    public void Christmas_and_new_years_day_are_closed_on_their_observed_days()
    {
        NoSession(Cme, new DateOnly(2025, 12, 25)).ShouldBe("2025-12-25 is closed: Christmas.");
        NoSession(Cme, new DateOnly(2021, 12, 24)).ShouldBe("2021-12-24 is closed: Christmas.");
        NoSession(Cme, new DateOnly(2022, 12, 26)).ShouldBe("2022-12-26 is closed: Christmas.");
        NoSession(Cme, new DateOnly(2026, 1, 1)).ShouldBe("2026-01-01 is closed: New Year's Day.");
        NoSession(Cme, new DateOnly(2023, 1, 2)).ShouldBe("2023-01-02 is closed: New Year's Day.");
    }

    [Fact]
    public void The_day_of_mourning_for_president_carter_closed_at_0830_so_it_had_no_rth()
    {
        var day = Day(Cme, new DateOnly(2025, 1, 9));

        Clock(day.Close).ShouldBe(new TimeOnly(8, 30));
        (day.RthOpen, day.RthClose).ShouldBe((null, null));
    }

    [Fact]
    public void The_daily_1515_halt_ran_until_june_25_2021()
    {
        Day(Cme, new DateOnly(2021, 6, 25)).Breaks.Select(b => (Clock(b.Start), Clock(b.End)))
            .ShouldBe([(new TimeOnly(15, 15), new TimeOnly(15, 30))]);
        Day(Cme, new DateOnly(2021, 6, 28)).Breaks.ShouldBeEmpty();
    }

    [Fact]
    public void The_march_2020_circuit_breakers_are_breaks()
    {
        Day(Cme, new DateOnly(2020, 3, 9)).Breaks.Select(b => (Clock(b.Start), Clock(b.End)))
            .ShouldBe([(new TimeOnly(8, 35, 19), new TimeOnly(8, 49, 13)), (new TimeOnly(15, 15), new TimeOnly(15, 30))]);
    }

    [Fact]
    public void Juneteenth_began_in_2022()
    {
        CloseOf(2021, 6, 18).ShouldBe(new TimeOnly(16, 0));
        CloseOf(2022, 6, 20).ShouldBe(new TimeOnly(12, 0));
    }

    [Fact]
    public void July_3_closes_early_only_when_july_4_falls_tuesday_to_friday()
    {
        CloseOf(2025, 7, 3).ShouldBe(new TimeOnly(12, 15));
        CloseOf(2025, 7, 4).ShouldBe(new TimeOnly(12, 0));

        // July 4 2026 is a Saturday, so Friday July 3 is the observed holiday.
        CloseOf(2026, 7, 3).ShouldBe(new TimeOnly(12, 0));
        CloseOf(2026, 7, 2).ShouldBe(new TimeOnly(16, 0));
    }

    [Fact]
    public void Rth_on_a_short_day_is_cut_to_the_session()
    {
        var thanksgiving = Day(Cme, new DateOnly(2025, 11, 27));

        (Clock(thanksgiving.RthOpen), Clock(thanksgiving.RthClose)).ShouldBe((new TimeOnly(8, 30), new TimeOnly(12, 0)));
        Clock(Day(Cme, new DateOnly(2025, 12, 24)).RthClose).ShouldBe(new TimeOnly(12, 15));
    }

    [Theory]
    [InlineData(2027, 1, 1, null)]
    [InlineData(2027, 1, 18, "12:00")]
    [InlineData(2027, 2, 15, "12:00")]
    [InlineData(2027, 3, 26, null)]
    [InlineData(2027, 5, 31, "12:00")]
    [InlineData(2027, 6, 18, "12:00")]
    [InlineData(2027, 7, 5, "12:00")]
    [InlineData(2027, 9, 6, "12:00")]
    [InlineData(2027, 11, 25, "12:00")]
    [InlineData(2027, 11, 26, "12:15")]
    [InlineData(2027, 12, 24, null)]
    [InlineData(2027, 12, 31, "16:00")]
    public void The_rules_project_2027(int year, int month, int day, string? close)
    {
        var found = Cme.For(Es, new DateOnly(year, month, day));

        if (close is null)
            found.ShouldBeNull();
        else
            Clock(found.ShouldNotBeNull().Close).ShouldBe(TimeOnly.Parse(close, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void A_projected_day_says_it_comes_from_a_rule()
    {
        Cme.ChangesOn(new DateOnly(2027, 11, 25)).ShouldHaveSingleItem().Source.ShouldBe(RuleCalendar.RuleSource);
    }

    [Fact]
    public void An_announced_jobs_report_opens_a_good_friday_the_rules_close()
    {
        var announced = new Announcements([new CalendarChange(new DateOnly(2027, 3, 26), CalendarChangeKind.EarlyClose,
            new DateTime(2027, 3, 26, 8, 15, 0), null, null, "Good Friday: jobs report", "CME notice", false)]);

        var updated = Cme.With(announced);

        updated.Error.ShouldBeNull();
        Clock(Day(updated.Value, new DateOnly(2027, 3, 26)).Close).ShouldBe(new TimeOnly(8, 15));
        Cme.For(Es, new DateOnly(2027, 3, 26)).ShouldBeNull();
    }
}
