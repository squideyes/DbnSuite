using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using DbnSync.Core;
using DbnSync.Data;
using DbnSuite.Testing;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using static DbnSync.UnitTests.TestData;

namespace DbnSync.UnitTests.Data;

/// <summary>Against a real S3 server, since pagination and checksums are where stores differ.</summary>
public sealed class S3ArchiveStoreTests : IAsyncLifetime
{
    private const string Image = "rustfs/rustfs:1.0.4";
    private const string AccessKey = "dbnsync-tests";
    private const string SecretKey = "dbnsync-tests-secret";
    private const string Bucket = "dbn";
    private const string Prefix = "archive";
    private const int S3Port = 9000;

    private readonly IContainer server = new ContainerBuilder(Image)
        .WithEnvironment("RUSTFS_ACCESS_KEY", AccessKey)
        .WithEnvironment("RUSTFS_SECRET_KEY", SecretKey)
        .WithPortBinding(S3Port, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(r => r.ForPort(S3Port).ForPath("/health"))
            .AddCustomWaitStrategy(new S3Ready(AccessKey, SecretKey, S3Port)))
        .Build();

    private IAmazonS3 s3 = null!;
    private S3ArchiveStore store = null!;

    public async Task InitializeAsync()
    {
        await server.StartAsync();

        var options = new StoreOptions
        {
            ServiceUrl = $"http://{server.Hostname}:{server.GetMappedPublicPort(S3Port)}",
            Bucket = Bucket,
            Prefix = Prefix,
            AccessKey = AccessKey,
            SecretKey = SecretKey
        };

        s3 = S3ArchiveStore.CreateClient(options);
        store = new S3ArchiveStore(s3, Options.Create(options), new SyncOptions { Dataset = Dataset });

        await store.EnsureBucketAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        s3.Dispose();

        await server.DisposeAsync();
    }

    [Fact]
    public async Task Files_and_empty_markers_both_count_as_held()
    {
        var path = Path.GetTempFileName();
        await System.IO.File.WriteAllBytesAsync(path, [1, 2, 3]);

        await store.PutFileAsync(Mbo, Root, Day(10, 30), path, CancellationToken.None);
        await store.PutEmptyAsync(Mbo, Root, Day(10, 31), CancellationToken.None);
        var held = await store.ListHeldAsync(Mbo, Root, CancellationToken.None);

        held.ShouldBe([Day(10, 30), Day(10, 31)], ignoreOrder: true);
        System.IO.File.Delete(path);
    }

