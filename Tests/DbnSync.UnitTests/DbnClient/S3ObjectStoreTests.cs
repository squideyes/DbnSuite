using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using DbnClient;
using DbnClient.Archive;
using DbnSuite.Testing;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Shouldly;
using static DbnSync.UnitTests.TestData;

namespace DbnSync.UnitTests.ClientTests;

/// <summary>The library's reads against a real S3 server, laid out as the worker writes it.</summary>
public sealed class S3ObjectStoreTests : IAsyncLifetime
{
    private const string Image = "rustfs/rustfs:1.0.4";
    private const string AccessKey = "dbnsync-tests";
    private const string SecretKey = "dbnsync-tests-secret";
    private const string WrongSecret = "not-the-secret";
    private const string Bucket = "dbn";
    private const string Prefix = "archive";
    private const int S3Port = 9000;

    private static readonly byte[] Content = [1, 2, 3, 4, 5];

    private readonly IContainer server = new ContainerBuilder(Image)
        .WithEnvironment("RUSTFS_ACCESS_KEY", AccessKey)
        .WithEnvironment("RUSTFS_SECRET_KEY", SecretKey)
        .WithPortBinding(S3Port, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(r => r.ForPort(S3Port).ForPath("/health"))
            .AddCustomWaitStrategy(new S3Ready(AccessKey, SecretKey, S3Port)))
        .Build();

    private readonly string cache = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    private string endpoint = "";

    public async Task InitializeAsync()
    {
        await server.StartAsync();

        endpoint = $"http://{server.Hostname}:{server.GetMappedPublicPort(S3Port)}";

        using var s3 = new AmazonS3Client(new BasicAWSCredentials(AccessKey, SecretKey),
            new AmazonS3Config { ServiceURL = endpoint, ForcePathStyle = true, AuthenticationRegion = "us-east-1" });

        await s3.PutBucketAsync(Bucket);
        await PutAsync(s3, DbnSyncLayout.Data(Dataset, Mbo, Root, Day(10, 30)), Content);
        await PutAsync(s3, DbnSyncLayout.Empty(Dataset, Mbo, Root, Day(10, 31)), []);
        await PutAsync(s3, DbnSyncLayout.Data(Dataset, Mbo, Root, Day(11, 2)), Content);
        await PutAsync(s3, DbnSyncLayout.Data(Dataset, Definition, OtherRoot, Day(10, 30)), Content);
    }

    public async Task DisposeAsync()
    {
        await server.DisposeAsync();

        if (Directory.Exists(cache))
            Directory.Delete(cache, recursive: true);
    }

    private static Task PutAsync(IAmazonS3 s3, string key, byte[] bytes) => s3.PutObjectAsync(new PutObjectRequest
    {
        BucketName = Bucket,
        Key = $"{Prefix}/{key}",
        InputStream = new MemoryStream(bytes)
    });

    private DbnArchive Archive(string secret = SecretKey) => DbnArchive.Open(new ArchiveOptions
    {
        Location = endpoint,
        Bucket = Bucket,
        Prefix = Prefix,
        AccessKey = AccessKey,
        SecretKey = secret
    }, cache);

    [Fact]
    public async Task The_layout_is_recognised_under_a_prefix()
    {
        using var archive = Archive();

        (await archive.LayoutAsync(CancellationToken.None)).Value.ShouldBe(DbnSyncLayout.Name);
    }

    [Fact]
    public async Task Every_series_is_listed_with_its_files_and_empty_days()
    {
        using var archive = Archive();

        var series = (await archive.ListSeriesAsync(CancellationToken.None)).Value;

        series.ShouldBe([
            new SeriesSummary(Definition, OtherRoot, 1, 0, Content.Length, Day(10, 30), Day(10, 30)),
            new SeriesSummary(Mbo, Root, 2, 1, 2 * Content.Length, Day(10, 30), Day(11, 2))
        ], ignoreOrder: true);
    }

