using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Text;

namespace DbnSuite.UnitTests.FetchTests;

/// <summary>
/// Just enough of S3, on a loopback port, to serve a folder as a bucket: list,
/// head and ranged get, with the faults a real endpoint shows. It lets tests
/// reach what dbnf does only for an S3 archive without any network.
/// </summary>
internal sealed class FakeS3 : IDisposable
{
    public const string Bucket = "dbn";

    private const string Denied = "<?xml version=\"1.0\"?><Error><Code>InvalidAccessKeyId</Code><Message>No such key.</Message></Error>";
    private const string NoBucket = "<?xml version=\"1.0\"?><Error><Code>NoSuchBucket</Code><Message>No such bucket.</Message></Error>";
    private const string LineEnd = "\r\n";
    private const string RangeHeader = "Range: bytes=";

    private readonly string root;
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stopping = new();
    private readonly ConcurrentQueue<string> downloads = new();
    private readonly ConcurrentQueue<long> offsets = new();

    /// <param name="root">The folder the bucket's keys are relative to.</param>
    public FakeS3(string root)
    {
        this.root = root;

        listener.Start();
        _ = AcceptAsync();
    }

    public string Location => $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";

    /// <summary>Refuse every key, as S3 does a bad one.</summary>
    public bool Deny { get; set; }

    /// <summary>Never answer a request for the endpoint itself, as a stalled proxy does.</summary>
    public bool SilentAtRoot { get; set; }

    /// <summary>Changed to make every object look replaced.</summary>
    public string Version { get; set; } = "1";

    /// <summary>Hang up after this many bytes of an object, as a dropped connection does.</summary>
    public int? DropAfter { get; set; }

    /// <summary>What the Date header says, against this PC's clock; null leaves it out.</summary>
    public TimeSpan? ClockOffset { get; set; } = TimeSpan.Zero;

    /// <summary>The keys downloaded, one entry per request.</summary>
    public IReadOnlyList<string> Downloads => [.. downloads];

    /// <summary>Where each download asked to start.</summary>
    public IReadOnlyList<long> Offsets => [.. offsets];

