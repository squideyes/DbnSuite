using Bunit;
using DbnSuite.Sync.Core;
using DbnSuite.Sync.Worker;
using DbnSuite.Sync.Worker.Components.Pages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using static DbnSuite.UnitTests.TestData;

namespace DbnSuite.UnitTests.Worker;

public sealed class JobPageTests : BunitContext
{
    private const string Unreachable = "glbx-mdp3-20261028.mbo.dbn.zst: connection reset";

    private static readonly DayRange Range = new(Day(10, 27), Day(10, 31));
    private static readonly IReadOnlyList<PendingJob> NoPending = [];

    private readonly IArchiveStore store = Substitute.For<IArchiveStore>();
    private readonly IDatabento databento = Substitute.For<IDatabento>();
    private readonly CorpusIndex index;

    public JobPageTests()
    {
        var clock = new FakeTimeProvider(Now);
        var options = new SyncOptions { Roots = Root, Schemas = Mbo };
        var scopes = new ServiceCollection().AddSingleton(databento).BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

        index = new CorpusIndex(scopes, store, options, clock, NullLogger<CorpusIndex>.Instance);

        Services.AddSingleton(store);
        Services.AddSingleton(databento);
        Services.AddKeyedSingleton<TimeProvider>(SyncClock.Key, clock);
        Services.AddSingleton(Options.Create(new DashboardOptions()));
        Services.AddSingleton(index);
        Services.AddSingleton(options);

        store.ListPassesAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<PassSummary>>([]));
    }

    private void Passes(params Issue[] issues) =>
        store.ListPassesAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<PassSummary>>(
            [
                new(Now, Now, 0, 0, 0, 0, 0, 0, 0, 0, 0, null, issues)
            ]));

    private static Issue Warning(int minutesAgo) =>
        new(Now.AddMinutes(-minutesAgo), IssueLevel.Warning, Errors.Unreachable, Unreachable, JobId, Mbo, Root, Range);

    [Fact]
    public void A_job_in_flight_shows_what_Databento_says()
    {
        IReadOnlyList<PendingJob> pending = [new(JobId, Root, Mbo, Range, Now.AddHours(-1))];
        store.ListPendingAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(pending));
        databento.GetStateAsync(JobId, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Result<JobState>>(JobState.Done));

        var page = Render<JobPage>(p => p.Add(j => j.Id, JobId));

        page.WaitForAssertion(() => page.Find(".verdict").TextContent.ShouldContain("next pass collects it"));
        page.Markup.ShouldContain($"{Root} {Mbo}");
    }

    [Fact]
    public void Repeats_of_one_problem_read_as_one()
    {
        store.ListPendingAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(NoPending));
        Passes(Warning(5), Warning(10), Warning(15));

        var page = Render<JobPage>(p => p.Add(j => j.Id, JobId));

        page.WaitForAssertion(() => page.FindAll(".problem").Count.ShouldBe(1));
        page.Find(".problem").TextContent.ShouldContain("3 times");
        page.FindAll("tbody tr").Count.ShouldBe(3);
        page.Find(".verdict").TextContent.ShouldContain("No longer in flight");
        databento.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public void Issues_of_other_jobs_stay_off_the_page()
    {
        store.ListPendingAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(NoPending));
        Passes(Warning(5) with { JobId = OtherJobId });

        var page = Render<JobPage>(p => p.Add(j => j.Id, JobId));

        page.WaitForAssertion(() => page.Find(".verdict").TextContent.ShouldContain("No record of this job"));
        page.FindAll(".problem").ShouldBeEmpty();
    }

    [Theory]
    [InlineData(JobState.Working, "still preparing it")]
    [InlineData(JobState.Gone, "no longer knows it")]
    public void A_job_in_flight_reads_as_Databento_sees_it(JobState state, string verdict)
    {
        InFlight(state);

        var page = Render<JobPage>(p => p.Add(j => j.Id, JobId));

        page.WaitForAssertion(() => page.Find(".verdict").TextContent.ShouldContain(verdict));
    }

    [Fact]
    public void With_Databento_unavailable_the_job_is_still_in_flight()
    {
        InFlight(new Error(Errors.Unreachable, Unreachable));

        var page = Render<JobPage>(p => p.Add(j => j.Id, JobId));

        page.WaitForAssertion(() => page.Find(".verdict").TextContent.ShouldContain($"unavailable: {Unreachable}"));
    }

    [Fact]
    public void Tiles_show_the_job_s_days_and_when_it_went()
    {
        InFlight(JobState.Working);

        var page = Render<JobPage>(p => p.Add(j => j.Id, JobId));

        page.WaitForAssertion(() => page.FindAll(".tile").Count.ShouldBe(4));
        page.Markup.ShouldContain(Range.ToString());
        page.Markup.ShouldContain("60 min ago");
    }

    [Fact]
    public async Task A_job_whose_days_are_all_stored_says_so()
    {
        await StoredAsync(Range);
        store.ListPendingAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(NoPending));
        Passes(Warning(5));

        var page = Render<JobPage>(p => p.Add(j => j.Id, JobId));

        page.WaitForAssertion(() => page.Find(".verdict").TextContent.ShouldContain("Stored: every day it asked for"));
        page.FindAll(".day-grid").Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_job_with_a_day_still_missing_says_its_days_are_planned_again()
    {
        await StoredAsync(new DayRange(Range.From, Range.Upto.AddDays(-1)));
        store.ListPendingAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(NoPending));
        Passes(Warning(5));

        var page = Render<JobPage>(p => p.Add(j => j.Id, JobId));

        page.WaitForAssertion(() => page.Find(".verdict").TextContent.ShouldContain("planned again"));
    }

    [Fact]
    public void A_problem_that_happened_once_says_when()
    {
        store.ListPendingAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(NoPending));
        Passes(Warning(5));

        var page = Render<JobPage>(p => p.Add(j => j.Id, JobId));

        page.WaitForAssertion(() => page.Find(".problem").TextContent.ShouldContain("Once, "));
        page.Find(".problem .chip").ClassList.ShouldContain("warning");
    }

    [Fact]
    public void An_error_is_marked_as_one()
    {
        store.ListPendingAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(NoPending));
        Passes(Warning(5) with { Level = IssueLevel.Error });

        var page = Render<JobPage>(p => p.Add(j => j.Id, JobId));

        page.WaitForAssertion(() => page.Find(".problem .chip").ClassList.ShouldContain("critical"));
        page.Find("tbody td:nth-child(2)").TextContent.ShouldContain("Error");
    }

    [Fact]
    public void A_store_that_cannot_be_read_says_so()
    {
        store.ListPendingAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException(Unreachable));

        var page = Render<JobPage>(p => p.Add(j => j.Id, JobId));

        page.WaitForAssertion(() => page.Find("[role=alert]").TextContent.ShouldContain(Unreachable));
    }

    [Fact]
    public void A_job_still_loading_says_so()
    {
        store.ListPendingAsync(Arg.Any<CancellationToken>()).Returns(new TaskCompletionSource<IReadOnlyList<PendingJob>>().Task);

        var page = Render<JobPage>(p => p.Add(j => j.Id, JobId));

        page.Markup.ShouldContain("Loading");
    }

    [Fact]
    public void Leaving_the_page_cancels_what_it_was_reading()
    {
        CancellationToken? reading = null;
        store.ListPendingAsync(Arg.Do<CancellationToken>(t => reading = t))
            .Returns(new TaskCompletionSource<IReadOnlyList<PendingJob>>().Task);
        var page = Render<JobPage>(p => p.Add(j => j.Id, JobId));
        var token = reading!.Value;

        page.Instance.Dispose();

        token.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public void The_logs_button_appears_when_seq_is_configured()
    {
        store.ListPendingAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(NoPending));
        Services.AddSingleton(Options.Create(new DashboardOptions { LogsUrl = SeqUrl }));

        var page = Render<JobPage>(p => p.Add(j => j.Id, JobId));

        page.WaitForAssertion(() => page.Find("a.button").GetAttribute("href").ShouldStartWith(SeqUrl));
    }

    private const string SeqUrl = "http://nas.local:5341";

    private void InFlight(Result<JobState> state)
    {
        IReadOnlyList<PendingJob> pending = [new(JobId, Root, Mbo, Range, Now.AddHours(-1))];

        store.ListPendingAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(pending));
        databento.GetStateAsync(JobId, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(state));
    }

    /// <summary>Scans a store holding <paramref name="days"/> of the job's series.</summary>
    private async Task StoredAsync(DayRange days)
    {
        IReadOnlyList<StoredDay> stored = [.. days.Each().Select(d => new StoredDay(d, 1, Empty: false))];

        store.ListPendingAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(NoPending));
        store.ListDaysAsync(Mbo, Root, Arg.Any<CancellationToken>()).Returns(Task.FromResult(stored));
        databento.GetAvailableEndsAsync(Dataset, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Result<IReadOnlyDictionary<string, DateTimeOffset>>>(new Error(Errors.Unreachable, Unreachable)));

        await index.RefreshAsync(CancellationToken.None);
    }
}
