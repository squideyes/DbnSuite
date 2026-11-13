using DbnSuite.IntegrationTests.Support;
using DbnSuite.Sync.Core;
using Shouldly;
using static DbnSuite.IntegrationTests.Support.Sample;

namespace DbnSuite.IntegrationTests;

/// <summary>Passes end to end: gateway to bucket, with nothing faked but Databento.</summary>
[Collection(ArchiveCollection.Name)]
public sealed class SyncPassTests(RustFs s3)
{
    private const string NestedPrefix = "archive/nas1";
    private const string KeysFolder = ".keys";

    /// <summary>Past the 16 MB where the SDK's transfer utility switches to a multipart upload.</summary>
    private const int MultipartBytes = 20 * 1024 * 1024;
    private const int Seed = 42;

    [Fact]
    public async Task A_job_is_submitted_collected_and_stored_byte_for_byte()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);

        var first = await sync.FirstPassAsync();
        var second = await sync.NextPassAsync();

        first.JobsSubmitted.ShouldBe(1);
        second.JobsCollected.ShouldBe(1);
        second.FilesStored.ShouldBe(TradingDays.Length);
        var job = sync.Gateway.Jobs.ShouldHaveSingleItem();
        foreach (var day in TradingDays)
            (await sync.Archive.ReadAsync(ArchiveKeys.Data(Dataset, Mbo, Mnq, day))).ShouldBe(job.DataFile(day).Bytes);
    }

    [Fact]
    public async Task Days_without_a_file_get_an_empty_marker_and_support_files_are_left_behind()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);

        await sync.FirstPassAsync();
        var collected = await sync.NextPassAsync();

        collected.EmptyDays.ShouldBe(2);
        (await sync.Archive.KeysAsync(ArchiveKeys.Folder(Dataset, Mbo, Mnq))).ShouldBe(
        [
            ArchiveKeys.Data(Dataset, Mbo, Mnq, FirstDay),
            ArchiveKeys.Empty(Dataset, Mbo, Mnq, Saturday),
            ArchiveKeys.Empty(Dataset, Mbo, Mnq, Sunday),
            ArchiveKeys.Data(Dataset, Mbo, Mnq, Monday),
            ArchiveKeys.Data(Dataset, Mbo, Mnq, LastDay)
        ], ignoreOrder: true);
    }

    [Fact]
    public async Task A_job_stays_on_the_ledger_from_submission_until_it_is_stored()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);

        await sync.FirstPassAsync();
        var whileInFlight = await sync.Archive.PendingAsync();
        await sync.NextPassAsync();

        var pending = whileInFlight.ShouldHaveSingleItem();
        pending.Id.ShouldBe(sync.Gateway.Jobs.Single().Id);
        pending.Range.ShouldBe(new DayRange(FirstDay, LastDay));
        (await sync.Archive.PendingAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_job_still_processing_is_left_alone_and_the_next_pass_comes_soon()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.Gateway.NewJobState = JobStates.Processing;

        await sync.FirstPassAsync();
        var waiting = await sync.NextPassAsync();

        waiting.JobsPending.ShouldBe(1);
        waiting.JobsSubmitted.ShouldBe(0);
        sync.Gateway.CallsTo(Endpoints.ListFiles).ShouldBeEmpty();
        sync.Delay.ShouldBe(TestClock.BusyInterval);
    }

    [Fact]
    public async Task Once_caught_up_the_worker_waits_six_hours_and_a_quiet_pass_leaves_no_record()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);

        await sync.FirstPassAsync();
        await sync.NextPassAsync();
        var delayWhenDone = sync.Delay;
        var quiet = await sync.NextPassAsync();

        delayWhenDone.ShouldBe(TestClock.Interval);
        quiet.Eventful.ShouldBeFalse();
        (await sync.Archive.PassesAsync()).Count.ShouldBe(2);
        sync.Gateway.CallsTo(Endpoints.SubmitJob).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_new_day_is_picked_up_when_the_dataset_moves_on()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);

        await sync.FirstPassAsync();
        await sync.NextPassAsync();
        sync.Gateway.AvailableEnd = AvailableEnd.AddDays(1);
        var next = await sync.PassAfterAsync(TimeSpan.FromDays(1));

        next.JobsSubmitted.ShouldBe(1);
        var job = sync.Gateway.Jobs[^1].Spec;
        job.From.ShouldBe(LastDay.AddDays(1));
        job.Upto.ShouldBe(LastDay.AddDays(1));
    }

    [Fact]
    public async Task Order_books_are_asked_for_first_since_they_age_out_first()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.Keeping(Mnq, $"{Definition},{Ohlcv},{Mbo}").MaxPending(1);

        var pass = await sync.FirstPassAsync();

        sync.Gateway.Jobs.ShouldHaveSingleItem().Spec.Schema.ShouldBe(Mbo);
        pass.JobsOwed.ShouldBe(2);
    }

    [Fact]
    public async Task Books_are_asked_for_by_leading_outright_and_the_rest_by_whole_root()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.Keeping(Mnq, $"{Mbo},{Definition}");

        await sync.FirstPassAsync();

        var jobs = sync.Gateway.Jobs.ToDictionary(j => j.Spec.Schema, j => j.Spec);
        jobs[Mbo].Symbols.ShouldBe($"{Mnq}.n.0,{Mnq}.n.1,{Mnq}.v.0");
        jobs[Mbo].StypeIn.ShouldBe("continuous");
        jobs[Definition].Symbols.ShouldBe($"{Mnq}.FUT");
        jobs[Definition].StypeIn.ShouldBe("parent");
    }

    [Fact]
    public async Task Jobs_are_asked_for_as_daily_compressed_downloads()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);

        await sync.FirstPassAsync();

        var fields = sync.Gateway.CallsTo(Endpoints.SubmitJob).ShouldHaveSingleItem().Fields;
        fields["split_duration"].ShouldBe("day");
        fields["compression"].ShouldBe("zstd");
        fields["encoding"].ShouldBe("dbn");
        fields["delivery"].ShouldBe("download");
    }

    [Fact]
    public async Task Jobs_in_flight_never_pass_the_pending_limit()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.Keeping($"{Es},{Mnq},{Nq}", Mbo).MaxPending(2);
        sync.Gateway.NewJobState = JobStates.Processing;

        var first = await sync.FirstPassAsync();
        var second = await sync.NextPassAsync();

        first.JobsSubmitted.ShouldBe(2);
        first.JobsOwed.ShouldBe(1);
        second.JobsSubmitted.ShouldBe(0);
        sync.Gateway.Jobs.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_schema_can_keep_fewer_roots_than_the_rest()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.Keeping($"{Es},{Mnq}", $"{Mbo},{Definition}")
            .With(SyncScenario.Sync($"{nameof(SyncOptions.RootsBySchema)}:{Mbo}"), Mnq);

        await sync.FirstPassAsync();

        sync.Gateway.Jobs.Where(j => j.Spec.Schema == Mbo).ShouldHaveSingleItem().Spec.Root.ShouldBe(Mnq);
        sync.Gateway.Jobs.Count(j => j.Spec.Schema == Definition).ShouldBe(2);
    }

    [Fact]
    public async Task Everything_lands_under_the_store_prefix()
    {
        await using var sync = await SyncScenario.CreateAsync(s3, NestedPrefix);

        await sync.FirstPassAsync();
        await sync.NextPassAsync();

        var keys = await sync.Archive.KeysAsync();
        keys.ShouldContain(ArchiveKeys.Data(Dataset, Mbo, Mnq, FirstDay));
        keys.ShouldContain(k => k.StartsWith(ArchiveKeys.PassesFolder, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Nothing_of_a_stored_job_is_left_on_the_scratch_volume()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);

        await sync.FirstPassAsync();
        await sync.NextPassAsync();

        Directory.GetFileSystemEntries(sync.ScratchPath).Select(Path.GetFileName)
            .Where(name => name != KeysFolder).ShouldBeEmpty();
    }

    [Fact]
    public async Task Every_request_carries_the_key_and_every_pass_is_recorded_with_its_counts()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);

        await sync.FirstPassAsync();
        await sync.NextPassAsync();

        sync.Gateway.Calls.ShouldAllBe(c => c.Authorization != null);
        var passes = await sync.Archive.PassesAsync();
        passes.Sum(p => p.JobsSubmitted).ShouldBe(1);
        passes.Sum(p => p.FilesStored).ShouldBe(TradingDays.Length);
        passes.Sum(p => p.BytesStored).ShouldBe(sync.Gateway.Jobs.Single().Files
            .Where(f => f.Name.EndsWith(ArchiveKeys.DataSuffix, StringComparison.Ordinal)).Sum(f => f.Bytes.Length));
    }

    [Fact]
    public async Task A_schema_s_own_roots_apply_however_its_name_is_spelled()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.Keeping($"{Es},{Mnq}", Mbo)
            .With(SyncScenario.Sync($"{nameof(SyncOptions.RootsBySchema)}:{Mbo.ToUpperInvariant()}"), Mnq);

        await sync.FirstPassAsync();

        sync.Gateway.Jobs.ShouldHaveSingleItem().Spec.Root.ShouldBe(Mnq);
    }

    [Fact]
    public async Task A_file_big_enough_to_upload_in_parts_is_stored_byte_for_byte()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.Gateway.NewJobState = JobStates.Processing;
        await sync.FirstPassAsync();
        var job = sync.Gateway.Jobs.Single();
        var name = BatchFactory.FileName(Mbo, Monday);
        var big = new byte[MultipartBytes];
        new Random(Seed).NextBytes(big);
        job.Files[job.Files.FindIndex(f => f.Name == name)] = new FakeFile(name, big);
        job.State = JobStates.Done;

        var stored = await sync.NextPassAsync();

        stored.JobsCollected.ShouldBe(1);
        (await sync.Archive.ReadAsync(ArchiveKeys.Data(Dataset, Mbo, Mnq, Monday))).ShouldBe(big);
    }
}
