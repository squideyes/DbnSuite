using System.Globalization;
using DbnSuite.IntegrationTests.Support;
using DbnSuite.Sync.Core;
using DbnSuite.Sync.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;
using static DbnSuite.IntegrationTests.Support.Sample;

namespace DbnSuite.IntegrationTests;

/// <summary>
/// What a mistake here costs is real money, so these come first: every way a
/// job can be quoted, refused, paid for or lost, seen from the gateway and the bucket.
/// </summary>
[Collection(ArchiveCollection.Name)]
public sealed class SpendPolicyTests(RustFs s3)
{
    private const decimal Quote = 4m;
    private const decimal PerJob = 5m;
    private const decimal PerDay = 5m;
    private const decimal PerMonth = 100m;
    private const string TwoRoots = $"{Es},{Mnq}";
    private const string ThreeRoots = $"{Es},{Mnq},{Nq}";
    private const string Malformed = "databento.malformed";
    private const string OtherKey = "db-some-other-made-up-key";
    private const string Unauthorized = "401";
    private const string Since = "since";

    private static readonly DateOnly FirstIncluded = DateOnly.FromDateTime(Now.UtcDateTime)
        .AddDays(-GlbxMdp3.Instance.Included.BookDays);

    private static readonly TimeSpan OneDay = TimeSpan.FromDays(1);

    private static decimal Dollars(string text) => decimal.Parse(text, CultureInfo.InvariantCulture);

    private static SpendEntry Spent(DateOnly on, decimal cost) => new(Guid.NewGuid().ToString("N"),
        new DateTimeOffset(on.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), Mnq, Mbo, new DayRange(on, on), cost, "EARLIER");

    [Theory]
    [InlineData("0.000001")]
    [InlineData("0.01")]
    [InlineData("1000")]
    public async Task By_default_nothing_quoted_above_zero_is_ever_submitted(string quote)
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.Gateway.Quote = _ => Dollars(quote);

        var pass = await sync.FirstPassAsync();

        sync.Gateway.CallsTo(Endpoints.SubmitJob).ShouldBeEmpty();
        pass.JobsSubmitted.ShouldBe(0);
        pass.QuotesRefused.ShouldBeGreaterThan(0);
        pass.Spent.ShouldBe(0m);
        (await sync.Archive.SpendAsync()).ShouldBeEmpty();
        (await sync.Archive.PendingAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Limits_without_allow_paid_jobs_count_as_zero()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.With(SyncScenario.Spend(nameof(SpendPolicy.MaxPerJob)), "50")
            .With(SyncScenario.Spend(nameof(SpendPolicy.MaxPerDay)), "50")
            .With(SyncScenario.Spend(nameof(SpendPolicy.MaxPerMonth)), "50");
        sync.Gateway.Quote = _ => Quote;

        await sync.FirstPassAsync();

        sync.Gateway.CallsTo(Endpoints.SubmitJob).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_free_job_goes_and_never_touches_the_ledger()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);

        var pass = await sync.FirstPassAsync();

        pass.JobsSubmitted.ShouldBe(1);
        (await sync.Archive.SpendAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_free_job_under_a_paid_policy_is_not_recorded_as_spending()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.Paying(PerJob, PerDay, PerMonth);

        var pass = await sync.FirstPassAsync();

        pass.JobsSubmitted.ShouldBe(1);
        pass.Spent.ShouldBe(0m);
        (await sync.Archive.SpendAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task What_is_quoted_is_exactly_what_is_submitted()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.Keeping(TwoRoots, $"{Mbo},{Ohlcv},{Definition}");

        await sync.FirstPassAsync();

        var quoted = sync.Gateway.CallsTo(Endpoints.GetCost).Select(c => c.Job).ToList();
        var submitted = sync.Gateway.CallsTo(Endpoints.SubmitJob).Select(c => c.Job).ToList();
        submitted.Count.ShouldBe(6);
        quoted.ShouldBe(submitted, ignoreOrder: true);
    }

    [Fact]
    public async Task A_paid_job_is_in_the_ledger_before_the_gateway_hears_of_it()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        IReadOnlyList<SpendEntry> whileSubmitting = [];
        sync.Paying(PerJob, PerDay, PerMonth);
        sync.Gateway.Quote = _ => Quote;
        sync.Gateway.OnSubmit = async _ => whileSubmitting = await sync.Archive.SpendAsync();

        var pass = await sync.FirstPassAsync();

        var reserved = whileSubmitting.ShouldHaveSingleItem();
        reserved.Cost.ShouldBe(Quote);
        reserved.JobId.ShouldBeNull();
        var settled = (await sync.Archive.SpendAsync()).ShouldHaveSingleItem();
        settled.Key.ShouldBe(reserved.Key);
        settled.JobId.ShouldBe(sync.Gateway.Jobs.Single().Id);
        pass.Spent.ShouldBe(Quote);
    }

