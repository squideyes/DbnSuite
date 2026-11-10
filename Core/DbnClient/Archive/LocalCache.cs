using System.Text.Json;
using System.Text.Json.Serialization;

namespace DbnClient.Archive;

/// <summary>What the cache knows of one file: the archive object it was copied from.</summary>
public sealed record CachedFile(long Size, string ETag);

/// <summary>
/// Raw archive files kept on this machine, under the same keys they have in
/// the archive, so each is downloaded once. A download in progress is a
/// <c>.part</c> file that the next attempt continues.
/// </summary>
public sealed class LocalCache
{
    private const string PartSuffix = ".part";
    private const string IndexName = ".dbnsync.json";
    private const int BufferSize = 1 << 20;

    /// <summary>How long a download may go without a byte arriving before it is given up, to carry on next time.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromMinutes(5);

    private readonly Lock gate = new();
    private readonly Dictionary<string, CachedFile> index;
    private readonly Dictionary<string, CachedFile> parts;
    private readonly bool flat;

    /// <param name="flat">Every file in <paramref name="root"/> itself, not in the archive's folders.</param>
    public LocalCache(string root, bool flat = false)
    {
        Root = Path.GetFullPath(root);
        this.flat = flat;
        Directory.CreateDirectory(Root);

        var saved = Load(Path.Combine(Root, IndexName));
        index = saved.Files;
        parts = saved.Parts;
    }

    public string Root { get; }

    /// <summary>Where an object's key puts it, which must be inside the cache: a key comes from the bucket, not from us.</summary>
    public string PathOf(string key)
    {
        var name = flat ? key[(key.LastIndexOf('/') + 1)..] : key.Replace('/', Path.DirectorySeparatorChar);
        var root = Path.GetFullPath(Root);
        var path = Path.GetFullPath(Path.Combine(root, name));
        var inside = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;

        if (!path.StartsWith(inside, StringComparison.Ordinal))
            throw new ArchiveException(new Error(Errors.UnknownLayout, $"The object key {key} is outside the cache."));

        return path;
    }

    /// <summary>True when the cache holds this exact object, so no download is needed.</summary>
    public bool Holds(StoredObject stored)
    {
        var path = PathOf(stored.Key);

        lock (gate)
        {
            return File.Exists(path)
                && new FileInfo(path).Length == stored.Size
                && index.TryGetValue(stored.Key, out var cached)
                && cached.Size == stored.Size
                && cached.ETag == stored.ETag;
        }
    }

    /// <summary>
    /// The local path of <paramref name="stored"/>, downloading it from
    /// <paramref name="store"/> first unless the cache already holds it.
    /// </summary>
    /// <param name="progress">Told the bytes received as they arrive.</param>
    public async Task<string> FetchAsync(IObjectStore store, StoredObject stored, IProgress<long>? progress,
        CancellationToken cancel)
    {
        var path = PathOf(stored.Key);

        if (Holds(stored))
            return path;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var part = path + PartSuffix;
        var offset = ResumeFrom(part, stored);

        Remember(parts, stored.Key, new CachedFile(stored.Size, stored.ETag));

        await using (var source = await store.OpenAsync(stored.Key, offset, cancel).ConfigureAwait(false))
        await using (var target = new FileStream(part, offset == 0 ? FileMode.Create : FileMode.Append,
                         FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
        {
            var buffer = new byte[BufferSize];
            int read;

            // A store that goes silent part way through a file would otherwise be waited on for ever: the
            // clock starts again with every read, so a file that keeps coming may take as long as it needs.
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel);

            try
            {
                while (true)
                {
                    stall.CancelAfter(StallTimeout);

                    if ((read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false)) == 0)
                        break;

                    await target.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
                    progress?.Report(read);
                }
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                throw new IOException($"{stored.Key} stopped arriving: nothing came for {StallTimeout.TotalMinutes:0} minutes. It carries on from here next time.");
            }
        }

        var length = new FileInfo(part).Length;

        if (length != stored.Size)
        {
            File.Delete(part);

            throw new IOException($"{stored.Key} arrived as {length:N0} bytes, not {stored.Size:N0}; it will be fetched again.");
        }

        File.Move(part, path, overwrite: true);

        lock (gate)
        {
            parts.Remove(stored.Key);
            index[stored.Key] = new CachedFile(stored.Size, stored.ETag);
            Save();
        }

        return path;
    }

    /// <summary>Every file the cache holds, with its key and size.</summary>
    public IReadOnlyList<(string Key, long Size)> Files()
    {
        lock (gate)
            return [.. index.Where(f => File.Exists(PathOf(f.Key))).Select(f => (f.Key, f.Value.Size))];
    }

    /// <summary>Deletes cached files whose keys start with <paramref name="prefix"/>; returns the bytes freed.</summary>
    public long Remove(string prefix)
    {
        lock (gate)
        {
            var freed = 0L;

            foreach (var key in index.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            {
                var path = PathOf(key);

                if (File.Exists(path))
                {
                    freed += new FileInfo(path).Length;
                    File.Delete(path);
                }

                index.Remove(key);
            }

            Save();

            return freed;
        }
    }

    /// <summary>Where a <c>.part</c> file can carry on from: its length, or 0 when it is stale.</summary>
    private long ResumeFrom(string part, StoredObject stored)
    {
        if (!File.Exists(part))
            return 0;

        var length = new FileInfo(part).Length;

        lock (gate)
        {
            if (parts.TryGetValue(stored.Key, out var was) && was == new CachedFile(stored.Size, stored.ETag)
                && length < stored.Size)
            {
                return length;
            }
        }

        File.Delete(part);

        return 0;
    }

    private void Remember(Dictionary<string, CachedFile> map, string key, CachedFile file)
    {
        lock (gate)
        {
            map[key] = file;
            Save();
        }
    }

    private void Save()
    {
        var path = Path.Combine(Root, IndexName);
        var temp = path + ".tmp";

        File.WriteAllText(temp, JsonSerializer.Serialize(new CacheIndex(index, parts), CacheJson.Default.CacheIndex));
        File.Move(temp, path, overwrite: true);
    }

    private static CacheIndex Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize(File.ReadAllText(path), CacheJson.Default.CacheIndex) ?? CacheIndex.Empty()
                : CacheIndex.Empty();
        }
        catch (JsonException)
        {
            // A damaged index only costs downloads: start over.
            return CacheIndex.Empty();
        }
    }
}

internal sealed record CacheIndex(Dictionary<string, CachedFile> Files, Dictionary<string, CachedFile> Parts)
{
    public static CacheIndex Empty() => new([], []);
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(CacheIndex))]
internal sealed partial class CacheJson : JsonSerializerContext;
