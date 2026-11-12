using System.Net;
using System.Runtime.CompilerServices;
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

    /// <summary>Every key under a prefix, however deep.</summary>
    IAsyncEnumerable<string> KeysAsync(string prefix, CancellationToken cancel);

    /// <summary>Deletes an object; one already gone is not an error.</summary>
    Task DeleteAsync(string key, CancellationToken cancel);
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

    public async IAsyncEnumerable<string> KeysAsync(string prefix, [EnumeratorCancellation] CancellationToken cancel)
    {
        var request = new ListObjectsV2Request { BucketName = options.Bucket, Prefix = this.prefix + prefix };

        do
        {
            var page = await s3.ListObjectsV2Async(request, cancel).ConfigureAwait(false);

            foreach (var stored in page.S3Objects ?? [])
                yield return stored.Key[this.prefix.Length..];

            request.ContinuationToken = page.NextContinuationToken;
        }
        while (request.ContinuationToken is not null);
    }

    public Task DeleteAsync(string key, CancellationToken cancel) =>
        s3.DeleteObjectAsync(options.Bucket, prefix + key, cancel);

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

    /// <summary>Replaces the file whole, so a stop part way through never leaves half a ledger behind.</summary>
    public async Task PutBytesAsync(string key, byte[] bytes, CancellationToken cancel)
    {
        var path = Prepare(key);
        var temporary = path + ".tmp";

        await File.WriteAllBytesAsync(temporary, bytes, cancel).ConfigureAwait(false);

        File.Move(temporary, path, overwrite: true);
    }

    public async Task<byte[]?> ReadAsync(string key, CancellationToken cancel)
    {
        var path = PathOf(key);

        return File.Exists(path) ? await File.ReadAllBytesAsync(path, cancel).ConfigureAwait(false) : null;
    }

    public async IAsyncEnumerable<string> KeysAsync(string prefix, [EnumeratorCancellation] CancellationToken cancel)
    {
        await Task.CompletedTask.ConfigureAwait(false);

        var under = PathOf(prefix);

        if (!Directory.Exists(under))
            yield break;

        foreach (var file in Directory.EnumerateFiles(under, "*", SearchOption.AllDirectories))
        {
            cancel.ThrowIfCancellationRequested();

            yield return Path.GetRelativePath(folder, file).Replace(Path.DirectorySeparatorChar, '/');
        }
    }

    public Task DeleteAsync(string key, CancellationToken cancel)
    {
        // As in S3, a key that isn't there is no error.
        if (File.Exists(PathOf(key)))
            File.Delete(PathOf(key));

        return Task.CompletedTask;
    }

    private string PathOf(string key) => Path.Combine(folder, key.Replace('/', Path.DirectorySeparatorChar));

    private string Prepare(string key)
    {
        var path = PathOf(key);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        return path;
    }
}
