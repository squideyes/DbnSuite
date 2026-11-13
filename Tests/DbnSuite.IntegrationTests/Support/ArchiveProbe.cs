using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using DbnSuite.Sync.Core;

namespace DbnSuite.IntegrationTests.Support;

/// <summary>Reads and tampers with a bucket straight through S3, past everything under test.</summary>
public sealed class ArchiveProbe(RustFs s3, string bucket, string prefix = "") : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly AmazonS3Client client = new(new BasicAWSCredentials(RustFs.AccessKey, RustFs.SecretKey),
        new AmazonS3Config
        {
            ServiceURL = s3.Endpoint,
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
        });

    public string Bucket { get; } = bucket;

    public string Prefix { get; } = prefix;

    public void Dispose() => client.Dispose();

    /// <summary>Every key under a folder, relative to the archive's prefix.</summary>
    public async Task<IReadOnlyList<string>> KeysAsync(string folder = "")
    {
        var keys = new List<string>();
        var request = new ListObjectsV2Request { BucketName = Bucket, Prefix = Key(folder) };

        do
        {
            var page = await client.ListObjectsV2Async(request);

            keys.AddRange((page.S3Objects ?? []).Select(o => o.Key[Key("").Length..]));

            request.ContinuationToken = page.IsTruncated == true ? page.NextContinuationToken : null;
        }
        while (request.ContinuationToken is not null);

        return keys;
    }

    public async Task<byte[]> ReadAsync(string key)
    {
        using var response = await client.GetObjectAsync(Bucket, Key(key));
        using var copy = new MemoryStream();

        await response.ResponseStream.CopyToAsync(copy);

        return copy.ToArray();
    }

    public async Task<IReadOnlyList<T>> ReadAllAsync<T>(string folder)
    {
        var values = new List<T>();

        foreach (var key in await KeysAsync(folder))
            values.Add(JsonSerializer.Deserialize<T>(await ReadAsync(key), Json)!);

        return values;
    }

    /// <summary>The spend ledger, as the bucket holds it right now.</summary>
    public Task<IReadOnlyList<SpendEntry>> SpendAsync() => ReadAllAsync<SpendEntry>(ArchiveKeys.SpendFolder);

    public Task<IReadOnlyList<PendingJob>> PendingAsync() => ReadAllAsync<PendingJob>(ArchiveKeys.JobsFolder);

    public Task<IReadOnlyList<PassSummary>> PassesAsync() => ReadAllAsync<PassSummary>(ArchiveKeys.PassesFolder);

    /// <summary>Writes a ledger entry from before this run.</summary>
    public Task SeedSpendAsync(SpendEntry entry) => PutAsync(ArchiveKeys.Spend(entry), JsonSerializer.Serialize(entry, Json));

    /// <summary>Writes an object straight into the bucket, making the bucket if nothing has yet.</summary>
    public async Task PutAsync(string key, string body)
    {
        try
        {
            await client.PutBucketAsync(Bucket);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            // Already there.
        }

        await client.PutObjectAsync(new PutObjectRequest { BucketName = Bucket, Key = Key(key), ContentBody = body });
    }

    public Task DeleteAsync(string key) => client.DeleteObjectAsync(Bucket, Key(key));

    /// <summary>Empties the bucket and deletes it, as a mistaken cleanup would.</summary>
    public async Task DropBucketAsync()
    {
        foreach (var key in await KeysAsync())
            await DeleteAsync(key);

        await client.DeleteBucketAsync(Bucket);
    }

    private string Key(string relative) => Prefix.Length == 0 ? relative : $"{Prefix}/{relative}";
}
