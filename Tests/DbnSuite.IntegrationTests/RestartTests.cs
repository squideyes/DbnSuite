using DbnSuite.IntegrationTests.Support;
using DbnSync.Core;
using Shouldly;
using static DbnSuite.IntegrationTests.Support.Sample;

namespace DbnSuite.IntegrationTests;

/// <summary>The archive is the only state: a new process picks up from the bucket, wherever the last one stopped.</summary>
[Collection(ArchiveCollection.Name)]
public sealed class RestartTests(RustFs s3)
{
    private static readonly TimeSpan GraceToReachGateway = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_restart_collects_what_the_last_run_submitted()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.Gateway.NewJobState = JobStates.Processing;

        await sync.FirstPassAsync();
        sync.Gateway.SetAllJobs(JobStates.Done);
        var restarted = await sync.RestartAsync();

        restarted.JobsCollected.ShouldBe(1);
        restarted.JobsSubmitted.ShouldBe(0);
        sync.Gateway.Jobs.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_restart_with_everything_stored_asks_for_nothing()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);

        await sync.FirstPassAsync();
        await sync.NextPassAsync();
        var quotes = sync.Gateway.CallsTo(Endpoints.GetCost).Count;
        var restarted = await sync.RestartAsync();

        restarted.Eventful.ShouldBeFalse();
        sync.Gateway.CallsTo(Endpoints.GetCost).Count.ShouldBe(quotes);
        sync.Delay.ShouldBe(TestClock.Interval);
    }

    [Fact]
    public async Task A_restart_after_a_partial_job_fetches_only_the_days_still_missing()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        await sync.FirstPassAsync();
        sync.Gateway.Fail(Endpoints.Download, call => call.File == BatchFactory.FileName(Mbo, LastDay) ? Reply.Dropped : null);
        await sync.NextPassAsync();
        var downloads = sync.Gateway.CallsTo(Endpoints.Download).Count;

        sync.Gateway.Heal(Endpoints.Download);
        var restarted = await sync.RestartAsync();

        restarted.JobsCollected.ShouldBe(1);
        restarted.FilesStored.ShouldBe(1);
        sync.Gateway.CallsTo(Endpoints.Download).Count.ShouldBe(downloads + 1);
    }

    [Fact]
    public async Task A_worker_stopped_mid_download_stores_nothing_partial_and_finishes_after_restart()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        await sync.FirstPassAsync();
        var downloading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sync.Gateway.OnDownload = async _ =>
        {
            downloading.TrySetResult();

            await release.Task;
        };

        sync.Clock.Advance(sync.Delay);
        await downloading.Task.WaitAsync(GraceToReachGateway);
        await sync.StopAsync();
        var afterStop = await sync.Archive.KeysAsync(ArchiveKeys.Folder(Dataset, Mbo, Mnq));
        sync.Gateway.OnDownload = null;
        release.SetResult();
        var restarted = await sync.FirstPassAsync();

        afterStop.ShouldBeEmpty();
        restarted.JobsCollected.ShouldBe(1);
        restarted.FilesStored.ShouldBe(TradingDays.Length);
        (await sync.Archive.KeysAsync(ArchiveKeys.Folder(Dataset, Mbo, Mnq))).Count.ShouldBe(5);
    }

    [Fact]
    public async Task A_hole_that_ages_out_while_the_worker_is_down_shows_as_lost_and_is_not_bought()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        await sync.FirstPassAsync();
        await sync.NextPassAsync();
        await sync.Archive.DeleteAsync(ArchiveKeys.Data(Dataset, Mbo, Mnq, Monday));
        await sync.StopAsync();
        var submitted = sync.Gateway.Jobs.Count;

        sync.Clock.Advance(TimeSpan.FromDays(40));
        sync.Gateway.AvailableEnd = AvailableEnd.AddDays(40);
        await sync.FirstPassAsync();

        sync.Corpus.Snapshot!.Series.ShouldHaveSingleItem().States[Monday].ShouldBe(DayState.Lost);
        sync.Gateway.Jobs.Skip(submitted).ShouldAllBe(j => j.Spec.From > Monday);
    }
}