    public void Dispose()
    {
        stopping.Cancel();
        listener.Stop();
        stopping.Dispose();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                var client = await listener.AcceptTcpClientAsync(stopping.Token);

                _ = Task.Run(() => ServeAsync(client));
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Stopped.
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var head = await ReadHeadAsync(stream);
                var parts = head[0].Split(' ');

                await RespondAsync(stream, parts[0], parts[1], head);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException)
            {
                // The client hung up, as it may, or the test is over.
            }
        }
    }

    private static async Task<string[]> ReadHeadAsync(NetworkStream stream)
    {
        var text = new StringBuilder();
        var buffer = new byte[1];

        while (!text.ToString().EndsWith(LineEnd + LineEnd, StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(buffer) == 0)
                throw new IOException("The client left.");

            text.Append((char)buffer[0]);
        }

        return text.ToString().Split(LineEnd, StringSplitOptions.RemoveEmptyEntries);
    }

    private async Task RespondAsync(NetworkStream stream, string method, string target, string[] head)
    {
        var parts = target.Split('?', 2);
        var path = Uri.UnescapeDataString(parts[0]).TrimStart('/');
        var query = parts.Length > 1 ? Query(parts[1]) : [];

        if (path.Length == 0)
        {
            if (SilentAtRoot)
                await Task.Delay(Timeout.Infinite, stopping.Token);

            await SendAsync(stream, 200, method, [], []);
            return;
        }

        if (Deny)
        {
            await SendAsync(stream, 403, method, [], Encoding.UTF8.GetBytes(Denied));
            return;
        }

        if (path.Split('/')[0] != Bucket)
        {
            await SendAsync(stream, 404, method, [], Encoding.UTF8.GetBytes(NoBucket));
            return;
        }

        if (path.TrimEnd('/') == Bucket && query.ContainsKey("list-type"))
        {
            await SendAsync(stream, 200, method, [], Encoding.UTF8.GetBytes(List(query)));
            return;
        }

        var key = path[(Bucket.Length + 1)..];
        var file = Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(file))
        {
            await SendAsync(stream, 404, method, [], []);
            return;
        }

        var bytes = await File.ReadAllBytesAsync(file);
        var etag = ETag(bytes);

        if (method == "HEAD")
        {
            await SendAsync(stream, 200, method, [$"ETag: {etag}"], [], bytes.Length);
            return;
        }

        var offset = Offset(head);

        downloads.Enqueue(key);
        offsets.Enqueue(offset);

        var body = bytes[(int)offset..];

        if (DropAfter is { } drop)
        {
            await SendAsync(stream, 200, method, [$"ETag: {etag}"], body[..drop], body.Length);
            return;
        }

        string[] headers = offset > 0
            ? [$"ETag: {etag}", $"Content-Range: bytes {offset}-{bytes.Length - 1}/{bytes.Length}"]
            : [$"ETag: {etag}"];

        await SendAsync(stream, offset > 0 ? 206 : 200, method, headers, body);
    }

    private string List(Dictionary<string, string> query)
    {
        var prefix = query.GetValueOrDefault("prefix", "");
        var delimited = query.ContainsKey("delimiter");
        var contents = new StringBuilder();
        var folders = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var key = Path.GetRelativePath(root, path).Replace('\\', '/');

            if (!key.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            var slash = key.IndexOf('/', prefix.Length);

            if (delimited && slash >= 0)
            {
                folders.Add(key[..(slash + 1)]);
                continue;
            }

            contents.Append(CultureInfo.InvariantCulture,
                $"<Contents><Key>{Xml(key)}</Key><LastModified>2026-11-01T00:00:00.000Z</LastModified><ETag>{Xml(ETag(File.ReadAllBytes(path)))}</ETag><Size>{new FileInfo(path).Length}</Size><StorageClass>STANDARD</StorageClass></Contents>");
        }

        var common = string.Concat(folders.Select(f => $"<CommonPrefixes><Prefix>{Xml(f)}</Prefix></CommonPrefixes>"));

        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?><ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\">" +
            $"<Name>{Bucket}</Name><Prefix>{Xml(prefix)}</Prefix><MaxKeys>1000</MaxKeys><IsTruncated>false</IsTruncated>{contents}{common}</ListBucketResult>";
    }

    private async Task SendAsync(NetworkStream stream, int status, string method, string[] headers, byte[] body, long? length = null)
    {
        var text = new StringBuilder($"HTTP/1.1 {status} {(status < 300 ? "OK" : "No")}{LineEnd}");

        if (ClockOffset is { } offset)
            text.Append(CultureInfo.InvariantCulture, $"Date: {(DateTimeOffset.UtcNow + offset).ToString("R", CultureInfo.InvariantCulture)}{LineEnd}");

        text.Append(CultureInfo.InvariantCulture, $"Content-Length: {length ?? body.Length}{LineEnd}Connection: close{LineEnd}");

        foreach (var header in headers)
            text.Append(header).Append(LineEnd);

        text.Append(LineEnd);

        await stream.WriteAsync(Encoding.ASCII.GetBytes(text.ToString()));

        if (method != "HEAD")
            await stream.WriteAsync(body);

        await stream.FlushAsync();
    }

    private string ETag(byte[] bytes) => $"\"{Version}-{bytes.Length}\"";

    private static long Offset(string[] head)
    {
        var range = head.FirstOrDefault(h => h.StartsWith(RangeHeader, StringComparison.OrdinalIgnoreCase));

        return range is null
            ? 0
            : long.Parse(range[RangeHeader.Length..].Split('-')[0], CultureInfo.InvariantCulture);
    }

    private static Dictionary<string, string> Query(string text) =>
        text.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : "");

    private static string Xml(string text) => SecurityElement.Escape(text);
}