    [Fact]
    public async Task A_day_is_fetched_into_the_cache_once()
    {
        using var archive = Archive();

        var path = (await archive.FetchAsync(Mbo, Root, Day(10, 30), null, CancellationToken.None)).Value;
        var days = (await archive.ListDaysAsync(Mbo, Root, CancellationToken.None)).Value;

        (await File.ReadAllBytesAsync(path)).ShouldBe(Content);
        path.ShouldStartWith(cache);
        archive.Cache!.Holds(days[0].Object).ShouldBeTrue();
    }

    [Fact]
    public async Task A_day_with_no_data_says_so()
    {
        using var archive = Archive();

        (await archive.FetchAsync(Mbo, Root, Day(10, 31), null, CancellationToken.None)).Error!.Code.ShouldBe(Errors.NoData);
    }

    [Fact]
    public async Task A_wrong_secret_is_refused_as_such()
    {
        using var archive = Archive(WrongSecret);

        (await archive.ListSeriesAsync(CancellationToken.None)).Error!.Code.ShouldBe(Errors.Denied);
    }

    [Fact]
    public async Task A_wrong_secret_is_refused_on_a_single_object_too()
    {
        using var archive = Archive(WrongSecret);

        (await archive.FetchAsync(Mbo, Root, Day(10, 30), null, CancellationToken.None)).Error!.Code.ShouldBe(Errors.Denied);
    }

    [Fact]
    public async Task A_missing_bucket_is_refused_as_such()
    {
        using var store = Store(bucket: "no-such-bucket");

        await Should.ThrowAsync<ArchiveException>(async () => await store.FoldersAsync("", CancellationToken.None).ToListAsync());
    }

    [Fact]
    public async Task An_endpoint_nothing_answers_on_is_unreachable()
    {
        using var nothing = DbnArchive.Open(new ArchiveOptions { Location = "http://127.0.0.1:1", AccessKey = AccessKey, SecretKey = SecretKey }, cache);

        (await nothing.ListSeriesAsync(CancellationToken.None)).Error!.Code.ShouldBe(Errors.Unreachable);
        (await nothing.FetchAsync(Mbo, Root, Day(10, 30), null, CancellationToken.None)).Error!.Code.ShouldBe(Errors.Unreachable);
    }

    [Fact]
    public async Task A_prefix_with_stray_slashes_is_the_same_prefix()
    {
        using var store = Store(prefix: $"/{Prefix}/");

        var folders = await store.FoldersAsync("", CancellationToken.None).ToListAsync();

        folders.ShouldBe([$"{Dataset}/"]);
    }

    [Fact]
    public async Task With_no_prefix_keys_start_at_the_bucket()
    {
        using var store = Store(prefix: "");

        var folders = await store.FoldersAsync("", CancellationToken.None).ToListAsync();

        folders.ShouldBe([$"{Prefix}/"]);
    }

