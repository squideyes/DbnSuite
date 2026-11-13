using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using Amazon.S3.Util;
using DbnSuite.Sync.Core;
using Microsoft.Extensions.Options;

namespace DbnSuite.Sync.Data;

/// <summary>
/// The archive and its job ledger, in one bucket, through the S3 API only.
/// <para>
/// Through the API and never the disk underneath: MinIO keeps its own
/// on-disk format, so a file copied into its data folder by hand is not an
/// object and never will be. Everything that reads the archive should go
/// through S3 too.
/// </para>
/// </summary>
public sealed class S3ArchiveStore(IAmazonS3 s3, IOptions<StoreOptions> store, SyncOptions sync)
    : IArchiveStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly StoreOptions settings = store.Value;

    /// <summary>
    /// A client that speaks to non-AWS stores: path-style addressing, and
    /// checksums only where the operation requires one. SDK v4 adds flexible
    /// checksums to every upload by default, which older MinIO and Garage
    /// releases reject.
    /// </summary>
    public static IAmazonS3 CreateClient(StoreOptions options) => new AmazonS3Client(
        new BasicAWSCredentials(options.AccessKey, options.SecretKey),
        new AmazonS3Config
        {
            ServiceURL = options.ServiceUrl,
            ForcePathStyle = true,
            AuthenticationRegion = options.Region,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
        });

    public async Task EnsureBucketAsync(CancellationToken cancel)
    {
        if (!await AmazonS3Util.DoesS3BucketExistV2Async(s3, settings.Bucket).ConfigureAwait(false))
        {
            await s3.PutBucketAsync(new PutBucketRequest { BucketName = settings.Bucket }, cancel)
                .ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlySet<DateOnly>> ListHeldAsync(string schema, string root,
        CancellationToken cancel) =>
        (await ListDaysAsync(schema, root, cancel).ConfigureAwait(false))
            .Select(d => d.Day)
            .ToHashSet();

    public async Task<IReadOnlyList<StoredDay>> ListDaysAsync(string schema, string root,
        CancellationToken cancel)
    {
        var days = new List<StoredDay>();

        await foreach (var (key, size) in ListAsync(ArchiveKeys.Folder(sync.Dataset, schema, root), cancel)
            .ConfigureAwait(false))
        {
            if (ArchiveKeys.DayOf(key) is { } day)
                days.Add(new StoredDay(day, size, key.EndsWith(ArchiveKeys.EmptySuffix, StringComparison.Ordinal)));
        }

        return days;
    }

    public Task SavePassAsync(PassSummary pass, CancellationToken cancel) =>
        s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = settings.Bucket,
            Key = settings.Key(ArchiveKeys.Pass(pass.StartedAt)),
            ContentBody = JsonSerializer.Serialize(pass, Json),
            ContentType = "application/json"
        }, cancel);

    public async Task<IReadOnlyList<PassSummary>> ListPassesAsync(DateOnly since, DateOnly until,
        CancellationToken cancel)
    {
        var passes = new List<PassSummary>();

        for (var day = until; day >= since; day = day.AddDays(-1))
        {
            await foreach (var (key, _) in ListAsync(ArchiveKeys.PassDay(day), cancel).ConfigureAwait(false))
            {
                if (await ReadAsync<PassSummary>(key, cancel).ConfigureAwait(false) is { } pass)
                    passes.Add(pass);
            }
        }

        return [.. passes.OrderByDescending(p => p.StartedAt)];
    }

    public async Task PutFileAsync(string schema, string root, DateOnly day, string path,
        CancellationToken cancel)
    {
        using var transfer = new TransferUtility(s3);

        await transfer.UploadAsync(new TransferUtilityUploadRequest
        {
            BucketName = settings.Bucket,
            Key = settings.Key(ArchiveKeys.Data(sync.Dataset, schema, root, day)),
            FilePath = path,
            ContentType = "application/octet-stream"
        }, cancel).ConfigureAwait(false);
    }

    public Task PutEmptyAsync(string schema, string root, DateOnly day, CancellationToken cancel) =>
        s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = settings.Bucket,
            Key = settings.Key(ArchiveKeys.Empty(sync.Dataset, schema, root, day)),
            ContentBody = ""
        }, cancel);

    public async Task<IReadOnlyList<PendingJob>> ListPendingAsync(CancellationToken cancel)
    {
        var jobs = new List<PendingJob>();

        await foreach (var (key, _) in ListAsync(ArchiveKeys.JobsFolder, cancel).ConfigureAwait(false))
        {
            if (await ReadAsync<PendingJob>(key, cancel).ConfigureAwait(false) is { } job)
                jobs.Add(job);
        }

        return [.. jobs.OrderBy(j => j.SubmittedAt)];
    }

    private async Task<T?> ReadAsync<T>(string key, CancellationToken cancel)
    {
        try
        {
            using var response = await s3.GetObjectAsync(settings.Bucket, settings.Key(key), cancel)
                .ConfigureAwait(false);

            return await JsonSerializer.DeserializeAsync<T>(response.ResponseStream, Json, cancel)
                .ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Deleted between the listing and the read.
            return default;
        }
    }

    public Task SavePendingAsync(PendingJob job, CancellationToken cancel) =>
        s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = settings.Bucket,
            Key = settings.Key(ArchiveKeys.Job(job.Id)),
            ContentBody = JsonSerializer.Serialize(job, Json),
            ContentType = "application/json"
        }, cancel);

    public Task DeletePendingAsync(string jobId, CancellationToken cancel) =>
        s3.DeleteObjectAsync(settings.Bucket, settings.Key(ArchiveKeys.Job(jobId)), cancel);

    public async Task<IReadOnlyList<SpendEntry>> ListSpendAsync(DateOnly month, CancellationToken cancel)
    {
        var entries = new List<SpendEntry>();

        await foreach (var (key, _) in ListAsync(ArchiveKeys.SpendMonth(month), cancel).ConfigureAwait(false))
        {
            if (await ReadAsync<SpendEntry>(key, cancel).ConfigureAwait(false) is { } entry)
                entries.Add(entry);
        }

        return [.. entries.OrderBy(e => e.At)];
    }

    public Task SaveSpendAsync(SpendEntry entry, CancellationToken cancel) =>
        s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = settings.Bucket,
            Key = settings.Key(ArchiveKeys.Spend(entry)),
            ContentBody = JsonSerializer.Serialize(entry, Json),
            ContentType = "application/json"
        }, cancel);

    public Task DeleteSpendAsync(SpendEntry entry, CancellationToken cancel) =>
        s3.DeleteObjectAsync(settings.Bucket, settings.Key(ArchiveKeys.Spend(entry)), cancel);

    /// <summary>
    /// Every key under a folder, relative to the prefix, with its size.
    /// Paginated: one listing stops at 1,000 keys, and a root's L0 history is
    /// thousands of days.
    /// </summary>
    private async IAsyncEnumerable<(string Key, long Size)> ListAsync(string folder,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancel)
    {
        var request = new ListObjectsV2Request { BucketName = settings.Bucket, Prefix = settings.Key(folder) };
        var strip = settings.Key("").Length;

        do
        {
            var page = await s3.ListObjectsV2Async(request, cancel).ConfigureAwait(false);

            foreach (var item in page.S3Objects ?? [])
                yield return (item.Key[strip..], item.Size ?? 0);

            request.ContinuationToken = page.IsTruncated == true ? page.NextContinuationToken : null;
        }
        while (request.ContinuationToken is not null);
    }
}
