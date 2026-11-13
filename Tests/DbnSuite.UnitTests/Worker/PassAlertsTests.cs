using DbnSuite.Sync.Core;
using DbnSuite.Sync.Worker;
using Shouldly;

namespace DbnSuite.UnitTests.Worker;

public sealed class PassAlertsTests
{
    private const string Root = "ES";
    private const string Schema = "mbo";

    private static readonly DateTimeOffset At = new(2026, 11, 10, 6, 0, 0, TimeSpan.Zero);

    private static PassSummary With(params Issue[] issues) =>
        new(At, At, 0, 0, 0, 0, 0, 0, 0, 0, issues.Count(i => i.Level == IssueLevel.Error), null, issues);

    [Fact]
    public void An_issue_that_needs_a_person_becomes_an_alert()
    {
        var pass = With(new Issue(At, IssueLevel.Error, Errors.Rejected, "401: bad key", Root: Root, Schema: Schema));

        var alert = PassAlerts.For(pass).ShouldHaveSingleItem();

        alert.ShouldSatisfyAllConditions(
            a => a.Key.ShouldBe($"{Errors.Rejected}|{Root}|{Schema}"),
            a => a.Title.ShouldBe("Databento refused (ES mbo)"),
            a => a.Detail.ShouldStartWith("401: bad key What to do: 401 or 403"));
    }

    [Fact]
    public void Issues_DbnSync_handles_by_itself_are_not_alerts()
    {
        var pass = With(
            new Issue(At, IssueLevel.Error, Errors.Unreachable, "timeout", Root: Root, Schema: Schema),
            new Issue(At, IssueLevel.Warning, Errors.NotFree, "quoted $1.20", Root: Root, Schema: Schema));

        PassAlerts.For(pass).ShouldBeEmpty();
    }

    [Fact]
    public void A_crashed_pass_is_an_alert()
    {
        var pass = PassSummary.Crashed(At, At, new IOException("S3 refused the keys"));

        var alert = PassAlerts.For(pass).ShouldHaveSingleItem();

        alert.Title.ShouldBe("Pass crashed");
        alert.Detail.ShouldContain("S3 refused the keys");
    }

    [Fact]
    public void An_unknown_code_is_an_alert()
    {
        var pass = With(new Issue(At, IssueLevel.Error, "databento.new_thing", "something new"));

        PassAlerts.For(pass).ShouldHaveSingleItem().Title.ShouldBe("databento.new_thing");
    }
}
