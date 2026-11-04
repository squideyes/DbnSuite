using System.Runtime.CompilerServices;

namespace DbnClient.Archive;

/// <summary>An archive in a folder on this machine, laid out as it would be in a bucket.</summary>
public sealed class FolderObjectStore(string root) : IObjectStore
{
    private readonly string root = Path.GetFullPath(root);

    public async IAsyncEnumerable<string> FoldersAsync(string prefix,
        [EnumeratorCancellation] CancellationToken cancel)
    {
        await Task.CompletedTask.ConfigureAwait(false);

        var folder = Resolve(prefix);

        if (!Directory.Exists(folder))
            yield break;

        foreach (var child in Directory.EnumerateDirectories(folder).Order(StringComparer.Ordinal))
        {
            cancel.ThrowIfCancellationRequested();

            yield return $"{prefix}{Path.GetFileName(child)}/";
        }
    }

    public async IAsyncEnumerable<StoredObject> ObjectsAsync(string prefix,
        [EnumeratorCancellation] CancellationToken cancel)
    {
        await Task.CompletedTask.ConfigureAwait(false);

        var folder = Resolve(prefix);

        if (!Directory.Exists(folder))
            yield break;

        foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            cancel.ThrowIfCancellationRequested();

            yield return Describe(path);
        }
    }

    public Task<StoredObject?> FindAsync(string key, CancellationToken cancel)
    {
        var path = Resolve(key);

        return Task.FromResult(File.Exists(path) ? Describe(path) : null);
    }

    public Task<Stream> OpenAsync(string key, long offset, CancellationToken cancel)
    {
        var stream = new FileStream(Resolve(key), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        stream.Seek(offset, SeekOrigin.Begin);

        return Task.FromResult<Stream>(stream);
    }

    /// <summary>The file a key names in this folder.</summary>
    public string PathOf(string key) => Resolve(key);

    private StoredObject Describe(string path)
    {
        var key = Path.GetRelativePath(root, path).Replace('\\', '/');

        return new StoredObject(key, new FileInfo(path).Length, "");
    }

    private string Resolve(string key)
    {
        var path = Path.GetFullPath(Path.Combine(root, key));
        var inside = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;

        if (path != root && !path.StartsWith(inside, StringComparison.Ordinal))
            throw new ArgumentException($"{key} is outside the archive folder.", nameof(key));

        return path;
    }
}