    [Fact]
    public async Task A_quote_above_the_job_limit_is_neither_recorded_nor_submitted()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.Keeping(Mnq, Definition).Paying(PerJob, PerDay, PerMonth);
        sync.Gateway.Quote = _ => PerJob + 0.01m;

        var pass = await sync.FirstPassAsync();

        sync.Gateway.CallsTo(Endpoints.SubmitJob).ShouldBeEmpty();
        (await sync.Archive.SpendAsync()).ShouldBeEmpty();
        pass.Issues.ShouldHaveSingleItem().Code.ShouldBe(Errors.OverLimit);
    }

    [Fact]
    public async Task A_quote_of_exactly_the_limit_is_allowed()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.Keeping(Mnq, Definition).Paying(PerJob, PerDay, PerMonth);
        sync.Gateway.Quote = _ => PerJob;

        var pass = await sync.FirstPassAsync();

        pass.JobsSubmitted.ShouldBe(1);
        pass.Spent.ShouldBe(PerJob);
    }

    [Fact]
    public async Task One_pass_spends_no_further_than_the_day_limit()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.Keeping(ThreeRoots, Definition).Paying(PerJob, perDay: 2 * Quote + 1m, PerMonth);
        sync.Gateway.Quote = _ => Quote;

        var pass = await sync.FirstPassAsync();

        sync.Gateway.CallsTo(Endpoints.SubmitJob).Select(c => c.Job.Root).ShouldBe([Es, Mnq]);
        pass.Spent.ShouldBe(2 * Quote);
        (await sync.Archive.SpendAsync()).Sum(e => e.Cost).ShouldBe(2 * Quote);
        pass.JobsOwed.ShouldBe(1);
    }

    [Fact]
    public async Task Spending_recorded_before_a_restart_still_counts()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.Keeping(TwoRoots, Definition).MaxPending(1).Paying(PerJob, PerDay, PerMonth);
        sync.Gateway.Quote = _ => Quote;
        sync.Gateway.NewJobState = JobStates.Processing;

        await sync.FirstPassAsync();
        sync.MaxPending(5);
        var restarted = await sync.RestartAsync();

        sync.Gateway.CallsTo(Endpoints.SubmitJob).ShouldHaveSingleItem().Job.Root.ShouldBe(Es);
        restarted.QuotesRefused.ShouldBe(1);
        (await sync.Archive.SpendAsync()).ShouldHaveSingleItem().Root.ShouldBe(Es);
    }

    [Theory]
    [InlineData("0.5", true)]
    [InlineData("2", false)]
    public async Task The_month_limit_counts_this_month_of_the_ledger_and_no_other(string quote, bool submitted)
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        await sync.Archive.SeedSpendAsync(Spent(Monday, PerMonth - 1m));
        await sync.Archive.SeedSpendAsync(Spent(Saturday, 10 * PerMonth));
        sync.Keeping(Mnq, Definition).Paying(PerJob, PerDay, PerMonth);
        sync.Gateway.Quote = _ => Dollars(quote);

        var pass = await sync.FirstPassAsync();

        pass.JobsSubmitted.ShouldBe(submitted ? 1 : 0);
    }

    [Fact]
    public async Task The_day_limit_opens_again_at_midnight_utc()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.Keeping(TwoRoots, Definition).Paying(PerJob, PerDay, PerMonth);
        sync.Gateway.Quote = _ => Quote;

        var today = await sync.FirstPassAsync();
        var tomorrow = await sync.PassAfterAsync(OneDay);

        today.Failure.ShouldBeNull(sync.Explain(today));
        today.JobsSubmitted.ShouldBe(1, sync.Explain(today));
        tomorrow.Failure.ShouldBeNull(sync.Explain(tomorrow));
        tomorrow.JobsSubmitted.ShouldBe(1, sync.Explain(tomorrow));
        (await sync.Archive.SpendAsync()).Select(e => e.Root).ShouldBe([Es, Mnq], ignoreOrder: true);
    }

    [Fact]
    public async Task With_the_budget_gone_free_jobs_still_go_and_paid_ones_do_not()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        await sync.Archive.SeedSpendAsync(Spent(Monday, PerMonth));
        sync.Keeping(Mnq, $"{Mbo},{Definition}").Paying(PerJob, PerDay, PerMonth);
        sync.Gateway.Quote = job => job.Schema == Mbo ? Quote : 0m;

        await sync.FirstPassAsync();

        sync.Gateway.CallsTo(Endpoints.SubmitJob).ShouldHaveSingleItem().Job.Schema.ShouldBe(Definition);
    }

    [Theory]
    [InlineData(StatusCodes.Status400BadRequest)]
    [InlineData(StatusCodes.Status403Forbidden)]
    [InlineData(StatusCodes.Status429TooManyRequests)]
    public async Task A_definite_refusal_withdraws_the_ledger_entry(int status)
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        var recorded = 0;
        sync.Keeping(Mnq, Definition).Paying(PerJob, PerDay, PerMonth);
        sync.Gateway.Quote = _ => Quote;
        sync.Gateway.OnSubmit = async _ => recorded = (await sync.Archive.SpendAsync()).Count;
        sync.Gateway.Fail(Endpoints.SubmitJob, Reply.Json(status, """{"detail":"refused"}"""));

        var pass = await sync.FirstPassAsync();

        recorded.ShouldBe(1);
        (await sync.Archive.SpendAsync()).ShouldBeEmpty();
        pass.Spent.ShouldBe(0m);
    }

    [Fact]
    public async Task A_refusal_that_says_there_was_nothing_to_buy_withdraws_the_entry_too()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.Keeping(Mnq, Definition).Paying(PerJob, PerDay, PerMonth);
        sync.Gateway.Quote = _ => Quote;
        sync.Gateway.Fail(Endpoints.SubmitJob, Reply.NoData);

        var pass = await sync.FirstPassAsync();

        (await sync.Archive.SpendAsync()).ShouldBeEmpty();
        pass.EmptyDays.ShouldBe(5);
    }

    [Theory]
    [InlineData(StatusCodes.Status500InternalServerError)]
    [InlineData(StatusCodes.Status502BadGateway)]
    [InlineData(StatusCodes.Status503ServiceUnavailable)]
    [InlineData(StatusCodes.Status504GatewayTimeout)]
    public async Task A_server_error_may_have_taken_the_job_so_it_stays_counted(int status)
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.Keeping(TwoRoots, Definition).Paying(PerJob, PerDay, PerMonth);
        sync.Gateway.Quote = _ => Quote;
        sync.Gateway.Fail(Endpoints.SubmitJob, Reply.Json(status, """{"detail":"upstream timed out"}"""));

        var pass = await sync.FirstPassAsync();

        (await sync.Archive.SpendAsync()).ShouldHaveSingleItem().JobId.ShouldBeNull();
        sync.Gateway.CallsTo(Endpoints.SubmitJob).ShouldHaveSingleItem();
        pass.JobsSubmitted.ShouldBe(0);
    }

    [Fact]
    public async Task A_connection_cut_during_submission_stays_counted_across_a_restart()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.Keeping(TwoRoots, Definition).Paying(PerJob, PerDay, PerMonth);
        sync.Gateway.Quote = _ => Quote;
        sync.Gateway.Fail(Endpoints.SubmitJob, Reply.Dropped);

        var cut = await sync.FirstPassAsync();
        sync.Gateway.Heal(Endpoints.SubmitJob);
        var restarted = await sync.RestartAsync();

        cut.Issues.ShouldContain(i => i.Code == Errors.Unreachable);
        (await sync.Archive.SpendAsync()).ShouldHaveSingleItem().JobId.ShouldBeNull();
        restarted.JobsSubmitted.ShouldBe(0);
        restarted.QuotesRefused.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task An_acceptance_without_a_job_id_stays_counted()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.Keeping(Mnq, Definition).Paying(PerJob, PerDay, PerMonth);
        sync.Gateway.Quote = _ => Quote;
        sync.Gateway.Fail(Endpoints.SubmitJob, Reply.Ok("""{"state":"queued"}"""));

        var pass = await sync.FirstPassAsync();

        (await sync.Archive.SpendAsync()).ShouldHaveSingleItem().Cost.ShouldBe(Quote);
        pass.Issues.ShouldHaveSingleItem().Code.ShouldBe(Malformed);
    }

    [Fact]
    public async Task A_quote_that_rises_before_submission_is_refused_and_withdrawn()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        var quotes = 0;
        sync.Keeping(Mnq, Definition).Paying(PerJob, PerDay, PerMonth);
        sync.Gateway.Quote = _ => Interlocked.Increment(ref quotes) == 1 ? Quote : Quote + 0.5m;

        var pass = await sync.FirstPassAsync();

        sync.Gateway.CallsTo(Endpoints.SubmitJob).ShouldBeEmpty();
        (await sync.Archive.SpendAsync()).ShouldBeEmpty();
        pass.Spent.ShouldBe(0m);
    }

    [Fact]
    public async Task A_job_in_flight_is_not_quoted_or_paid_for_again_after_a_restart()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.Paying(PerJob, PerDay, PerMonth);
        sync.Gateway.Quote = _ => Quote;
        sync.Gateway.NewJobState = JobStates.Processing;

        await sync.FirstPassAsync();
        var quotes = sync.Gateway.CallsTo(Endpoints.GetCost).Count;
        await sync.RestartAsync();
        sync.Gateway.SetAllJobs(JobStates.Done);
        var collected = await sync.NextPassAsync();

        collected.JobsCollected.ShouldBe(1);
        sync.Gateway.Jobs.ShouldHaveSingleItem();
        sync.Gateway.CallsTo(Endpoints.GetCost).Count.ShouldBe(quotes);
        (await sync.Archive.SpendAsync()).ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData("\"free\"")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("-1")]
    [InlineData("not json")]
    [InlineData("")]
    public async Task A_quote_that_cannot_be_read_spends_nothing(string body)
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.Keeping(Mnq, Definition).Paying(PerJob, PerDay, PerMonth);
        sync.Gateway.Fail(Endpoints.GetCost, Reply.Ok(body));

        var pass = await sync.FirstPassAsync();

        sync.Gateway.CallsTo(Endpoints.SubmitJob).ShouldBeEmpty();
        (await sync.Archive.SpendAsync()).ShouldBeEmpty();
        pass.Issues.ShouldHaveSingleItem().Code.ShouldBe(Malformed);
    }

    [Fact]
    public async Task A_book_job_at_the_window_edge_sheds_old_days_until_it_is_free()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        var freeFrom = FirstIncluded.AddDays(2);
        sync.From(FirstIncluded).MaxPending(1);
        sync.Gateway.Quote = job => job.From < freeFrom ? Quote : 0m;

        var pass = await sync.FirstPassAsync();

        sync.Gateway.CallsTo(Endpoints.GetCost).Select(c => c.Job.From)
            .ShouldBe([FirstIncluded, FirstIncluded.AddDays(1), freeFrom]);
        sync.Gateway.Jobs.ShouldHaveSingleItem().Spec.From.ShouldBe(freeFrom);
        pass.QuotesRefused.ShouldBe(2);
    }

    [Fact]
    public async Task A_book_job_is_given_up_on_after_three_days_are_shed()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.From(FirstIncluded).MaxPending(1);
        sync.Gateway.Quote = job => job.From < FirstIncluded.AddDays(4) ? Quote : 0m;

        var pass = await sync.FirstPassAsync();

        sync.Gateway.Jobs.Where(j => j.Spec.From < FirstIncluded.AddDays(5)).ShouldBeEmpty();
        pass.Issues.ShouldContain(i => i.Code == Errors.OverLimit && i.Range!.Value.From == FirstIncluded.AddDays(3));
    }

    [Fact]
    public async Task Days_older_than_the_plan_are_asked_about_after_included_days_and_only_until_one_is_refused()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.From(FirstIncluded.AddDays(-20)).ReachingBackTo(FirstIncluded.AddDays(-14));
        sync.Gateway.Quote = job => job.From < FirstIncluded ? Quote : 0m;

        var pass = await sync.FirstPassAsync();

        var asked = sync.Gateway.CallsTo(Endpoints.GetCost).Select(c => c.Job.From).ToList();
        asked.Count(from => from < FirstIncluded).ShouldBe(1);
        asked[^1].ShouldBe(FirstIncluded.AddDays(-4));
        sync.Gateway.Jobs.ShouldAllBe(j => j.Spec.From >= FirstIncluded);
        pass.Issues.ShouldHaveSingleItem().Code.ShouldBe(Errors.OverLimit);
        pass.Busy.ShouldBeTrue();
    }

    [Fact]
    public async Task A_paid_reach_back_buys_the_newest_old_days_first_within_the_budget()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.From(FirstIncluded.AddDays(-20)).ReachingBackTo(FirstIncluded.AddDays(-14)).Paying(PerJob, PerDay, PerMonth);
        sync.Gateway.Quote = job => job.From < FirstIncluded ? Quote : 0m;

        var pass = await sync.FirstPassAsync();

        var entry = (await sync.Archive.SpendAsync()).ShouldHaveSingleItem();
        entry.Range.From.ShouldBe(FirstIncluded.AddDays(-4));
        pass.Spent.ShouldBe(Quote);
    }

    [Theory]
    [InlineData("true", "0", "0", "0", "must all be set")]
    [InlineData("true", "5", "20", "0", "must all be set")]
    [InlineData("true", "50", "20", "100", "must nest")]
    [InlineData("true", "5", "200", "100", "must nest")]
    [InlineData("false", "-1", "0", "0", "cannot be negative")]
    public async Task A_policy_half_set_stops_the_worker_before_it_asks_for_anything(string allow, string perJob,
        string perDay, string perMonth, string reason)
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.With(SyncScenario.Spend(nameof(SpendPolicy.AllowPaidJobs)), allow)
            .With(SyncScenario.Spend(nameof(SpendPolicy.MaxPerJob)), perJob)
            .With(SyncScenario.Spend(nameof(SpendPolicy.MaxPerDay)), perDay)
            .With(SyncScenario.Spend(nameof(SpendPolicy.MaxPerMonth)), perMonth);

        var refused = Should.Throw<OptionsValidationException>(sync.Start);

        refused.Message.ShouldContain(reason);
        sync.Gateway.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_paid_policy_is_a_warning_in_the_log_each_time_the_worker_starts()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.Paying(PerJob, PerDay, PerMonth);

        await sync.FirstPassAsync();
        await sync.RestartAsync();

        sync.Logs.Lines.ShouldContain(l => l.Level == LogLevel.Warning && l.Text.Contains("paid jobs allowed"));
    }

    [Fact]
    public async Task A_wrong_key_spends_nothing_and_says_why()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.With($"{DatabentoOptions.Section}:{nameof(DatabentoOptions.ApiKey)}", OtherKey);

        var pass = await sync.FirstPassAsync();

        sync.Gateway.CallsTo(Endpoints.GetCost).ShouldBeEmpty();
        pass.Failure.ShouldNotBeNull();
        pass.Issues.ShouldHaveSingleItem().Message.ShouldContain(Unauthorized);
    }

    /// <summary>What the worker asks for on its first pass here: mbo of MNQ, Friday to Tuesday.</summary>
    private static readonly JobSpec Wanted = new(Dataset, Mbo, $"{Mnq}.n.0,{Mnq}.n.1,{Mnq}.v.0", "continuous", FirstDay, LastDay);

    /// <summary>What an earlier run left in the spend ledger for what the worker asks for first, yesterday, so no limit of today's counts it.</summary>
    private static SpendEntry BoughtEarlier(string root = Mnq, string schema = Mbo) =>
        new(Guid.NewGuid().ToString("N"), Now.AddDays(-1), root, schema, new DayRange(FirstDay, LastDay), Quote);

    /// <summary>Databento takes the job and the answer is lost, as a crash or a cut connection leaves it.</summary>
    private static void AcceptAndLoseTheAnswer(SyncScenario sync, Reply lost) =>
        sync.Gateway.Fail(Endpoints.SubmitJob, call =>
        {
            sync.Gateway.Accept(call);

            return lost;
        });

    [Fact]
    public async Task A_crash_after_databento_accepts_a_paid_job_takes_it_up_and_buys_nothing_more()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.Paying(PerJob, PerDay, PerMonth);
        sync.Gateway.Quote = _ => Quote;
        sync.Gateway.NewJobState = JobStates.Processing;
        AcceptAndLoseTheAnswer(sync, Reply.Dropped);

        await sync.FirstPassAsync();
        sync.Gateway.Heal(Endpoints.SubmitJob);
        var restarted = await sync.RestartAsync();

        var job = sync.Gateway.Jobs.ShouldHaveSingleItem();
        sync.Gateway.CallsTo(Endpoints.SubmitJob).ShouldHaveSingleItem();
        restarted.JobsSubmitted.ShouldBe(0);
        restarted.JobsOwed.ShouldBe(0);
        restarted.Spent.ShouldBe(0m);
        restarted.Issues.ShouldHaveSingleItem().Code.ShouldBe(IssueCodes.JobAdopted);
        var pending = (await sync.Archive.PendingAsync()).ShouldHaveSingleItem();
        pending.Id.ShouldBe(job.Id);
        pending.SubmittedAt.ShouldBe(job.SubmittedAt);
    }

    [Fact]
    public async Task The_earlier_unclear_entry_is_all_the_spend_ledger_holds_after_a_job_is_taken_up()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.Paying(PerJob, PerDay, PerMonth);
        sync.Gateway.Quote = _ => Quote;
        AcceptAndLoseTheAnswer(sync, Reply.Dropped);

        await sync.FirstPassAsync();
        var before = (await sync.Archive.SpendAsync()).ShouldHaveSingleItem();
        sync.Gateway.Heal(Endpoints.SubmitJob);
        await sync.RestartAsync();

        var after = (await sync.Archive.SpendAsync()).ShouldHaveSingleItem();
        after.ShouldBe(before);
        after.Cost.ShouldBe(Quote);
    }

    [Fact]
    public async Task A_crash_between_the_two_ledgers_takes_the_job_up_and_keeps_its_entry()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.Paying(PerJob, PerDay, PerMonth);
        sync.Gateway.Quote = _ => Quote;
        sync.Gateway.NewJobState = JobStates.Processing;

        await sync.FirstPassAsync();
        var job = sync.Gateway.Jobs.Single();
        await sync.Archive.DeleteAsync(ArchiveKeys.Job(job.Id));
        var restarted = await sync.RestartAsync();

        restarted.JobsSubmitted.ShouldBe(0);
        sync.Gateway.CallsTo(Endpoints.SubmitJob).ShouldHaveSingleItem();
        (await sync.Archive.PendingAsync()).ShouldHaveSingleItem().Id.ShouldBe(job.Id);
        (await sync.Archive.SpendAsync()).ShouldHaveSingleItem().JobId.ShouldBe(job.Id);
    }

    [Fact]
    public async Task A_job_taken_up_is_collected_like_any_other()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.Paying(PerJob, PerDay, PerMonth);
        sync.Gateway.Quote = _ => Quote;
        AcceptAndLoseTheAnswer(sync, Reply.Dropped);

        await sync.FirstPassAsync();
        sync.Gateway.Heal(Endpoints.SubmitJob);
        await sync.RestartAsync();
        var collected = await sync.NextPassAsync();

        collected.JobsCollected.ShouldBe(1);
        collected.FilesStored.ShouldBe(TradingDays.Length);
        (await sync.Archive.PendingAsync()).ShouldBeEmpty();
        sync.Gateway.CallsTo(Endpoints.SubmitJob).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_server_error_after_databento_accepts_a_paid_job_stays_counted_and_the_job_is_taken_up()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.Paying(PerJob, PerDay, PerMonth);
        sync.Gateway.Quote = _ => Quote;
        AcceptAndLoseTheAnswer(sync, Reply.Json(StatusCodes.Status503ServiceUnavailable, """{"detail":"upstream"}"""));

        var failed = await sync.FirstPassAsync();
        sync.Gateway.Heal(Endpoints.SubmitJob);
        var next = await sync.NextPassAsync();

        failed.Issues.ShouldHaveSingleItem().Code.ShouldBe(Errors.ServerError);
        failed.Issues.Single().Level.ShouldBe(IssueLevel.Warning);
        next.JobsSubmitted.ShouldBe(0);
        sync.Gateway.Jobs.ShouldHaveSingleItem();
        (await sync.Archive.SpendAsync()).ShouldHaveSingleItem().Cost.ShouldBe(Quote);
    }

    [Fact]
    public async Task A_job_is_taken_up_though_the_day_limit_is_spent_on_it()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.Paying(Quote, Quote, PerMonth);
        sync.Gateway.Quote = _ => Quote;
        AcceptAndLoseTheAnswer(sync, Reply.Dropped);

        await sync.FirstPassAsync();
        sync.Gateway.Heal(Endpoints.SubmitJob);
        var restarted = await sync.RestartAsync();

        restarted.QuotesRefused.ShouldBe(0);
        (await sync.Archive.PendingAsync()).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task What_is_left_to_spend_after_a_job_is_taken_up_counts_it_once()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.Keeping(TwoRoots, Definition).Paying(PerJob, 2 * Quote, PerMonth);
        sync.Gateway.Quote = _ => Quote;
        var taken = 0;
        sync.Gateway.Fail(Endpoints.SubmitJob, call =>
        {
            if (Interlocked.Increment(ref taken) > 1)
                return null;

            sync.Gateway.Accept(call);

            return Reply.Dropped;
        });

        await sync.FirstPassAsync();
        sync.Gateway.Heal(Endpoints.SubmitJob);
        var restarted = await sync.RestartAsync();

        sync.Gateway.Jobs.Select(j => j.Spec.Root).ShouldBe([Es, Mnq], ignoreOrder: true);
        (await sync.Archive.SpendAsync()).Sum(e => e.Cost).ShouldBe(2 * Quote);
        restarted.Spent.ShouldBe(0m);
    }

    public static TheoryData<JobSpec> SimilarJobs() => new()
    {
        Wanted with { From = FirstDay.AddDays(1) },
        Wanted with { From = FirstDay.AddDays(-1) },
        Wanted with { Upto = LastDay.AddDays(-1) },
        Wanted with { Schema = Ohlcv },
        Wanted with { Symbols = $"{Mnq}.n.0" },
        Wanted with { StypeIn = "parent" },
        Wanted with { Dataset = "XNAS.ITCH" }
    };

    [Theory]
    [MemberData(nameof(SimilarJobs))]
    public async Task A_similar_job_is_not_taken_up_and_the_days_are_bought(JobSpec similar)
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        await sync.Archive.SeedSpendAsync(BoughtEarlier());
        sync.Paying(PerJob, PerDay, PerMonth);
        sync.Gateway.Quote = _ => Quote;
        var held = sync.Gateway.Hold(similar, JobStates.Done);

        var pass = await sync.FirstPassAsync();

        pass.JobsSubmitted.ShouldBe(1);
        (await sync.Archive.PendingAsync()).ShouldHaveSingleItem().Id.ShouldNotBe(held.Id);
        (await sync.Archive.SpendAsync()).Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_job_of_another_encoding_is_not_taken_up()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        await sync.Archive.SeedSpendAsync(BoughtEarlier());
        sync.Paying(PerJob, PerDay, PerMonth);
        sync.Gateway.Quote = _ => Quote;
        sync.Gateway.Hold(Wanted, JobStates.Done, encoding: "csv");

        var pass = await sync.FirstPassAsync();

        pass.JobsSubmitted.ShouldBe(1);
    }

    [Theory]
    [InlineData(JobStates.Expired)]
    [InlineData(JobStates.Failed)]
    public async Task A_job_expired_or_failed_is_not_taken_up(string state)
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        await sync.Archive.SeedSpendAsync(BoughtEarlier());
        sync.Paying(PerJob, PerDay, PerMonth);
        sync.Gateway.Quote = _ => Quote;
        sync.Gateway.Hold(Wanted, state);

        var pass = await sync.FirstPassAsync();

        pass.JobsSubmitted.ShouldBe(1);
        sync.Gateway.CallsTo(Endpoints.JobDetails).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_job_whose_files_expired_is_not_taken_up_though_it_still_says_done()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        await sync.Archive.SeedSpendAsync(BoughtEarlier());
        sync.Paying(PerJob, PerDay, PerMonth);
        sync.Gateway.Quote = _ => Quote;
        sync.Gateway.Hold(Wanted, JobStates.Done).ExpiresAt = Now;

        var pass = await sync.FirstPassAsync();

        pass.JobsSubmitted.ShouldBe(1);
    }

    public static TheoryData<Reply, string> UnusableListings() => new()
    {
        { Reply.Dropped, Errors.Unreachable },
        { Reply.Json(StatusCodes.Status500InternalServerError, "{}"), Errors.ServerError },
        { Reply.Json(StatusCodes.Status403Forbidden, "{}"), Errors.Rejected },
        { Reply.Ok("not json"), Malformed },
        { Reply.Ok("{}"), Malformed },
        { Reply.Ok("""[{"id":"X","state":"held","ts_received":"2026-11-04T00:00:00.000000000Z"}]"""), Malformed }
    };

    [Theory]
    [MemberData(nameof(UnusableListings))]
    public async Task A_listing_that_fails_or_cannot_be_read_buys_nothing_this_pass(Reply listing, string code)
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        await sync.Archive.SeedSpendAsync(BoughtEarlier());
        sync.Paying(PerJob, PerDay, PerMonth);
        sync.Gateway.Quote = _ => Quote;
        sync.Gateway.Fail(Endpoints.ListJobs, listing);

        var pass = await sync.FirstPassAsync();

        sync.Gateway.CallsTo(Endpoints.SubmitJob).ShouldBeEmpty();
        (await sync.Archive.SpendAsync()).ShouldHaveSingleItem().JobId.ShouldBeNull();
        (await sync.Archive.PendingAsync()).ShouldBeEmpty();
        pass.Spent.ShouldBe(0m);
        pass.Issues.ShouldHaveSingleItem().Code.ShouldBe(code);
    }

    [Fact]
    public async Task Details_that_cannot_be_read_buy_nothing_this_pass()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        await sync.Archive.SeedSpendAsync(BoughtEarlier());
        sync.Paying(PerJob, PerDay, PerMonth);
        sync.Gateway.Quote = _ => Quote;
        sync.Gateway.Hold(Wanted, JobStates.Done);
        sync.Gateway.Fail(Endpoints.JobDetails, Reply.Ok("{}"));

        var pass = await sync.FirstPassAsync();

        sync.Gateway.CallsTo(Endpoints.SubmitJob).ShouldBeEmpty();
        (await sync.Archive.SpendAsync()).ShouldHaveSingleItem();
        pass.Issues.ShouldHaveSingleItem().Code.ShouldBe(Malformed);
    }

    [Fact]
    public async Task Once_the_listing_answers_again_the_days_are_bought()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        await sync.Archive.SeedSpendAsync(BoughtEarlier());
        sync.Paying(PerJob, PerDay, PerMonth);
        sync.Gateway.Quote = _ => Quote;
        sync.Gateway.Fail(Endpoints.ListJobs, Reply.Dropped);

        await sync.FirstPassAsync();
        sync.Gateway.Heal(Endpoints.ListJobs);
        var next = await sync.NextPassAsync();

        next.JobsSubmitted.ShouldBe(1);
        next.Spent.ShouldBe(Quote);
    }

    [Fact]
    public async Task Free_jobs_still_go_while_the_listing_is_down()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        await sync.Archive.SeedSpendAsync(BoughtEarlier());
        sync.Keeping(Mnq, $"{Mbo},{Definition}").Paying(PerJob, PerDay, PerMonth);
        sync.Gateway.Quote = job => job.Schema == Mbo ? Quote : 0m;
        sync.Gateway.Fail(Endpoints.ListJobs, Reply.Dropped);

        await sync.FirstPassAsync();

        sync.Gateway.CallsTo(Endpoints.SubmitJob).ShouldHaveSingleItem().Job.Schema.ShouldBe(Definition);
    }

    [Fact]
    public async Task Free_jobs_never_ask_for_the_listing()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.Paying(PerJob, PerDay, PerMonth);

        await sync.FirstPassAsync();

        sync.Gateway.CallsTo(Endpoints.ListJobs).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_free_job_is_not_taken_up_and_goes_as_it_always_did()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        await sync.Archive.SeedSpendAsync(BoughtEarlier());
        sync.Paying(PerJob, PerDay, PerMonth);
        sync.Gateway.Hold(Wanted, JobStates.Done);

        var pass = await sync.FirstPassAsync();

        pass.JobsSubmitted.ShouldBe(1);
        sync.Gateway.Jobs.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Jobs_the_ledger_knows_are_not_asked_about()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        await sync.Archive.SeedSpendAsync(BoughtEarlier(schema: Definition));
        sync.Keeping(TwoRoots, Definition).Paying(PerJob, PerDay, PerMonth);
        sync.Gateway.Quote = _ => Quote;
        sync.Gateway.NewJobState = JobStates.Processing;

        await sync.FirstPassAsync();

        sync.Gateway.CallsTo(Endpoints.ListJobs).ShouldHaveSingleItem();
        sync.Gateway.CallsTo(Endpoints.JobDetails).ShouldBeEmpty();
    }

    [Fact]
    public async Task With_no_spend_entry_for_a_job_databento_is_never_asked_what_it_holds()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        await sync.Archive.SeedSpendAsync(BoughtEarlier() with { Range = new DayRange(FirstDay, FirstDay) });
        await sync.Archive.SeedSpendAsync(BoughtEarlier(root: Es));
        await sync.Archive.SeedSpendAsync(BoughtEarlier(schema: Definition));
        sync.Paying(PerJob, PerDay, PerMonth);
        sync.Gateway.Quote = _ => Quote;
        sync.Gateway.Hold(Wanted, JobStates.Done);
        sync.Gateway.Hold(Wanted, JobStates.Done);

        var pass = await sync.FirstPassAsync();

        sync.Gateway.CallsTo(Endpoints.ListJobs).ShouldBeEmpty();
        sync.Gateway.CallsTo(Endpoints.JobDetails).ShouldBeEmpty();
        pass.JobsSubmitted.ShouldBe(1);
    }

    [Fact]
    public async Task A_listing_that_fails_stops_only_the_job_with_a_matching_entry()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        await sync.Archive.SeedSpendAsync(BoughtEarlier(root: Es, schema: Definition));
        sync.Keeping(TwoRoots, Definition).Paying(PerJob, PerDay, PerMonth);
        sync.Gateway.Quote = _ => Quote;
        sync.Gateway.Fail(Endpoints.ListJobs, Reply.Dropped);

        var pass = await sync.FirstPassAsync();

        sync.Gateway.CallsTo(Endpoints.SubmitJob).ShouldHaveSingleItem().Job.Root.ShouldBe(Mnq);
        var issue = pass.Issues.ShouldHaveSingleItem();
        issue.Code.ShouldBe(Errors.Unreachable);
        issue.Root.ShouldBe(Es);
        pass.Spent.ShouldBe(Quote);
    }

    [Fact]
    public async Task The_listing_starts_from_the_day_of_the_earliest_matching_entry()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        await sync.Archive.SeedSpendAsync(BoughtEarlier());
        await sync.Archive.SeedSpendAsync(BoughtEarlier() with { At = Now.AddDays(-2) });
        await sync.Archive.SeedSpendAsync(BoughtEarlier(root: Es) with { At = Now.AddDays(-3) });
        sync.Paying(PerJob, PerDay, PerMonth);
        sync.Gateway.Quote = _ => Quote;

        await sync.FirstPassAsync();

        sync.Gateway.CallsTo(Endpoints.ListJobs).ShouldHaveSingleItem().Fields[Since]
            .ShouldBe(Now.AddDays(-3).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task An_entry_from_last_month_still_leads_to_its_job()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        await sync.Archive.SeedSpendAsync(BoughtEarlier() with { At = Now.AddDays(-20) });
        sync.Paying(PerJob, PerDay, PerMonth);
        sync.Gateway.Quote = _ => Quote;
        var held = sync.Gateway.Hold(Wanted, JobStates.Done);

        var pass = await sync.FirstPassAsync();

        pass.JobsSubmitted.ShouldBe(0);
        (await sync.Archive.PendingAsync()).ShouldHaveSingleItem().Id.ShouldBe(held.Id);
    }
}
