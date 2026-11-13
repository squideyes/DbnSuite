using DbnSuite.IntegrationTests.Support;
using DbnSuite.Sync.Core;
using Microsoft.AspNetCore.Http;
using Shouldly;
using static DbnSuite.IntegrationTests.Support.Sample;

namespace DbnSuite.IntegrationTests;

/// <summary>
/// After money, the worst failure is a hole nobody sees: a day that looks held
/// and is not, or one marked empty that had data. Every failure here must end
/// with the day stored, or still visibly owed.
/// </summary>
[Collection(ArchiveCollection.Name)]
public sealed class GapsTests(RustFs s3)
{
    private const int NotFound = 404;

    private const string Corrupted = "download.corrupt";
    private const string BrokenLedgerEntry = "not json";

    private static readonly DayRange AllDays = new(FirstDay, LastDay);
    private static readonly string BrokenLedgerKey = ArchiveKeys.Job("GLBX-BROKEN");

    private static string DataKey(DateOnly day) => ArchiveKeys.Data(Dataset, Mbo, Mnq, day);

    private static string EmptyKey(DateOnly day) => ArchiveKeys.Empty(Dataset, Mbo, Mnq, day);

    private static string File(DateOnly day) => BatchFactory.FileName(Mbo, day);

    private static async Task<IReadOnlyList<string>> SeriesAsync(SyncScenario sync) =>
        await sync.Archive.KeysAsync(ArchiveKeys.Folder(Dataset, Mbo, Mnq));

    [Fact]
    public async Task A_corrupt_download_is_thrown_away_marks_nothing_empty_and_comes_again()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        await sync.FirstPassAsync();
        var good = sync.Gateway.Jobs.Single().DataFile(Monday).Bytes;
        byte[] altered = [.. good];
        altered[^1] ^= 0xFF;
        sync.Gateway.Fail(Endpoints.Download, call => call.File == File(Monday) ? Reply.Ok(altered) : null);

        var failed = await sync.NextPassAsync();
        var afterFailure = await SeriesAsync(sync);
        sync.Gateway.Heal(Endpoints.Download);
        var retried = await sync.NextPassAsync();

