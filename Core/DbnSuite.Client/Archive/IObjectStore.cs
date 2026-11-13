namespace DbnSuite.Client.Archive;

/// <summary>One object: its key relative to the archive, its size, and its ETag where the store has one.</summary>
public sealed record StoredObject(string Key, long Size, string ETag);

/// <summary>
/// The few things DbnClient asks of a store: list keys, describe one, read one
/// from an offset. An S3 bucket and a local folder both answer them.
/// </summary>
public interface IObjectStore
{
    /// <summary>The folders directly under <paramref name="prefix"/>, each ending in <c>/</c>.</summary>
    IAsyncEnumerable<string> FoldersAsync(string prefix, CancellationToken cancel);

    /// <summary>Every object under <paramref name="prefix"/>, at any depth.</summary>
    IAsyncEnumerable<StoredObject> ObjectsAsync(string prefix, CancellationToken cancel);

    /// <summary>One object, or null when there is none.</summary>
    Task<StoredObject?> FindAsync(string key, CancellationToken cancel);

    /// <summary>The object's bytes from <paramref name="offset"/> to its end.</summary>
    Task<Stream> OpenAsync(string key, long offset, CancellationToken cancel);
}
