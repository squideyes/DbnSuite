using System.Net;
using System.Runtime.CompilerServices;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace DbnClient.Archive;

/// <summary>An archive in an S3 bucket: RustFS, MinIO, AWS, DigitalOcean Spaces.</summary>
public sealed class S3ObjectStore : IObjectStore, IDisposable
{
    private const string Delimiter = "/";

    private readonly AmazonS3Client s3;
    private readonly string bucket;
    private readonly string prefix;

    public S3ObjectStore(ArchiveOptions options)
    {
        s3 = new AmazonS3Client(
            new BasicAWSCredentials(options.AccessKey, options.SecretKey),
            new AmazonS3Config
            {
                ServiceURL = options.Location,
                ForcePathStyle = true,
                AuthenticationRegion = options.Region
            });

        bucket = options.Bucket;
        prefix = options.Prefix.Trim('/') is { Length: > 0 } p ? p + Delimiter : "";
    }

    public async IAsyncEnumerable<string> FoldersAsync(string folder,
        [EnumeratorCancellation] CancellationToken cancel)
    {
        var request = new ListObjectsV2Request { BucketName = bucket, Prefix = prefix + folder, Delimiter = Delimiter };

        do
        {
            var page = await Call(() => s3.ListObjectsV2Async(request, cancel)).ConfigureAwait(false);

            foreach (var common in page.CommonPrefixes ?? [])
                yield return common[prefix.Length..];

            request.ContinuationToken = page.NextContinuationToken;
        }
        while (request.ContinuationToken is not null);
    }

    public async IAsyncEnumerable<StoredObject> ObjectsAsync(string folder,
        [EnumeratorCancellation] CancellationToken cancel)
    {
        var request = new ListObjectsV2Request { BucketName = bucket, Prefix = prefix + folder };

        do
        {
            var page = await Call(() => s3.ListObjectsV2Async(request, cancel)).ConfigureAwait(false);

            foreach (var o in page.S3Objects ?? [])
                yield return new StoredObject(o.Key[prefix.Length..], o.Size ?? 0, o.ETag ?? "");

            request.ContinuationToken = page.NextContinuationToken;
        }
        while (request.ContinuationToken is not null);
    }

    public async Task<StoredObject?> FindAsync(string key, CancellationToken cancel)
    {
        try
        {
            var head = await s3.GetObjectMetadataAsync(bucket, prefix + key, cancel).ConfigureAwait(false);

            return new StoredObject(key, head.ContentLength, head.ETag ?? "");
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (Exception e) when (ArchiveException.Map(e) is { } error)
        {
            throw new ArchiveException(error, e);
        }
    }

    public async Task<Stream> OpenAsync(string key, long offset, CancellationToken cancel)
    {
        var request = new GetObjectRequest { BucketName = bucket, Key = prefix + key };

        if (offset > 0)
            request.ByteRange = new ByteRange(offset, long.MaxValue);

        var response = await Call(() => s3.GetObjectAsync(request, cancel)).ConfigureAwait(false);

        return new OwnedStream(response.ResponseStream, response);
    }

    public void Dispose() => s3.Dispose();

    private static async Task<T> Call<T>(Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (Exception e) when (ArchiveException.Map(e) is { } error)
        {
            throw new ArchiveException(error, e);
        }
    }

    /// <summary>A response stream that disposes the response it came with.</summary>
    private sealed class OwnedStream(Stream inner, IDisposable owner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default) =>
            inner.ReadAsync(buffer, cancel);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancel) =>
            inner.ReadAsync(buffer, offset, count, cancel);

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                owner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
