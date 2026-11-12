using DbnSync.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using static DbnSync.UnitTests.TestData;

namespace DbnSync.UnitTests.Core;

public sealed class SyncRunnerTests : IDisposable
{
    private static readonly IReadOnlySet<DateOnly> NothingHeld = new HashSet<DateOnly>();

    private static readonly Result<IReadOnlyList<RemoteJob>> NoJobs = (Result<IReadOnlyList<RemoteJob>>)Array.Empty<RemoteJob>();

    private readonly IDatabento databento = Substitute.For<IDatabento>();
    private readonly IArchiveStore store = Substitute.For<IArchiveStore>();
    private readonly FakeTimeProvider clock = new(Now);
    private readonly string scratch = Path.Combine(Path.GetTempPath(), $"dbnsync-tests-{Guid.NewGuid():N}");

    private readonly SyncOptions options;

    public SyncRunnerTests()
    {
        options = new SyncOptions
        {
            Roots = Root,
            Schemas = Mbo,
            HistoryStart = new DateOnly(2020, 1, 1),
            ScratchPath = scratch
        };

        databento.GetAvailableEndsAsync(Dataset, Arg.Any<CancellationToken>())
            .Returns(Ends(Mbo, Trades, Definition));

        databento.ListJobsAsync(Arg.Any<DateOnly>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<CancellationToken>())
            .Returns(NoJobs);

        store.ListPendingAsync(Arg.Any<CancellationToken>()).Returns([]);
        store.ListHeldAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(NothingHeld);
    }

    public void Dispose()
    {
        if (Directory.Exists(scratch))
            Directory.Delete(scratch, recursive: true);
    }