        var issue = failed.Issues.ShouldHaveSingleItem();
        issue.Code.ShouldBe(Corrupted);
        issue.Level.ShouldBe(IssueLevel.Error);
        issue.JobId.ShouldBe(sync.Gateway.Jobs.Single().Id);
        afterFailure.ShouldNotContain(DataKey(Monday));
        afterFailure.ShouldNotContain(EmptyKey(Saturday));
        retried.JobsCollected.ShouldBe(1);
        (await sync.Archive.ReadAsync(DataKey(Monday))).ShouldBe(good);
        (await SeriesAsync(sync)).Count.ShouldBe(AllDays.Days);
    }

    [Fact]
    public async Task A_short_download_never_looks_downloaded()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        await sync.FirstPassAsync();
        var whole = sync.Gateway.Jobs.Single().DataFile(Monday).Bytes;
        sync.Gateway.Fail(Endpoints.Download, call => call.File == File(Monday) ? Reply.Ok(whole[..^1]) : null);

        var failed = await sync.NextPassAsync();

        failed.Issues.ShouldHaveSingleItem().Code.ShouldBe(Corrupted);
        (await SeriesAsync(sync)).ShouldNotContain(DataKey(Monday));
    }

    [Fact]
    public async Task Files_that_landed_are_kept_and_only_the_failed_one_comes_again()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        await sync.FirstPassAsync();
        sync.Gateway.Fail(Endpoints.Download, call => call.File == File(LastDay)
            ? Reply.Json(StatusCodes.Status404NotFound, """{"detail":"not found"}""")
            : null);

        var failed = await sync.NextPassAsync();
        sync.Gateway.Heal(Endpoints.Download);
        await sync.NextPassAsync();

        failed.FilesStored.ShouldBe(TradingDays.Length - 1);
        var downloads = sync.Gateway.CallsTo(Endpoints.Download);
        downloads.Count(c => c.File == File(FirstDay)).ShouldBe(1);
        downloads.Count(c => c.File == File(Monday)).ShouldBe(1);
        downloads.Count(c => c.File == File(LastDay)).ShouldBe(2);
    }

    [Fact]
    public async Task A_connection_cut_mid_file_is_a_warning_and_leaves_nothing_behind()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        await sync.FirstPassAsync();
        var whole = sync.Gateway.Jobs.Single().DataFile(Monday).Bytes;
        sync.Gateway.Fail(Endpoints.Download, call => call.File == File(Monday)
            ? new Reply(StatusCodes.Status200OK, whole, DropAfter: whole.Length / 2)
            : null);

        var failed = await sync.NextPassAsync();

        var issue = failed.Issues.ShouldHaveSingleItem();
        issue.Code.ShouldBe(Errors.Unreachable);
        issue.Level.ShouldBe(IssueLevel.Warning);
        (await SeriesAsync(sync)).ShouldNotContain(DataKey(Monday));
    }

    [Fact]
    public async Task A_job_that_expired_has_its_days_asked_for_again_in_the_same_pass()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.Gateway.NewJobState = JobStates.Processing;
        await sync.FirstPassAsync();
        sync.Gateway.SetAllJobs(JobStates.Expired);
        sync.Gateway.NewJobState = JobStates.Done;

        var replanned = await sync.NextPassAsync();
        var stored = await sync.NextPassAsync();

        replanned.Issues.ShouldHaveSingleItem().Code.ShouldBe(IssueCodes.JobGone);
        replanned.JobsSubmitted.ShouldBe(1);
        sync.Gateway.Jobs.Count.ShouldBe(2);
        sync.Gateway.Jobs[1].Spec.From.ShouldBe(FirstDay);
        stored.FilesStored.ShouldBe(TradingDays.Length);
    }

    [Fact]
    public async Task A_job_Databento_no_longer_knows_is_gone_too()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.Gateway.NewJobState = JobStates.Processing;
        await sync.FirstPassAsync();
        sync.Gateway.Fail(Endpoints.ListJobs, Reply.Ok("[]"));
        sync.Gateway.Fail(Endpoints.JobDetails, Reply.Json(NotFound, "{}"));

        var replanned = await sync.NextPassAsync();

        replanned.Issues.ShouldHaveSingleItem().Code.ShouldBe(IssueCodes.JobGone);
        (await sync.Archive.PendingAsync()).ShouldHaveSingleItem().Id.ShouldBe(sync.Gateway.Jobs[1].Id);
    }

    [Fact]
    public async Task A_job_missing_from_one_listing_is_kept_when_Databento_still_knows_it()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.Gateway.NewJobState = JobStates.Processing;
        await sync.FirstPassAsync();
        sync.Gateway.Fail(Endpoints.ListJobs, Reply.Ok("[]"));

        var kept = await sync.NextPassAsync();

        kept.JobsSubmitted.ShouldBe(0);
        sync.Gateway.Jobs.Count.ShouldBe(1);
        (await sync.Archive.PendingAsync()).ShouldHaveSingleItem().Id.ShouldBe(sync.Gateway.Jobs[0].Id);
    }

    [Fact]
    public async Task A_hole_in_the_archive_is_asked_for_again_and_nothing_else_is()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        await sync.FirstPassAsync();
        await sync.NextPassAsync();
        await sync.Archive.DeleteAsync(DataKey(Monday));

        var refill = await sync.NextPassAsync();
        var stored = await sync.NextPassAsync();

        refill.JobsSubmitted.ShouldBe(1);
        var job = sync.Gateway.Jobs[^1].Spec;
        job.From.ShouldBe(Monday);
        job.Upto.ShouldBe(Monday);
        stored.FilesStored.ShouldBe(1);
        (await sync.Archive.ReadAsync(DataKey(Monday))).ShouldBe(sync.Gateway.Jobs[^1].DataFile(Monday).Bytes);
    }

    [Theory]
    [InlineData(Errors.NoSymbols)]
    [InlineData(Errors.NoData)]
    public async Task Days_Databento_has_nothing_for_are_marked_empty_and_not_asked_for_again(string code)
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.Gateway.Fail(Endpoints.GetCost, code == Errors.NoSymbols ? Reply.NoSymbols : Reply.NoData);

        var settled = await sync.FirstPassAsync();
        var quotes = sync.Gateway.CallsTo(Endpoints.GetCost).Count;
        await sync.NextPassAsync();

        settled.EmptyDays.ShouldBe(AllDays.Days);
        settled.Issues.ShouldHaveSingleItem().Code.ShouldBe(code);
        (await SeriesAsync(sync)).ShouldBe([.. AllDays.Each().Select(EmptyKey)], ignoreOrder: true);
        sync.Gateway.CallsTo(Endpoints.GetCost).Count.ShouldBe(quotes);
        sync.Delay.ShouldBe(TestClock.Interval);
    }

    [Fact]
    public async Task A_job_that_delivers_no_files_marks_every_day_empty()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.Gateway.Quiet(TradingDays);

        await sync.FirstPassAsync();
        var collected = await sync.NextPassAsync();

        collected.JobsCollected.ShouldBe(1);
        collected.EmptyDays.ShouldBe(AllDays.Days);
    }

    [Fact]
    public async Task A_file_for_a_day_not_asked_for_stores_nothing_and_the_job_is_abandoned_after_a_week()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.Gateway.NewJobState = JobStates.Processing;
        await sync.FirstPassAsync();
        var job = sync.Gateway.Jobs.Single();
        job.Files.Add(new FakeFile(File(FirstDay.AddDays(-1)), [1, 2, 3]));
        job.State = JobStates.Done;
        sync.Gateway.NewJobState = JobStates.Done;

        var refused = await sync.NextPassAsync();
        var keysAfterRefusal = await SeriesAsync(sync);
        var abandoned = await sync.PassAfterAsync(TimeSpan.FromDays(8));

        var issue = refused.Issues.ShouldHaveSingleItem();
        issue.Code.ShouldBe(IssueCodes.UnexpectedFile);
        issue.Level.ShouldBe(IssueLevel.Error);
        keysAfterRefusal.ShouldBeEmpty();
        abandoned.Failure.ShouldBeNull(sync.Explain(abandoned));
        abandoned.Issues.Select(i => i.Code).ShouldBe([IssueCodes.UnexpectedFile, IssueCodes.JobAbandoned], ignoreOrder: true,
            sync.Explain(abandoned));
        abandoned.JobsSubmitted.ShouldBe(1, sync.Explain(abandoned));
    }

    [Fact]
    public async Task A_job_whose_files_cannot_be_listed_stays_pending()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        await sync.FirstPassAsync();
        sync.Gateway.Fail(Endpoints.ListFiles, Reply.Json(StatusCodes.Status503ServiceUnavailable, "{}"));

        var failed = await sync.NextPassAsync();
        sync.Gateway.Heal(Endpoints.ListFiles);
        var collected = await sync.NextPassAsync();

        failed.JobsPending.ShouldBe(1);
        failed.Issues.ShouldHaveSingleItem().Message.ShouldContain("Files unavailable");
        collected.JobsCollected.ShouldBe(1);
    }

    [Fact]
    public async Task A_job_whose_state_cannot_be_read_stays_pending()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        await sync.FirstPassAsync();
        sync.Gateway.Fail(Endpoints.ListJobs, Reply.Json(StatusCodes.Status503ServiceUnavailable, "{}"));

        var failed = await sync.NextPassAsync();

        failed.JobsPending.ShouldBe(1);
        failed.Issues.ShouldHaveSingleItem().Message.ShouldContain("State unavailable");
        (await sync.Archive.PendingAsync()).ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData(Endpoints.ListJobs, """{"jobs":[]}""")]
    [InlineData(Endpoints.ListJobs, """[{"state":"done"}]""")]
    [InlineData(Endpoints.ListFiles, """{"files":[]}""")]
    [InlineData(Endpoints.ListFiles, """[{"size":"big"}]""")]
    public async Task An_answer_of_the_wrong_shape_is_a_malformed_issue_and_the_pass_carries_on(string endpoint, string body)
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        await sync.FirstPassAsync();
        sync.Gateway.Fail(endpoint, Reply.Ok(body));

        var failed = await sync.NextPassAsync();

        failed.Failure.ShouldBeNull();
        failed.Issues.ShouldHaveSingleItem().Code.ShouldBe(Errors.Malformed);
        failed.JobsPending.ShouldBe(1);
    }

    [Fact]
    public async Task A_dataset_range_of_the_wrong_shape_skips_the_pass_without_crashing_it()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.Gateway.Fail(Endpoints.DatasetRange, Reply.Ok("""{"schema":{"mbo":{"end":20261104}}}"""));

        var failed = await sync.FirstPassAsync();

        failed.Issues.ShouldHaveSingleItem().Code.ShouldBe(Errors.Malformed);
        sync.Gateway.CallsTo(Endpoints.GetCost).ShouldBeEmpty();
    }

    [Fact]
    public async Task With_the_dataset_range_unavailable_the_pass_is_skipped_and_tried_again_soon()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.Gateway.Fail(Endpoints.DatasetRange, Reply.Json(StatusCodes.Status503ServiceUnavailable, "{}"));

        var skipped = await sync.FirstPassAsync();
        var delay = sync.Delay;
        sync.Gateway.Heal(Endpoints.DatasetRange);
        var retried = await sync.NextPassAsync();

        skipped.Failure.ShouldNotBeNull();
        delay.ShouldBe(TestClock.BusyInterval);
        retried.JobsSubmitted.ShouldBe(1);
        (await sync.Archive.PassesAsync()).Count.ShouldBe(2);
    }

    [Fact]
    public async Task With_the_gateway_down_nothing_is_lost_and_the_worker_keeps_going()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        await sync.FirstPassAsync();
        await sync.Gateway.StopAsync();

        var down = await sync.NextPassAsync();

        down.Issues.ShouldHaveSingleItem().Code.ShouldBe(Errors.Unreachable);
        sync.Delay.ShouldBe(TestClock.BusyInterval);
        (await sync.Archive.PendingAsync()).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_pass_that_crashes_is_recorded_and_the_next_one_recovers()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        await sync.FirstPassAsync();
        await sync.Archive.PutAsync(BrokenLedgerKey, BrokenLedgerEntry);

        var crashed = await sync.NextPassAsync();
        var delay = sync.Delay;
        var scanFailure = sync.Corpus.Snapshot!.Failure;
        await sync.Archive.DeleteAsync(BrokenLedgerKey);
        var recovered = await sync.NextPassAsync();

        crashed.Issues.ShouldHaveSingleItem().Code.ShouldBe(IssueCodes.Crashed);
        delay.ShouldBe(TestClock.BusyInterval);
        scanFailure.ShouldNotBeNull();
        recovered.JobsCollected.ShouldBe(1);
        sync.Corpus.Snapshot!.Failure.ShouldBeNull();
        (await sync.Archive.PassesAsync()).ShouldContain(p => p.Issues.Any(i => i.Code == IssueCodes.Crashed));
    }

    [Fact]
    public async Task A_bucket_that_disappears_stops_nothing_but_the_pass()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        await sync.FirstPassAsync();
        await sync.Archive.DropBucketAsync();

        var crashed = await sync.NextPassAsync();

        crashed.Issues.ShouldHaveSingleItem().Code.ShouldBe(IssueCodes.Crashed);
        sync.Delay.ShouldBe(TestClock.BusyInterval);
        sync.Logs.Lines.ShouldContain(l => l.Text.Contains("could not be recorded"));
    }
}