    [Fact]
    public async Task Objects_are_listed_relative_to_the_prefix_with_their_sizes_and_etags()
    {
        using var store = Store();

        var objects = await store.ObjectsAsync(DbnSyncLayout.Folder(Dataset, Definition, OtherRoot), CancellationToken.None).ToListAsync();

        var stored = objects.ShouldHaveSingleItem();
        stored.Key.ShouldBe(DbnSyncLayout.Data(Dataset, Definition, OtherRoot, Day(10, 30)));
        stored.Size.ShouldBe(Content.Length);
        stored.ETag.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task One_object_is_described_or_is_not_there()
    {
        using var store = Store();

        (await store.FindAsync(DbnSyncLayout.Data(Dataset, Mbo, Root, Day(10, 30)), CancellationToken.None))!.Size.ShouldBe(Content.Length);
        (await store.FindAsync("no/such/key", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task An_object_reads_from_the_start_or_from_an_offset()
    {
        using var store = Store();
        var key = DbnSyncLayout.Data(Dataset, Mbo, Root, Day(10, 30));

        await using var whole = await store.OpenAsync(key, 0, CancellationToken.None);
        await using var rest = await store.OpenAsync(key, 3, CancellationToken.None);

        (await ReadAll(whole)).ShouldBe(Content);
        (await ReadAll(rest)).ShouldBe(Content[3..]);
    }

    private static async Task<byte[]> ReadAll(Stream stream)
    {
        using var target = new MemoryStream();

        await stream.CopyToAsync(target);

        return target.ToArray();
    }

    [Fact]
    public async Task An_object_that_is_not_there_is_not_an_expected_failure_to_open()
    {
        using var store = Store();

        await Should.ThrowAsync<AmazonS3Exception>(() => store.OpenAsync("no/such/key", 0, CancellationToken.None));
    }

    [Fact]
    public async Task An_open_object_reads_every_way_a_stream_can()
    {
        using var store = Store();

        await using var stream = await store.OpenAsync(DbnSyncLayout.Data(Dataset, Mbo, Root, Day(10, 30)), 0, CancellationToken.None);
        var first = new byte[1];
        var second = new byte[1];
        var third = new byte[1];

        (await stream.ReadAsync(first.AsMemory(), CancellationToken.None)).ShouldBe(1);
        (await stream.ReadAsync(second, 0, 1, CancellationToken.None)).ShouldBe(1);
        stream.Read(third, 0, 1).ShouldBe(1);

        new[] { first[0], second[0], third[0] }.ShouldBe(Content[..3]);
    }

    [Fact]
    public async Task An_open_object_only_reads_forward()
    {
        using var store = Store();

        await using var stream = await store.OpenAsync(DbnSyncLayout.Data(Dataset, Mbo, Root, Day(10, 30)), 0, CancellationToken.None);

        stream.CanRead.ShouldBeTrue();
        stream.CanSeek.ShouldBeFalse();
        stream.CanWrite.ShouldBeFalse();
        stream.Length.ShouldBe(Content.Length);
        Should.Throw<NotSupportedException>(() => stream.Position);
        stream.Flush();
        Should.Throw<NotSupportedException>(() => stream.Position = 1);
        Should.Throw<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Should.Throw<NotSupportedException>(() => stream.SetLength(0));
        Should.Throw<NotSupportedException>(() => stream.Write(Content, 0, 1));
    }

    [Fact]
    public async Task A_folder_with_nothing_in_it_lists_nothing()
    {
        using var store = Store();

        (await store.FoldersAsync("no-such-folder/", CancellationToken.None).ToListAsync()).ShouldBeEmpty();
        (await store.ObjectsAsync("no-such-folder/", CancellationToken.None).ToListAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_listing_longer_than_a_page_is_followed_to_its_end()
    {
        const int MoreThanAPage = 1001;
        const string Folder = "paged/";
        using var s3 = new AmazonS3Client(new BasicAWSCredentials(AccessKey, SecretKey),
            new AmazonS3Config { ServiceURL = endpoint, ForcePathStyle = true, AuthenticationRegion = "us-east-1" });

        await Parallel.ForEachAsync(Enumerable.Range(0, MoreThanAPage), new ParallelOptions { MaxDegreeOfParallelism = 16 },
            async (i, cancel) => await PutAsync(s3, $"{Folder}{i}/x", []));
        using var store = Store();

        (await store.ObjectsAsync(Folder, CancellationToken.None).ToListAsync()).Count.ShouldBe(MoreThanAPage);
        (await store.FoldersAsync(Folder, CancellationToken.None).ToListAsync()).Count.ShouldBe(MoreThanAPage);
    }

    private S3ObjectStore Store(string bucket = Bucket, string prefix = Prefix) => new(new ArchiveOptions
    {
        Location = endpoint,
        Bucket = bucket,
        Prefix = prefix,
        AccessKey = AccessKey,
        SecretKey = SecretKey
    });
}