    [Fact]
    public async Task Everything_lands_under_the_prefix()
    {
        await store.PutEmptyAsync(Mbo, Root, Day(10, 31), CancellationToken.None);

        var listing = await s3.ListObjectsV2Async(new() { BucketName = Bucket });

        listing.S3Objects.ShouldAllBe(o => o.Key.StartsWith(Prefix + "/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Held_days_are_read_past_one_listing_page()
    {
        var days = Enumerable.Range(0, 1_005).Select(i => new DateOnly(2020, 1, 1).AddDays(i)).ToList();

        // RustFS sheds load with 503s at 16 at once while other test containers start;
        // the worker itself uploads two files at a time.
        await Parallel.ForEachAsync(days, new ParallelOptions { MaxDegreeOfParallelism = 4 },
            async (day, token) => await store.PutEmptyAsync(Definition, Root, day, token));
        var held = await store.ListHeldAsync(Definition, Root, CancellationToken.None);

        held.Count.ShouldBe(days.Count);
    }

    [Fact]
    public async Task The_job_ledger_round_trips()
    {
        var job = new PendingJob(JobId, Root, Mbo, new DayRange(Day(10, 1), Day(10, 5)), Now);

        await store.SavePendingAsync(job, CancellationToken.None);
        var saved = await store.ListPendingAsync(CancellationToken.None);
        await store.DeletePendingAsync(JobId, CancellationToken.None);
        var after = await store.ListPendingAsync(CancellationToken.None);

        saved.ShouldBe([job]);
        after.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_spend_ledger_keeps_each_month_apart_and_round_trips()
    {
        var range = new DayRange(Day(10, 1), Day(10, 5));
        var reserved = new SpendEntry("20261101120000000-a1", Now, Root, Mbo, range, 2.5m);
        var lastMonth = new SpendEntry("20261031120000000-b2", Now.AddDays(-1), Root, Mbo, range, 4m, "OLDJOB");

        await store.SaveSpendAsync(reserved, CancellationToken.None);
        await store.SaveSpendAsync(reserved with { JobId = JobId }, CancellationToken.None);
        await store.SaveSpendAsync(lastMonth, CancellationToken.None);
        var november = await store.ListSpendAsync(Today, CancellationToken.None);
        await store.DeleteSpendAsync(reserved, CancellationToken.None);
        var after = await store.ListSpendAsync(Today, CancellationToken.None);

        november.ShouldBe([reserved with { JobId = JobId }]);
        after.ShouldBeEmpty();
    }

    [Fact]
    public async Task Stored_days_carry_their_size_and_whether_they_are_empty()
    {
        var path = Path.GetTempFileName();
        await System.IO.File.WriteAllBytesAsync(path, [1, 2, 3]);

        await store.PutFileAsync(Mbo, Root, Day(10, 30), path, CancellationToken.None);
        await store.PutEmptyAsync(Mbo, Root, Day(10, 31), CancellationToken.None);
        var days = await store.ListDaysAsync(Mbo, Root, CancellationToken.None);

        days.ShouldBe([new StoredDay(Day(10, 30), 3, Empty: false), new StoredDay(Day(10, 31), 0, Empty: true)],
            ignoreOrder: true);
        System.IO.File.Delete(path);
    }

    [Fact]
    public async Task Passes_round_trip_with_their_issues_newest_first()
    {
        var issue = new Issue(Now, IssueLevel.Error, Errors.Corrupt, FileName(Day(10, 30), Mbo),
            JobId, Mbo, Root, new DayRange(Day(10, 30), Day(10, 30)));
        var older = Pass(Now.AddDays(-1), []);
        var newer = Pass(Now, [issue]);

        await store.SavePassAsync(older, CancellationToken.None);
        await store.SavePassAsync(newer, CancellationToken.None);
        var passes = await store.ListPassesAsync(Today.AddDays(-1), Today, CancellationToken.None);

        passes.Select(p => p.StartedAt).ShouldBe([newer.StartedAt, older.StartedAt]);
        passes[0].Issues.ShouldBe([issue]);
    }

    [Fact]
    public async Task A_ledger_entry_deleted_between_listing_and_reading_is_skipped()
    {
        var job = new PendingJob(JobId, Root, Mbo, new DayRange(Day(10, 1), Day(10, 5)), Now);
        var racing = Substitute.For<IAmazonS3>();
        racing.ListObjectsV2Async(Arg.Any<ListObjectsV2Request>(), Arg.Any<CancellationToken>())
            .Returns(new ListObjectsV2Response { S3Objects = [new S3Object { Key = ArchiveKeys.Job(job.Id), Size = 1 }] });
        racing.GetObjectAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new AmazonS3Exception("gone") { StatusCode = HttpStatusCode.NotFound });
        var reader = new S3ArchiveStore(racing, Options.Create(new StoreOptions { Bucket = Bucket }), new SyncOptions());

        var pending = await reader.ListPendingAsync(CancellationToken.None);

        pending.ShouldBeEmpty();
    }

    [Fact]
    public async Task Other_failures_reading_the_ledger_are_not_swallowed()
    {
        var failing = Substitute.For<IAmazonS3>();
        failing.ListObjectsV2Async(Arg.Any<ListObjectsV2Request>(), Arg.Any<CancellationToken>())
            .Returns(new ListObjectsV2Response { S3Objects = [new S3Object { Key = ArchiveKeys.Job(JobId), Size = 1 }] });
        failing.GetObjectAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new AmazonS3Exception("denied") { StatusCode = HttpStatusCode.Forbidden });
        var reader = new S3ArchiveStore(failing, Options.Create(new StoreOptions { Bucket = Bucket }), new SyncOptions());

        await Should.ThrowAsync<AmazonS3Exception>(() => reader.ListPendingAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Ensuring_a_bucket_twice_is_harmless()
    {
        await store.EnsureBucketAsync(CancellationToken.None);

        (await store.ListPendingAsync(CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Keys_with_no_day_in_a_series_folder_are_ignored()
    {
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket,
            Key = $"{Prefix}/{ArchiveKeys.Folder(Dataset, Mbo, Root)}notes.txt",
            ContentBody = ""
        });

        var days = await store.ListDaysAsync(Mbo, Root, CancellationToken.None);

        days.ShouldBeEmpty();
    }

    private static PassSummary Pass(DateTimeOffset startedAt, IReadOnlyList<Issue> issues) =>
        new(startedAt, startedAt.AddMinutes(1), 1, 0, 0, 0, 1, 3, 0, 0, issues.Count, null, issues);

    [Fact]
    public async Task Schemas_and_roots_do_not_see_each_others_days()
    {
        await store.PutEmptyAsync(Mbo, Root, Day(10, 31), CancellationToken.None);

        var otherRoot = await store.ListHeldAsync(Mbo, OtherRoot, CancellationToken.None);
        var otherSchema = await store.ListHeldAsync(Trades, Root, CancellationToken.None);

        otherRoot.ShouldBeEmpty();
        otherSchema.ShouldBeEmpty();
    }
}
