using DbnSuite.Spool;
using Shouldly;

namespace DbnSuite.UnitTests.SpoolTests;

public sealed class SpoolAlertsTests
{
    private const string Problem = "MES: 2026-11-05 set aside after stopping DbnSpool 2 times";
    private const string ShelfProblem = "MES: the tape store holds tapes in the old format";

    [Fact]
    public void A_clean_pass_raises_nothing()
    {
        RootResult[] results = [new("ES", 1, 0, false), new("NQ", 0, 0, true)];

        SpoolAlerts.For(results, []).ShouldBeEmpty();
    }

    [Fact]
    public void Dates_set_aside_are_an_alert()
    {
        RootResult[] results = [new("MES", 0, 1, false, Problem, SetAside: 1)];

        var alert = SpoolAlerts.For(results, []).ShouldHaveSingleItem();

        alert.Key.ShouldBe("set-aside|MES");
        alert.Detail.ShouldStartWith(Problem);
    }

    [Fact]
    public void A_tape_store_problem_is_its_own_alert_and_not_about_memory()
    {
        RootResult[] results = [new("MES", 0, 0, false, Shelf: ShelfProblem)];

        var alert = SpoolAlerts.For(results, []).ShouldHaveSingleItem();

        alert.Key.ShouldBe("shelf|MES");
        alert.Detail.ShouldStartWith(ShelfProblem);
        alert.Detail.ShouldNotContain("mem_limit");
    }

    [Fact]
    public void Other_failures_are_a_separate_alert()
    {
        RootResult[] results = [new("MES", 0, 3, false, Problem, SetAside: 1)];

        var alerts = SpoolAlerts.For(results, []).ToList();

        alerts.Select(a => a.Key).ShouldBe(["set-aside|MES", "failed|MES"]);
        alerts[1].Detail.ShouldStartWith("2 trade dates or steps failed for MES");
    }

    [Fact]
    public void A_date_that_threw_is_named_in_the_alert()
    {
        RootResult[] results = [new("MES", 0, 1, false, Trouble: "2026-11-05: The disk is full.")];

        var alert = SpoolAlerts.For(results, []).ShouldHaveSingleItem();

        alert.Key.ShouldBe("failed|MES");
        alert.Detail.ShouldContain("2026-11-05: The disk is full.");
    }

    [Fact]
    public void A_skipped_root_with_dates_still_set_aside_is_an_alert()
    {
        RootResult[] results = [new("MES", 0, 1, true, Problem, SetAside: 1)];

        SpoolAlerts.For(results, []).ShouldHaveSingleItem().Key.ShouldBe("set-aside|MES");
    }

    [Fact]
    public void A_root_whose_pass_threw_is_an_alert()
    {
        var alert = SpoolAlerts.For([], [new RootError("CL", "The archive is unreachable.")]).ShouldHaveSingleItem();

        alert.Key.ShouldBe("error|CL");
        alert.Title.ShouldBe("Pass failed (CL)");
        alert.Detail.ShouldStartWith("The archive is unreachable.");
    }
}