    [Fact]
    public async Task A_job_that_is_not_free_is_never_recorded()
    {
        options.Schemas = Definition;
        databento.SubmitWithinAsync(Arg.Any<BatchRequest>(), Arg.Any<decimal>(), Arg.Any<CancellationToken>())
            .Returns(NotFree());

        var pass = await Runner().RunOnceAsync(CancellationToken.None);

        pass.JobsSubmitted.ShouldBe(0);
        pass.QuotesRefused.ShouldBeGreaterThan(0);
        await store.DidNotReceive().SavePendingAsync(Arg.Any<PendingJob>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_book_job_at_the_window_edge_sheds_its_oldest_day()
    {
        options.MaxPendingJobs = 1;
        var first = new List<DateOnly>();

        databento.SubmitWithinAsync(Arg.Any<BatchRequest>(), Arg.Any<decimal>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<BatchRequest>();
                first.Add(request.Range.From);

                return first.Count == 1 ? NotFree() : (Result<string>)JobId;
            });

        var pass = await Runner().RunOnceAsync(CancellationToken.None);

        pass.JobsSubmitted.ShouldBe(1);
        first.ShouldBe([Day(10, 1), Day(10, 2)]);
        await store.Received(1).SavePendingAsync(
            Arg.Is<PendingJob>(j => j.Id == JobId && j.Range.From == Day(10, 2)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task By_default_every_job_is_submitted_at_zero_and_the_ledger_is_never_touched()
    {
        options.MaxPendingJobs = 3;
        databento.SubmitWithinAsync(Arg.Any<BatchRequest>(), Arg.Any<decimal>(), Arg.Any<CancellationToken>())
            .Returns(JobId);

        await Runner().RunOnceAsync(CancellationToken.None);

        await databento.Received(3).SubmitWithinAsync(Arg.Any<BatchRequest>(), 0m, Arg.Any<CancellationToken>());
        await databento.DidNotReceive().SubmitWithinAsync(Arg.Any<BatchRequest>(),
            Arg.Is<decimal>(d => d != 0m), Arg.Any<CancellationToken>());
        await databento.DidNotReceive().QuoteAsync(Arg.Any<BatchRequest>(), Arg.Any<CancellationToken>());
        await store.DidNotReceive().ListSpendAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
        await store.DidNotReceive().SaveSpendAsync(Arg.Any<SpendEntry>(), Arg.Any<CancellationToken>());
    }

    private static readonly DateTimeOffset AcceptedAt = Now.AddHours(-3);

    private static readonly DateTimeOffset BoughtAt = Now.AddDays(-5);
    private static readonly DateOnly BoughtDay = DateOnly.FromDateTime(BoughtAt.UtcDateTime);
    private static readonly DateOnly LastMonth = Day(10, 1);
    private static readonly DateOnly MonthBefore = Day(9, 1);

    private static SpendEntry Earlier(DayRange range, DateTimeOffset? at = null, string root = Root, string schema = Mbo, string? job = null) =>
        new(Guid.NewGuid().ToString("N"), at ?? BoughtAt, root, schema, range, PaidQuote, job);

    private void Spent(DateOnly month, params SpendEntry[] entries) =>
        store.ListSpendAsync(month, Arg.Any<CancellationToken>()).Returns(entries);

    private void Listing(Result<IReadOnlyList<RemoteJob>> listed) =>
        databento.ListJobsAsync(Arg.Any<DateOnly>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<CancellationToken>()).Returns(listed);

    private Task NeverListed() =>
        databento.DidNotReceive().ListJobsAsync(Arg.Any<DateOnly>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<CancellationToken>());

    /// <summary>
    /// Quotes every job as paid, and answers the listing with what <paramref name="held"/>
    /// makes of the job quoted, which last month's spend ledger shows was bought.
    /// </summary>
    private void PaidJobsFound(Func<BatchRequest, RemoteJob> held)
    {
        BatchRequest? asked = null;

        databento.QuoteAsync(Arg.Any<BatchRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            asked ??= call.Arg<BatchRequest>();

            return PaidQuote;
        });
        store.ListSpendAsync(LastMonth, Arg.Any<CancellationToken>())
            .Returns(_ => [Earlier(asked!.Range)]);
        databento.ListJobsAsync(Arg.Any<DateOnly>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => (Result<IReadOnlyList<RemoteJob>>)new[] { held(asked!) });
    }

    /// <summary>Quotes every job as paid and returns the ranges a pass plans, oldest first, leaving no trace of having asked.</summary>
    private async Task<IReadOnlyList<DayRange>> PlannedAsync()
    {
        var planned = new List<DayRange>();

        databento.QuoteAsync(Arg.Any<BatchRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            planned.Add(call.Arg<BatchRequest>().Range);

            return PaidQuote;
        });
        databento.SubmitWithinAsync(Arg.Any<BatchRequest>(), Arg.Any<decimal>(), Arg.Any<CancellationToken>())
            .Returns(new Error(Errors.Rejected, "no"));

        await Runner().RunOnceAsync(CancellationToken.None);

        databento.ClearReceivedCalls();
        store.ClearReceivedCalls();

        var ranges = planned.ToList();

        databento.QuoteAsync(Arg.Any<BatchRequest>(), Arg.Any<CancellationToken>()).Returns(PaidQuote);

        return ranges;
    }

    private static RemoteJob Same(BatchRequest request, DateTimeOffset? expires = null) => new(JobId, AcceptedAt, expires,
        request.Dataset, request.Schema, request.Symbols, request.StypeIn, request.Range, BatchRequest.Encoding);

    [Fact]
    public async Task A_paid_job_already_at_databento_is_taken_up_for_nothing()
    {
        PayUpTo(perJob: 5m);
        options.MaxPendingJobs = 1;
        PaidJobsFound(r => Same(r));

        var pass = await Runner().RunOnceAsync(CancellationToken.None);

        await databento.DidNotReceive().SubmitWithinAsync(Arg.Any<BatchRequest>(), Arg.Any<decimal>(), Arg.Any<CancellationToken>());
        await store.DidNotReceive().SaveSpendAsync(Arg.Any<SpendEntry>(), Arg.Any<CancellationToken>());
        await store.Received(1).SavePendingAsync(
            Arg.Is<PendingJob>(j => j.Id == JobId && j.SubmittedAt == AcceptedAt), Arg.Any<CancellationToken>());
        pass.JobsSubmitted.ShouldBe(0);
        pass.JobsPending.ShouldBe(1);
        pass.Spent.ShouldBe(0m);
        var issue = pass.Issues.ShouldHaveSingleItem();
        issue.Code.ShouldBe(IssueCodes.JobAdopted);
        issue.Level.ShouldBe(IssueLevel.Warning);
        issue.JobId.ShouldBe(JobId);
    }

    [Fact]
    public async Task A_job_taken_up_is_no_longer_owed()
    {
        PayUpTo(perJob: 5m);
        options.Schemas = Definition;
        options.HistoryStart = Today.AddDays(-2);
        PaidJobsFound(r => Same(r));

        var pass = await Runner().RunOnceAsync(CancellationToken.None);

        pass.JobsOwed.ShouldBe(0);
    }

    [Fact]
    public async Task A_job_is_taken_up_even_with_nothing_left_to_spend()
    {
        PayUpTo(perJob: 1m, perDay: 1m, perMonth: 1m);
        options.MaxPendingJobs = 1;
        store.ListSpendAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns([new SpendEntry("k", Now, Root, Mbo, new DayRange(Today, Today), 1m)]);
        PaidJobsFound(r => Same(r));

        var pass = await Runner().RunOnceAsync(CancellationToken.None);

        await store.Received(1).SavePendingAsync(Arg.Any<PendingJob>(), Arg.Any<CancellationToken>());
        pass.QuotesRefused.ShouldBe(0);
    }

    [Fact]
    public async Task A_paid_job_with_no_budget_left_and_nothing_to_take_up_is_refused()
    {
        PayUpTo(perJob: 1m, perDay: 1m, perMonth: 1m);
        options.MaxPendingJobs = 1;
        options.Schemas = Definition;
        store.ListSpendAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns([new SpendEntry("k", Now, Root, Definition, new DayRange(Today, Today), 1m)]);
        databento.QuoteAsync(Arg.Any<BatchRequest>(), Arg.Any<CancellationToken>()).Returns(PaidQuote);

        var pass = await Runner().RunOnceAsync(CancellationToken.None);

        await databento.DidNotReceive().SubmitWithinAsync(Arg.Any<BatchRequest>(), Arg.Any<decimal>(), Arg.Any<CancellationToken>());
        pass.QuotesRefused.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task A_job_whose_files_have_gone_is_not_taken_up()
    {
        PayUpTo(perJob: 5m);
        options.MaxPendingJobs = 1;
        PaidJobsFound(r => Same(r, expires: Now));
        databento.SubmitWithinAsync(Arg.Any<BatchRequest>(), PaidQuote, Arg.Any<CancellationToken>()).Returns(OtherJobId);

        await Runner().RunOnceAsync(CancellationToken.None);

        await store.Received(1).SavePendingAsync(Arg.Is<PendingJob>(j => j.Id == OtherJobId), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_job_whose_files_have_yet_to_expire_is_taken_up()
    {
        PayUpTo(perJob: 5m);
        options.MaxPendingJobs = 1;
        PaidJobsFound(r => Same(r, expires: Now.AddDays(1)));

        await Runner().RunOnceAsync(CancellationToken.None);

        await store.Received(1).SavePendingAsync(Arg.Is<PendingJob>(j => j.Id == JobId), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(Errors.Unreachable, IssueLevel.Warning)]
    [InlineData(Errors.ServerError, IssueLevel.Warning)]
    [InlineData(Errors.Malformed, IssueLevel.Error)]
    [InlineData(Errors.Rejected, IssueLevel.Error)]
    public async Task A_listing_that_fails_buys_nothing_and_says_why(string code, IssueLevel level)
    {
        PayUpTo(perJob: 5m);
        options.MaxPendingJobs = 1;
        var planned = await PlannedAsync();
        Spent(LastMonth, [.. planned.Select(r => Earlier(r))]);
        Listing(new Error(code, "no"));

        var pass = await Runner().RunOnceAsync(CancellationToken.None);

        await databento.DidNotReceive().SubmitWithinAsync(Arg.Any<BatchRequest>(), Arg.Any<decimal>(), Arg.Any<CancellationToken>());
        await store.DidNotReceive().SaveSpendAsync(Arg.Any<SpendEntry>(), Arg.Any<CancellationToken>());
        await store.DidNotReceive().SavePendingAsync(Arg.Any<PendingJob>(), Arg.Any<CancellationToken>());
        pass.Issues[0].Code.ShouldBe(code);
        pass.Issues[0].Level.ShouldBe(level);
    }

    [Fact]
    public async Task A_listing_is_asked_for_once_a_pass_even_when_it_fails()
    {
        PayUpTo(perJob: 5m);
        options.MaxPendingJobs = 3;
        var planned = await PlannedAsync();
        Spent(LastMonth, [.. planned.Select(r => Earlier(r))]);
        Listing(new Error(Errors.Unreachable, "no"));

        var pass = await Runner().RunOnceAsync(CancellationToken.None);

        await databento.Received(1).ListJobsAsync(BoughtDay.AddDays(-1), Arg.Any<IReadOnlySet<string>>(), Arg.Any<CancellationToken>());
        pass.Issues.Count.ShouldBeGreaterThan(1);
    }

    [Fact]
    public async Task A_listing_is_asked_for_once_for_each_day_it_starts_from()
    {
        PayUpTo(perJob: 5m);
        var planned = await PlannedAsync();
        var dayBefore = BoughtAt.AddDays(-1);
        Spent(LastMonth, Earlier(planned[0]), Earlier(planned[1], dayBefore));
        Listing(NoJobs);

        await Runner().RunOnceAsync(CancellationToken.None);

        await databento.Received(1).ListJobsAsync(BoughtDay.AddDays(-1), Arg.Any<IReadOnlySet<string>>(), Arg.Any<CancellationToken>());
        await databento.Received(1).ListJobsAsync(BoughtDay.AddDays(-2), Arg.Any<IReadOnlySet<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_listing_that_fails_stops_only_the_job_that_needed_it()
    {
        PayUpTo(perJob: 5m);
        options.MaxPendingJobs = 2;
        var planned = await PlannedAsync();
        Spent(LastMonth, Earlier(planned[0]));
        Listing(new Error(Errors.Unreachable, "no"));
        databento.SubmitWithinAsync(Arg.Any<BatchRequest>(), PaidQuote, Arg.Any<CancellationToken>()).Returns(OtherJobId);

        var pass = await Runner().RunOnceAsync(CancellationToken.None);

        await databento.DidNotReceive().SubmitWithinAsync(Arg.Is<BatchRequest>(b => b.Range == planned[0]), Arg.Any<decimal>(), Arg.Any<CancellationToken>());
        await databento.Received(1).SubmitWithinAsync(Arg.Is<BatchRequest>(b => b.Range == planned[1]), PaidQuote, Arg.Any<CancellationToken>());
        pass.Issues.ShouldHaveSingleItem().Range.ShouldBe(planned[0]);
        pass.JobsSubmitted.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task The_listing_starts_from_the_earliest_entry_for_the_job_and_leaves_out_the_job_ledger()
    {
        var job = Pending(new DayRange(Day(10, 1), Day(10, 1)));
        store.ListPendingAsync(Arg.Any<CancellationToken>()).Returns([job]);
        databento.GetStateAsync(JobId, job.SubmittedAt, Arg.Any<CancellationToken>()).Returns(JobState.Working);
        PayUpTo(perJob: 5m);
        options.MaxPendingJobs = 2;
        var planned = await PlannedAsync();
        Spent(LastMonth, Earlier(planned[0]), Earlier(planned[0], BoughtAt.AddDays(-2)), Earlier(planned[1], BoughtAt.AddDays(-9)));
        Listing(NoJobs);

        await Runner().RunOnceAsync(CancellationToken.None);

        await databento.Received(1).ListJobsAsync(BoughtDay.AddDays(-3),
            Arg.Is<IReadOnlySet<string>>(k => k.Contains(JobId)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task With_nothing_in_the_spend_ledger_for_a_job_no_listing_is_asked_for()
    {
        PayUpTo(perJob: 5m);
        options.MaxPendingJobs = 1;
        var planned = await PlannedAsync();
        databento.SubmitWithinAsync(Arg.Any<BatchRequest>(), PaidQuote, Arg.Any<CancellationToken>()).Returns(JobId);

        var pass = await Runner().RunOnceAsync(CancellationToken.None);

        await NeverListed();
        pass.JobsSubmitted.ShouldBe(1);
        planned.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task An_entry_for_another_root_schema_or_range_needs_no_listing()
    {
        PayUpTo(perJob: 5m);
        options.MaxPendingJobs = 1;
        var first = (await PlannedAsync())[0];
        var otherRange = new DayRange(first.From, first.From);
        Spent(LastMonth, Earlier(first, root: OtherRoot), Earlier(first, schema: Definition), Earlier(otherRange));
        databento.SubmitWithinAsync(Arg.Any<BatchRequest>(), PaidQuote, Arg.Any<CancellationToken>()).Returns(JobId);

        await Runner().RunOnceAsync(CancellationToken.None);

        await NeverListed();
    }

    [Fact]
    public async Task An_entry_whose_job_is_in_the_job_ledger_needs_no_listing()
    {
        var job = Pending(new DayRange(Day(9, 1), Day(9, 1)));
        store.ListPendingAsync(Arg.Any<CancellationToken>()).Returns([job]);
        databento.GetStateAsync(JobId, job.SubmittedAt, Arg.Any<CancellationToken>()).Returns(JobState.Working);
        PayUpTo(perJob: 5m);
        options.MaxPendingJobs = 2;
        var first = (await PlannedAsync())[0];
        Spent(LastMonth, Earlier(first, job: JobId));
        databento.SubmitWithinAsync(Arg.Any<BatchRequest>(), PaidQuote, Arg.Any<CancellationToken>()).Returns(OtherJobId);

        await Runner().RunOnceAsync(CancellationToken.None);

        await NeverListed();
    }

    [Fact]
    public async Task An_entry_whose_job_has_left_the_job_ledger_is_listed_for()
    {
        PayUpTo(perJob: 5m);
        options.MaxPendingJobs = 1;
        var first = (await PlannedAsync())[0];
        Spent(LastMonth, Earlier(first, job: JobId));
        Listing(NoJobs);

        await Runner().RunOnceAsync(CancellationToken.None);

        await databento.Received(1).ListJobsAsync(BoughtDay.AddDays(-1), Arg.Any<IReadOnlySet<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_entry_from_this_month_is_found_too()
    {
        PayUpTo(perJob: 5m);
        options.MaxPendingJobs = 1;
        var first = (await PlannedAsync())[0];
        Spent(Today, Earlier(first, Now.AddHours(-1)));
        Listing(NoJobs);

        await Runner().RunOnceAsync(CancellationToken.None);

        await databento.Received(1).ListJobsAsync(Today.AddDays(-1), Arg.Any<IReadOnlySet<string>>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(60, 1)]
    [InlineData(61, 0)]
    public async Task The_spend_ledger_is_read_sixty_days_back_and_no_further(int daysAgo, int listings)
    {
        PayUpTo(perJob: 5m);
        options.MaxPendingJobs = 1;
        var first = (await PlannedAsync())[0];
        Spent(MonthBefore, Earlier(first, Now.AddDays(-daysAgo)));
        Listing(NoJobs);

        await Runner().RunOnceAsync(CancellationToken.None);

        await databento.Received(listings).ListJobsAsync(Arg.Any<DateOnly>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Each_month_of_the_spend_ledger_is_read_once_a_pass()
    {
        PayUpTo(perJob: 5m);
        options.MaxPendingJobs = 3;
        await PlannedAsync();
        databento.SubmitWithinAsync(Arg.Any<BatchRequest>(), PaidQuote, Arg.Any<CancellationToken>()).Returns(new Error(Errors.Rejected, "no"));

        await Runner().RunOnceAsync(CancellationToken.None);

        await store.Received(1).ListSpendAsync(Today, Arg.Any<CancellationToken>());
        await store.Received(1).ListSpendAsync(LastMonth, Arg.Any<CancellationToken>());
        await store.Received(1).ListSpendAsync(MonthBefore, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task No_listing_is_asked_for_without_a_paid_policy()
    {
        options.MaxPendingJobs = 1;
        databento.SubmitWithinAsync(Arg.Any<BatchRequest>(), 0m, Arg.Any<CancellationToken>()).Returns(JobId);

        await Runner().RunOnceAsync(CancellationToken.None);

        await databento.DidNotReceive().ListJobsAsync(Arg.Any<DateOnly>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task No_listing_is_asked_for_a_job_quoted_at_zero_under_a_paid_policy()
    {
        PayUpTo(perJob: 5m);
        options.MaxPendingJobs = 1;
        databento.QuoteAsync(Arg.Any<BatchRequest>(), Arg.Any<CancellationToken>()).Returns(0m);
        databento.SubmitWithinAsync(Arg.Any<BatchRequest>(), 0m, Arg.Any<CancellationToken>()).Returns(JobId);

        await Runner().RunOnceAsync(CancellationToken.None);

        await databento.DidNotReceive().ListJobsAsync(Arg.Any<DateOnly>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_server_error_on_a_paid_submit_stays_counted_and_is_only_a_warning()
    {
        PayUpTo(perJob: PaidQuote, perDay: PaidQuote);
        options.MaxPendingJobs = 2;
        databento.QuoteAsync(Arg.Any<BatchRequest>(), Arg.Any<CancellationToken>()).Returns(PaidQuote);
        databento.SubmitWithinAsync(Arg.Any<BatchRequest>(), PaidQuote, Arg.Any<CancellationToken>())
            .Returns(new Error(Errors.ServerError, "503"));

        var pass = await Runner().RunOnceAsync(CancellationToken.None);

        await store.DidNotReceive().DeleteSpendAsync(Arg.Any<SpendEntry>(), Arg.Any<CancellationToken>());
        await databento.Received(1).SubmitWithinAsync(Arg.Any<BatchRequest>(), PaidQuote, Arg.Any<CancellationToken>());
        pass.Issues[0].Level.ShouldBe(IssueLevel.Warning);
    }

    [Fact]
    public async Task A_paid_job_is_in_the_ledger_before_it_is_submitted_at_its_quote()
    {
        PayUpTo(perJob: 5m);
        options.MaxPendingJobs = 1;
        databento.QuoteAsync(Arg.Any<BatchRequest>(), Arg.Any<CancellationToken>()).Returns(PaidQuote);
        databento.SubmitWithinAsync(Arg.Any<BatchRequest>(), PaidQuote, Arg.Any<CancellationToken>())
            .Returns(JobId);

        var pass = await Runner().RunOnceAsync(CancellationToken.None);

        pass.JobsSubmitted.ShouldBe(1);
        pass.Spent.ShouldBe(PaidQuote);
        Received.InOrder(() =>
        {
            store.SaveSpendAsync(Arg.Is<SpendEntry>(e => e.Cost == PaidQuote && e.JobId == null), Arg.Any<CancellationToken>());
            databento.SubmitWithinAsync(Arg.Any<BatchRequest>(), PaidQuote, Arg.Any<CancellationToken>());
            store.SaveSpendAsync(Arg.Is<SpendEntry>(e => e.Cost == PaidQuote && e.JobId == JobId), Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task A_quote_above_the_allowance_is_neither_recorded_nor_submitted()
    {
        PayUpTo(perJob: 1m);
        options.Schemas = Definition;
        databento.QuoteAsync(Arg.Any<BatchRequest>(), Arg.Any<CancellationToken>()).Returns(PaidQuote);

        var pass = await Runner().RunOnceAsync(CancellationToken.None);

        pass.JobsSubmitted.ShouldBe(0);
        pass.Issues.ShouldContain(i => i.Code == Errors.OverLimit);
        await store.DidNotReceive().SaveSpendAsync(Arg.Any<SpendEntry>(), Arg.Any<CancellationToken>());
        await databento.DidNotReceive().SubmitWithinAsync(Arg.Any<BatchRequest>(), Arg.Any<decimal>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Spending_already_in_the_ledger_counts_against_the_month()
    {
        PayUpTo(perJob: 5m, perMonth: 10m);
        options.MaxPendingJobs = 1;
        store.ListSpendAsync(Today, Arg.Any<CancellationToken>())
            .Returns([new SpendEntry("earlier", Now.AddDays(-5), Root, Mbo, new DayRange(Day(9, 1), Day(9, 5)), 10m)]);
        databento.SubmitWithinAsync(Arg.Any<BatchRequest>(), 0m, Arg.Any<CancellationToken>()).Returns(JobId);

        await Runner().RunOnceAsync(CancellationToken.None);

        await databento.DidNotReceive().SubmitWithinAsync(Arg.Any<BatchRequest>(),
            Arg.Is<decimal>(d => d != 0m), Arg.Any<CancellationToken>());
        await databento.Received(1).SubmitWithinAsync(Arg.Any<BatchRequest>(), 0m, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_definite_refusal_withdraws_the_ledger_entry()
    {
        PayUpTo(perJob: 5m);
        options.MaxPendingJobs = 1;
        databento.QuoteAsync(Arg.Any<BatchRequest>(), Arg.Any<CancellationToken>()).Returns(PaidQuote);
        databento.SubmitWithinAsync(Arg.Any<BatchRequest>(), PaidQuote, Arg.Any<CancellationToken>())
            .Returns(new Error(Errors.Rejected, "400 Bad Request"));

        await Runner().RunOnceAsync(CancellationToken.None);

        await store.Received().DeleteSpendAsync(Arg.Is<SpendEntry>(e => e.Cost == PaidQuote), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unclear_failure_leaves_the_ledger_entry_counted()
    {
        PayUpTo(perJob: 5m);
        options.MaxPendingJobs = 1;
        databento.QuoteAsync(Arg.Any<BatchRequest>(), Arg.Any<CancellationToken>()).Returns(PaidQuote);
        databento.SubmitWithinAsync(Arg.Any<BatchRequest>(), PaidQuote, Arg.Any<CancellationToken>())
            .Returns(new Error(Errors.Unreachable, "timed out"));

        await Runner().RunOnceAsync(CancellationToken.None);

        await store.DidNotReceive().DeleteSpendAsync(Arg.Any<SpendEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Days_before_the_plan_wait_behind_included_days_and_stop_at_the_first_refusal()
    {
        options.ReachBackTo = Day(6, 1);
        var asked = new List<DayRange>();

        databento.SubmitWithinAsync(Arg.Do<BatchRequest>(r => asked.Add(r.Range)), Arg.Any<decimal>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<BatchRequest>().Range.From < IncludedFrom ? NotFree() : (Result<string>)JobId);

        var pass = await Runner().RunOnceAsync(CancellationToken.None);

        asked.TakeWhile(r => r.From >= IncludedFrom).Count().ShouldBe(pass.JobsSubmitted);
        asked.Count(r => r.From < IncludedFrom).ShouldBe(1);
        pass.JobsOwed.ShouldBe(0);
        pass.Issues.ShouldHaveSingleItem().Code.ShouldBe(Errors.OverLimit);
    }

    [Fact]
    public async Task Book_jobs_ask_for_the_leading_outrights_by_open_interest_and_the_volume_leader()
    {
        options.MaxPendingJobs = 1;
        BatchRequest? asked = null;

        databento.SubmitWithinAsync(Arg.Do<BatchRequest>(r => asked = r), Arg.Any<decimal>(), Arg.Any<CancellationToken>())
            .Returns(JobId);

        await Runner().RunOnceAsync(CancellationToken.None);

        asked.ShouldNotBeNull();
        asked.StypeIn.ShouldBe("continuous");
        asked.Symbols.ShouldBe([$"{Root}.n.0", $"{Root}.n.1", $"{Root}.v.0"]);
    }

    [Fact]
    public async Task Submission_stops_at_the_pending_limit()
    {
        options.MaxPendingJobs = 2;
        databento.SubmitWithinAsync(Arg.Any<BatchRequest>(), Arg.Any<decimal>(), Arg.Any<CancellationToken>())
            .Returns(JobId);

        var pass = await Runner().RunOnceAsync(CancellationToken.None);

        pass.JobsSubmitted.ShouldBe(2);
        pass.JobsOwed.ShouldBeGreaterThan(0);
        pass.Busy.ShouldBeTrue();
    }

    [Fact]
    public async Task A_finished_job_stores_its_files_and_marks_the_days_without_one()
    {
        var job = Pending(new DayRange(Day(10, 30), Day(10, 31)));
        var file = File(Day(10, 30));

        Finished(job, file);
        databento.DownloadAsync(file, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Write(call.ArgAt<string>(1)));

        var pass = await Runner().RunOnceAsync(CancellationToken.None);

        pass.JobsCollected.ShouldBe(1);
        pass.FilesStored.ShouldBe(1);
        await store.Received(1).PutFileAsync(Mbo, Root, Day(10, 30), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await store.Received(1).PutEmptyAsync(Mbo, Root, Day(10, 31), Arg.Any<CancellationToken>());
        await store.Received(1).DeletePendingAsync(JobId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_download_marks_nothing_empty_and_keeps_the_job()
    {
        var job = Pending(new DayRange(Day(10, 30), Day(10, 31)));
        var file = File(Day(10, 30));

        Finished(job, file);
        databento.DownloadAsync(file, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new Error(Errors.Corrupt, file.Name));

        var pass = await Runner().RunOnceAsync(CancellationToken.None);

        pass.JobsCollected.ShouldBe(0);
        await store.DidNotReceive().PutEmptyAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
        await store.DidNotReceive().DeletePendingAsync(JobId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_corrupt_download_is_recorded_as_an_error_against_its_job()
    {
        var job = Pending(new DayRange(Day(10, 30), Day(10, 30)));
        var file = File(Day(10, 30));

        Finished(job, file);
        databento.DownloadAsync(file, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new Error(Errors.Corrupt, file.Name));

        var pass = await Runner().RunOnceAsync(CancellationToken.None);

        var issue = pass.Issues.ShouldHaveSingleItem();
        issue.Level.ShouldBe(IssueLevel.Error);
        issue.Code.ShouldBe(Errors.Corrupt);
        issue.JobId.ShouldBe(JobId);
        issue.Range.ShouldBe(job.Range);
        pass.Errors.ShouldBe(1);
        pass.Eventful.ShouldBeTrue();
    }

    [Fact]
    public async Task A_refused_quote_is_recorded_as_a_warning_against_its_series()
    {
        options.Schemas = Definition;
        options.MaxPendingJobs = 1;
        databento.SubmitWithinAsync(Arg.Any<BatchRequest>(), Arg.Any<decimal>(), Arg.Any<CancellationToken>())
            .Returns(NotFree());

        var pass = await Runner().RunOnceAsync(CancellationToken.None);

        pass.Issues.ShouldNotBeEmpty();
        pass.Issues.ShouldAllBe(i => i.Level == IssueLevel.Warning && i.Code == Errors.OverLimit
            && i.Schema == Definition && i.Root == Root);
        pass.Errors.ShouldBe(0);
    }

    [Fact]
    public async Task Days_before_a_root_was_listed_are_marked_as_having_no_data()
    {
        options.Schemas = Definition;
        options.HistoryStart = Day(10, 1);
        databento.SubmitWithinAsync(Arg.Any<BatchRequest>(), Arg.Any<decimal>(), Arg.Any<CancellationToken>())
            .Returns(NoSymbols());

        var pass = await Runner().RunOnceAsync(CancellationToken.None);

        var october = new DayRange(Day(10, 1), Day(10, 31));
        foreach (var day in october.Each())
            await store.Received(1).PutEmptyAsync(Definition, Root, day, Arg.Any<CancellationToken>());
        pass.EmptyDays.ShouldBe(october.Days);
        pass.JobsSubmitted.ShouldBe(0);
        await store.DidNotReceive().SavePendingAsync(Arg.Any<PendingJob>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Days_with_no_symbols_are_one_warning_and_no_longer_owed()
    {
        options.Schemas = Definition;
        options.HistoryStart = Day(10, 1);
        databento.SubmitWithinAsync(Arg.Any<BatchRequest>(), Arg.Any<decimal>(), Arg.Any<CancellationToken>())
            .Returns(NoSymbols());

        var pass = await Runner().RunOnceAsync(CancellationToken.None);

        var issue = pass.Issues.ShouldHaveSingleItem();
        issue.Level.ShouldBe(IssueLevel.Warning);
        issue.Code.ShouldBe(Errors.NoSymbols);
        pass.Errors.ShouldBe(0);
        pass.JobsOwed.ShouldBe(0);
        pass.Busy.ShouldBeFalse();
    }

    [Fact]
    public async Task Days_with_no_records_are_marked_empty_and_no_longer_owed()
    {
        options.Schemas = Definition;
        options.HistoryStart = Day(10, 1);
        databento.SubmitWithinAsync(Arg.Any<BatchRequest>(), Arg.Any<decimal>(), Arg.Any<CancellationToken>())
            .Returns(NoData());

        var pass = await Runner().RunOnceAsync(CancellationToken.None);

        var october = new DayRange(Day(10, 1), Day(10, 31));
        foreach (var day in october.Each())
            await store.Received(1).PutEmptyAsync(Definition, Root, day, Arg.Any<CancellationToken>());
        pass.Issues.ShouldHaveSingleItem().Code.ShouldBe(Errors.NoData);
        pass.Errors.ShouldBe(0);
        pass.Busy.ShouldBeFalse();
    }

    [Fact]
    public async Task A_no_data_answer_for_several_days_is_asked_again_one_day_at_a_time()
    {
        options.Schemas = Definition;
        options.HistoryStart = Day(10, 1);
        var traded = Day(10, 15);
        databento.SubmitWithinAsync(Arg.Any<BatchRequest>(), Arg.Any<decimal>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<BatchRequest>().Range == new DayRange(traded, traded) ? (Result<string>)JobId : NoData());

        var pass = await Runner().RunOnceAsync(CancellationToken.None);

        var october = new DayRange(Day(10, 1), Day(10, 31));
        foreach (var day in october.Each().Where(d => d != traded))
            await store.Received(1).PutEmptyAsync(Definition, Root, day, Arg.Any<CancellationToken>());
        await store.DidNotReceive().PutEmptyAsync(Definition, Root, traded, Arg.Any<CancellationToken>());
        await store.Received(1).SavePendingAsync(Arg.Is<PendingJob>(j => j.Id == JobId && j.Range == new DayRange(traded, traded)),
            Arg.Any<CancellationToken>());
        pass.EmptyDays.ShouldBe(october.Days - 1);
        pass.JobsSubmitted.ShouldBe(1);
        var issue = pass.Issues.ShouldHaveSingleItem();
        issue.Code.ShouldBe(Errors.NoData);
        issue.Level.ShouldBe(IssueLevel.Warning);
    }

    [Fact]
    public async Task Days_not_asked_about_alone_are_not_marked_empty()
    {
        options.Schemas = Definition;
        options.HistoryStart = Day(10, 1);
        options.MaxPendingJobs = 1;
        databento.SubmitWithinAsync(Arg.Any<BatchRequest>(), Arg.Any<decimal>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<BatchRequest>().Range == new DayRange(Day(10, 1), Day(10, 1)) ? (Result<string>)JobId : NoData());

        var pass = await Runner().RunOnceAsync(CancellationToken.None);

        await store.DidNotReceive().PutEmptyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly>(),
            Arg.Any<CancellationToken>());
        pass.EmptyDays.ShouldBe(0);
        pass.JobsSubmitted.ShouldBe(1);
    }

    [Fact]
    public async Task A_quiet_pass_is_not_eventful()
    {
        options.HistoryStart = Today;

        var pass = await Runner().RunOnceAsync(CancellationToken.None);

        pass.Eventful.ShouldBeFalse();
    }

    [Fact]
    public async Task A_day_already_held_is_not_downloaded_again()
    {
        var job = Pending(new DayRange(Day(10, 30), Day(10, 30)));
        var file = File(Day(10, 30));

        Finished(job, file);
        store.ListHeldAsync(Mbo, Root, Arg.Any<CancellationToken>())
            .Returns(new HashSet<DateOnly> { Day(10, 30) });

        await Runner().RunOnceAsync(CancellationToken.None);

        await databento.DidNotReceive().DownloadAsync(Arg.Any<RemoteFile>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        await store.Received(1).DeletePendingAsync(JobId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_gone_job_leaves_the_ledger_so_its_days_are_planned_again()
    {
        var job = Pending(new DayRange(Day(10, 30), Day(10, 31)));

        store.ListPendingAsync(Arg.Any<CancellationToken>()).Returns([job]);
        databento.GetStateAsync(JobId, job.SubmittedAt, Arg.Any<CancellationToken>())
            .Returns(JobState.Gone);

        await Runner().RunOnceAsync(CancellationToken.None);

        await store.Received(1).DeletePendingAsync(JobId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Pending_days_are_not_asked_for_twice()
    {
        options.MaxPendingJobs = 100;
        var job = Pending(new DayRange(Day(10, 1), Day(10, 31)));
        var asked = new List<BatchRequest>();

        store.ListPendingAsync(Arg.Any<CancellationToken>()).Returns([job]);
        databento.GetStateAsync(JobId, job.SubmittedAt, Arg.Any<CancellationToken>())
            .Returns(JobState.Working);
        databento.SubmitWithinAsync(Arg.Do<BatchRequest>(asked.Add), Arg.Any<decimal>(), Arg.Any<CancellationToken>())
            .Returns(OtherJobId);

        await Runner().RunOnceAsync(CancellationToken.None);

        asked.ShouldAllBe(r => r.Range.From > Day(10, 31));
    }

    [Fact]
    public async Task A_schema_Databento_reports_no_range_for_is_skipped_and_said_once()
    {
        options.Schemas = "statistics";
        options.Roots = $"{Root},ES";

        var pass = await Runner().RunOnceAsync(CancellationToken.None);

        await databento.DidNotReceive().SubmitWithinAsync(Arg.Any<BatchRequest>(), Arg.Any<decimal>(), Arg.Any<CancellationToken>());

        var issue = pass.Issues.ShouldHaveSingleItem();
        issue.Code.ShouldBe(Errors.Malformed);
        issue.Level.ShouldBe(IssueLevel.Error);
        issue.Message.ShouldContain("statistics");
    }

    [Fact]
    public async Task A_window_that_opens_after_the_data_ends_asks_for_nothing()
    {
        options.HistoryStart = Today;

        var pass = await Runner().RunOnceAsync(CancellationToken.None);

        await databento.DidNotReceive().SubmitWithinAsync(Arg.Any<BatchRequest>(), Arg.Any<decimal>(), Arg.Any<CancellationToken>());
        pass.JobsOwed.ShouldBe(0);
    }

    [Fact]
    public async Task With_the_dataset_range_unavailable_nothing_new_is_asked_for_and_the_pass_says_why()
    {
        databento.GetAvailableEndsAsync(Dataset, Arg.Any<CancellationToken>())
            .Returns(new Error(Errors.Rejected, "401 Unauthorized"));

        var pass = await Runner().RunOnceAsync(CancellationToken.None);

        var issue = pass.Issues.ShouldHaveSingleItem();
        issue.Level.ShouldBe(IssueLevel.Error);
        issue.Code.ShouldBe(Errors.Rejected);
        pass.Failure.ShouldNotBeNull();
        await databento.DidNotReceive().SubmitWithinAsync(Arg.Any<BatchRequest>(), Arg.Any<decimal>(), Arg.Any<CancellationToken>());
        await store.DidNotReceive().ListPendingAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(Errors.Malformed)]
    [InlineData(Errors.ServerError)]
    public async Task With_only_the_dataset_range_unreadable_a_finished_job_is_still_collected(string code)
    {
        var job = Pending(new DayRange(Day(10, 30), Day(10, 30)));

        Finished(job, File(Day(10, 30)));
        databento.DownloadAsync(Arg.Any<RemoteFile>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Write(call.ArgAt<string>(1)));
        databento.GetAvailableEndsAsync(Dataset, Arg.Any<CancellationToken>())
            .Returns(new Error(code, "get_dataset_range has no per-schema ranges."));

        var pass = await Runner().RunOnceAsync(CancellationToken.None);

        pass.JobsCollected.ShouldBe(1);
        pass.Failure.ShouldNotBeNull();
        await store.Received(1).DeletePendingAsync(job.Id, Arg.Any<CancellationToken>());
        await databento.DidNotReceive().SubmitWithinAsync(Arg.Any<BatchRequest>(), Arg.Any<decimal>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_single_day_book_job_that_stays_above_the_policy_sheds_to_nothing_and_stays_owed()
    {
        options.HistoryStart = Day(10, 31);
        databento.SubmitWithinAsync(Arg.Any<BatchRequest>(), Arg.Any<decimal>(), Arg.Any<CancellationToken>())
            .Returns(NotFree());

        var pass = await Runner().RunOnceAsync(CancellationToken.None);

        pass.QuotesRefused.ShouldBe(1);
        pass.JobsSubmitted.ShouldBe(0);
        pass.JobsOwed.ShouldBe(1);
    }

    [Fact]
    public async Task A_job_whose_state_is_unavailable_stays_pending_with_a_warning()
    {
        options.HistoryStart = Today;
        var job = Pending(new DayRange(Day(10, 30), Day(10, 30)));
        store.ListPendingAsync(Arg.Any<CancellationToken>()).Returns([job]);
        databento.GetStateAsync(JobId, job.SubmittedAt, Arg.Any<CancellationToken>())
            .Returns(new Error(Errors.Unreachable, "timed out"));

        var pass = await Runner().RunOnceAsync(CancellationToken.None);

        pass.JobsPending.ShouldBe(1);
        var issue = pass.Issues.ShouldHaveSingleItem();
        issue.Level.ShouldBe(IssueLevel.Warning);
        issue.JobId.ShouldBe(JobId);
        await store.DidNotReceive().DeletePendingAsync(JobId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_finished_job_that_lists_nothing_marks_no_day_empty_and_stays_pending()
    {
        var job = Pending(new DayRange(Day(10, 30), Day(10, 30)));
        store.ListPendingAsync(Arg.Any<CancellationToken>()).Returns([job]);
        databento.GetStateAsync(JobId, job.SubmittedAt, Arg.Any<CancellationToken>()).Returns(JobState.Done);
        databento.ListFilesAsync(JobId, Arg.Any<CancellationToken>())
            .Returns((Result<IReadOnlyList<RemoteFile>>)new List<RemoteFile>());

        var summary = await Runner().RunOnceAsync(CancellationToken.None);

        summary.Issues.ShouldContain(i => i.Code == IssueCodes.NoFiles);
        await store.DidNotReceive().PutEmptyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
        await store.DidNotReceive().DeletePendingAsync(JobId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_job_whose_files_are_unavailable_stays_pending_until_it_has_failed_for_a_week()
    {
        var job = Pending(new DayRange(Day(10, 30), Day(10, 30)));
        KeepPendingAsSaved(job);
        databento.GetStateAsync(JobId, job.SubmittedAt, Arg.Any<CancellationToken>()).Returns(JobState.Done);
        databento.ListFilesAsync(JobId, Arg.Any<CancellationToken>()).Returns(new Error(Errors.Unreachable, "timed out"));

        await Runner().RunOnceAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromDays(6));
        await Runner().RunOnceAsync(CancellationToken.None);
        var kept = store.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IArchiveStore.DeletePendingAsync));
        clock.Advance(TimeSpan.FromDays(2));
        var abandoned = await Runner().RunOnceAsync(CancellationToken.None);

        kept.ShouldBe(0);
        abandoned.JobsCollected.ShouldBe(1);
        abandoned.Issues.ShouldContain(i => i.Code == IssueCodes.JobAbandoned && i.Level == IssueLevel.Error);
        await store.Received(1).DeletePendingAsync(JobId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_job_left_waiting_while_the_worker_was_down_is_not_given_up_at_its_first_failure()
    {
        var job = Pending(new DayRange(Day(10, 30), Day(10, 30))) with { SubmittedAt = Now.AddDays(-10) };
        KeepPendingAsSaved(job);
        databento.GetStateAsync(JobId, job.SubmittedAt, Arg.Any<CancellationToken>()).Returns(JobState.Done);
        databento.ListFilesAsync(JobId, Arg.Any<CancellationToken>()).Returns(new Error(Errors.Unreachable, "timed out"));

        var summary = await Runner().RunOnceAsync(CancellationToken.None);

        summary.Issues.ShouldNotContain(i => i.Code == IssueCodes.JobAbandoned);
        await store.DidNotReceive().DeletePendingAsync(JobId, Arg.Any<CancellationToken>());
        await store.Received(1).SavePendingAsync(Arg.Is<PendingJob>(j => j.Id == JobId && j.FailingSince == Now),
            Arg.Any<CancellationToken>());
    }

    /// <summary>The store lists a pending job as it was last saved, as the real one does.</summary>
    private void KeepPendingAsSaved(PendingJob job)
    {
        var current = job;
        store.ListPendingAsync(Arg.Any<CancellationToken>()).Returns(_ => [current]);
        store.SavePendingAsync(Arg.Any<PendingJob>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(call =>
            {
                if (call.Arg<PendingJob>().Id == job.Id)
                    current = call.Arg<PendingJob>();
            });
    }

    [Theory]
    [InlineData("glbx-mdp3-20261020.mbo.dbn.zst")]
    [InlineData("glbx-mdp3-20261030-20261031.mbo.dbn.zst")]
    [InlineData("not-a-day.mbo.dbn.zst")]
    [InlineData("../glbx-mdp3-20261030.mbo.dbn.zst")]
    [InlineData("sub/glbx-mdp3-20261030.mbo.dbn.zst")]
    public async Task A_file_that_is_not_a_day_of_the_job_stores_nothing(string name)
    {
        var job = Pending(new DayRange(Day(10, 30), Day(10, 31)));

        Finished(job, new RemoteFile(name, 3, "", name), File(Day(10, 30)));

        var pass = await Runner().RunOnceAsync(CancellationToken.None);

        var issue = pass.Issues.ShouldHaveSingleItem();
        issue.Code.ShouldBe(IssueCodes.UnexpectedFile);
        issue.Level.ShouldBe(IssueLevel.Error);
        await databento.DidNotReceive().DownloadAsync(Arg.Any<RemoteFile>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await store.DidNotReceive().PutEmptyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_download_that_fails_for_a_network_reason_is_only_a_warning()
    {
        var job = Pending(new DayRange(Day(10, 30), Day(10, 30)));
        var file = File(Day(10, 30));

        Finished(job, file);
        databento.DownloadAsync(file, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new Error(Errors.Unreachable, file.Name));

        var pass = await Runner().RunOnceAsync(CancellationToken.None);

        pass.Issues.ShouldHaveSingleItem().Level.ShouldBe(IssueLevel.Warning);
    }

    [Fact]
    public async Task A_store_that_refuses_a_file_ends_the_pass_with_the_scratch_folder_gone()
    {
        var job = Pending(new DayRange(Day(10, 30), Day(10, 30)));
        var file = File(Day(10, 30));

        Finished(job, file);
        databento.DownloadAsync(file, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Write(call.ArgAt<string>(1)));
        store.PutFileAsync(Mbo, Root, Day(10, 30), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("disk full"));

        await Should.ThrowAsync<IOException>(() => Runner().RunOnceAsync(CancellationToken.None));

        Directory.Exists(Path.Combine(scratch, JobId)).ShouldBeFalse();
        await store.DidNotReceive().DeletePendingAsync(JobId, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(Errors.Unreachable, IssueLevel.Warning)]
    [InlineData(Errors.ServerError, IssueLevel.Warning)]
    [InlineData(Errors.Rejected, IssueLevel.Error)]
    [InlineData(Errors.Malformed, IssueLevel.Error)]
    public async Task A_job_not_submitted_is_recorded_against_its_series_and_stays_owed(string code, IssueLevel level)
    {
        options.Schemas = Definition;
        databento.SubmitWithinAsync(Arg.Any<BatchRequest>(), Arg.Any<decimal>(), Arg.Any<CancellationToken>())
            .Returns(new Error(code, "no"));

        var pass = await Runner().RunOnceAsync(CancellationToken.None);

        pass.Issues.ShouldNotBeEmpty();
        pass.Issues.ShouldAllBe(i => i.Code == code && i.Level == level && i.Schema == Definition);
        pass.JobsOwed.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task A_quote_that_fails_under_a_paid_policy_records_nothing()
    {
        PayUpTo(perJob: 5m);
        options.Schemas = Definition;
        databento.QuoteAsync(Arg.Any<BatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new Error(Errors.Unreachable, "timed out"));

        var pass = await Runner().RunOnceAsync(CancellationToken.None);

        pass.Issues.ShouldNotBeEmpty();
        await store.DidNotReceive().SaveSpendAsync(Arg.Any<SpendEntry>(), Arg.Any<CancellationToken>());
        await databento.DidNotReceive().SubmitWithinAsync(Arg.Any<BatchRequest>(), Arg.Any<decimal>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_zero_quote_under_a_paid_policy_goes_at_zero_and_records_nothing()
    {
        PayUpTo(perJob: 5m);
        options.MaxPendingJobs = 1;
        databento.QuoteAsync(Arg.Any<BatchRequest>(), Arg.Any<CancellationToken>()).Returns(0m);
        databento.SubmitWithinAsync(Arg.Any<BatchRequest>(), 0m, Arg.Any<CancellationToken>()).Returns(JobId);

        var pass = await Runner().RunOnceAsync(CancellationToken.None);

        pass.JobsSubmitted.ShouldBe(1);
        pass.Spent.ShouldBe(0m);
        await store.DidNotReceive().SaveSpendAsync(Arg.Any<SpendEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unclear_failure_counts_against_the_budget_of_the_same_pass()
    {
        PayUpTo(perJob: PaidQuote, perDay: PaidQuote);
        options.Schemas = Definition;
        options.Roots = $"{Root},{OtherRoot}";
        databento.QuoteAsync(Arg.Any<BatchRequest>(), Arg.Any<CancellationToken>()).Returns(PaidQuote);
        databento.SubmitWithinAsync(Arg.Any<BatchRequest>(), PaidQuote, Arg.Any<CancellationToken>())
            .Returns(new Error(Errors.Unreachable, "timed out"));

        await Runner().RunOnceAsync(CancellationToken.None);

        await databento.Received(1).SubmitWithinAsync(Arg.Any<BatchRequest>(), PaidQuote, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Later_days_older_than_the_plan_wait_once_one_is_refused()
    {
        options.ReachBackTo = Day(6, 1);
        options.MaxPendingJobs = 100;
        databento.SubmitWithinAsync(Arg.Any<BatchRequest>(), Arg.Any<decimal>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<BatchRequest>().Range.From < IncludedFrom ? NotFree() : (Result<string>)JobId);

        var pass = await Runner().RunOnceAsync(CancellationToken.None);

        pass.Issues.ShouldHaveSingleItem().Message.ShouldContain("such jobs wait for a later pass");
        pass.QuotesRefused.ShouldBe(1);
    }

    private const decimal PaidQuote = 2.5m;

    /// <summary>The first day the Standard plan includes mbo, a month before <see cref="Now"/>.</summary>
    private static readonly DateOnly IncludedFrom = Today.AddDays(-GlbxMdp3.Instance.Included.BookDays);

    private void PayUpTo(decimal perJob, decimal perDay = 10m, decimal perMonth = 100m)
    {
        options.Spend = new SpendPolicy
        {
            AllowPaidJobs = true,
            MaxPerJob = perJob,
            MaxPerDay = perDay,
            MaxPerMonth = perMonth
        };
        store.ListSpendAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).Returns([]);
    }

    private SyncRunner Runner() =>
        new(databento, store, options, clock, NullLogger<SyncRunner>.Instance);

    private PendingJob Pending(DayRange range) =>
        new(JobId, Root, Mbo, range, clock.GetUtcNow().AddHours(-1));

    private void Finished(PendingJob job, params RemoteFile[] files)
    {
        store.ListPendingAsync(Arg.Any<CancellationToken>()).Returns([job]);
        databento.GetStateAsync(JobId, job.SubmittedAt, Arg.Any<CancellationToken>())
            .Returns(JobState.Done);
        List<RemoteFile> listed = [new RemoteFile("manifest.json", 1, "", ""), .. files];

        databento.ListFilesAsync(JobId, Arg.Any<CancellationToken>())
            .Returns((Result<IReadOnlyList<RemoteFile>>)listed);
    }

    private static RemoteFile File(DateOnly day) =>
        new(FileName(day, Mbo), 3, "", $"https://api.databento.com/v0/batch/download/{FileName(day, Mbo)}");

    private static Result<long> Write(string path)
    {
        System.IO.File.WriteAllBytes(path, [1, 2, 3]);

        return 3L;
    }

    private static Result<string> NotFree() => new Error(Errors.OverLimit, "quoted $0.01");

    private static Result<string> NoSymbols() =>
        new Error(Errors.NoSymbols, "422 Unprocessable Entity: None of the symbols could be resolved");

    private static Result<string> NoData() =>
        new Error(Errors.NoData, "422 Unprocessable Entity: No data was found for the request you submitted.");

    private static Result<IReadOnlyDictionary<string, DateTimeOffset>> Ends(params string[] schemas) =>
        schemas.ToDictionary(s => s, _ => new DateTimeOffset(2026, 11, 1, 20, 0, 0, TimeSpan.Zero));
}
