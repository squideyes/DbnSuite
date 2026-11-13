using Bunit;
using DbnSuite.Sync.Core;
using DbnSuite.Sync.Worker;
using DbnSuite.Sync.Worker.Components.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using static DbnSuite.UnitTests.TestData;

namespace DbnSuite.UnitTests.Worker;

public sealed class IssueDetailTests : BunitContext
{
    private const string SeqUrl = "http://nas.local:5341";
    private const string Message = "nqbm.dbn.zst: size 10 expected 12";

    private static readonly Issue Corrupt = new(Now, IssueLevel.Error, Errors.Corrupt, Message,
        JobId, Mbo, Root, new DayRange(Day(10, 27), Day(10, 31)));

    private void UseLogs(string? url) =>
        Services.AddSingleton(Options.Create(new DashboardOptions { LogsUrl = url }));

    [Fact]
    public void Explains_the_code_and_quotes_the_message()
    {
        UseLogs(SeqUrl);

        var detail = Render<IssueDetail>(p => p.Add(d => d.Issue, Corrupt));

        detail.Markup.ShouldContain(IssueGuide.For(Errors.Corrupt).Meaning);
        detail.Markup.ShouldContain(IssueGuide.For(Errors.Corrupt).Next);
        detail.Find("pre").TextContent.ShouldBe(Message);
    }

    [Fact]
    public void Links_the_series_the_job_and_the_job_logs()
    {
        UseLogs(SeqUrl);

        var detail = Render<IssueDetail>(p => p.Add(d => d.Issue, Corrupt));

        var links = detail.FindAll(".links a").Select(a => a.GetAttribute("href")).ToList();
        links.ShouldContain($"series/{Mbo}/{Root}");
        links.ShouldContain($"job/{JobId}");
        links.ShouldContain(new DashboardOptions { LogsUrl = SeqUrl }.LogsForJob(JobId));
    }

    [Fact]
    public void Says_how_to_get_log_links_when_none_are_configured()
    {
        UseLogs(null);

        var detail = Render<IssueDetail>(p => p
            .Add(d => d.Issue, Corrupt)
            .Add(d => d.ShowJobLink, false));

        detail.FindAll(".links a").Select(a => a.GetAttribute("href")).ShouldBe([$"series/{Mbo}/{Root}"]);
        detail.Markup.ShouldContain("Dashboard__LogsUrl");
    }

    [Fact]
    public void Says_when_an_issue_fixes_itself()
    {
        UseLogs(null);

        var detail = Render<IssueDetail>(p => p.Add(d => d.Issue, Corrupt));

        detail.Find("dd .chip").ClassList.ShouldContain("good");
    }

    [Fact]
    public void Says_when_an_issue_may_need_a_person()
    {
        UseLogs(null);

        var detail = Render<IssueDetail>(p => p.Add(d => d.Issue, Corrupt with { Code = Errors.Rejected }));

        detail.Find("dd .chip").ClassList.ShouldContain("warning");
    }

    [Fact]
    public void An_issue_of_no_job_links_the_logs_around_its_moment_and_no_series()
    {
        UseLogs(SeqUrl);
        var issue = new Issue(Now, IssueLevel.Error, IssueCodes.Crashed, Message);

        var detail = Render<IssueDetail>(p => p.Add(d => d.Issue, issue));

        detail.FindAll(".links a").Select(a => a.GetAttribute("href")).ShouldBe(
            [new DashboardOptions { LogsUrl = SeqUrl }.LogsAround(Now)]);
    }
}
