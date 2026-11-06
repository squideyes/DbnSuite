using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using DbnClient.Archive;

namespace DbnSpool;

/// <summary>Where finished tapes are kept: an S3 bucket such as a DigitalOcean Space, or a folder.</summary>
public interface ITapeShelf
{
    Task EnsureReadyAsync(CancellationToken cancel);

    /// <summary>Puts a whole file on the shelf, replacing any object of that key.</summary>
    Task PutFileAsync(string key, string path, CancellationToken cancel);

    Task PutBytesAsync(string key, byte[] bytes, CancellationToken cancel);

    /// <summary>An object's bytes, or null when there is none.</summary>
    Task<byte[]?> ReadAsync(string key, CancellationToken cancel);
}

/// <summary>A shelf in an S3 bucket, spoken to the way DbnSync speaks to stores that aren't AWS.</summary>
public sealed class S3TapeShelf(ArchiveOptions options) : ITapeShelf, IDisposable
{
    private const string Delimiter = "/";

    private readonly AmazonS3Client s3 = new(
        new BasicAWSCredentials(options.AccessKey, options.SecretKey),
        new AmazonS3Config
        {
            ServiceURL = options.Location,
            ForcePathStyle = true,
            AuthenticationRegion = options.Region,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
        });

    private readonly string prefix = options.Prefix.Trim('/') is { Length: > 0 } p ? p + Delimiter : "";

    public async Task EnsureReadyAsync(CancellationToken cancel)
    {
        if (!await AmazonS3Util.DoesS3BucketExistV2Async(s3, options.Bucket).ConfigureAwait(false))
            await s3.PutBucketAsync(new PutBucketRequest { BucketName = options.Bucket }, cancel).ConfigureAwait(false);
    }

    public Task PutFileAsync(string key, string path, CancellationToken cancel) =>
        s3.PutObjectAsync(new PutObjectRequest { BucketName = options.Bucket, Key = prefix + key, FilePath = path }, cancel);

    public async Task PutBytesAsync(string key, byte[] bytes, CancellationToken cancel)
    {
        using var body = new MemoryStream(bytes);

        await s3.PutObjectAsync(new PutObjectRequest { BucketName = options.Bucket, Key = prefix + key, InputStream = body }, cancel)
            .ConfigureAwait(false);
    }

    public async Task<byte[]?> ReadAsync(string key, CancellationToken cancel)
    {
        try
        {
            using var response = await s3.GetObjectAsync(options.Bucket, prefix + key, cancel).ConfigureAwait(false);
            using var bytes = new MemoryStream();

            await response.ResponseStream.CopyToAsync(bytes, cancel).ConfigureAwait(false);

            return bytes.ToArray();
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public void Dispose() => s3.Dispose();
}

/// <summary>A shelf in a folder, under the same keys.</summary>
public sealed class FolderTapeShelf(string folder) : ITapeShelf
{
    public Task EnsureReadyAsync(CancellationToken cancel)
    {
        Directory.CreateDirectory(folder);

        return Task.CompletedTask;
    }

    public Task PutFileAsync(string key, string path, CancellationToken cancel)
    {
        File.Copy(path, Prepare(key), overwrite: true);

        return Task.CompletedTask;
    }

    public Task PutBytesAsync(string key, byte[] bytes, CancellationToken cancel) =>
        File.WriteAllBytesAsync(Prepare(key), bytes, cancel);

    public async Task<byte[]?> ReadAsync(string key, CancellationToken cancel)
    {
        var path = PathOf(key);

        return File.Exists(path) ? await File.ReadAllBytesAsync(path, cancel).ConfigureAwait(false) : null;
    }

    private string PathOf(string key) => Path.Combine(folder, key.Replace('/', Path.DirectorySeparatorChar));

    private string Prepare(string key)
    {
        var path = PathOf(key);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        return path;
    }
}
