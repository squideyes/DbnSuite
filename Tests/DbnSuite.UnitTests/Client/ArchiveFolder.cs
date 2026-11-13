using System.Runtime.CompilerServices;
using DbnSuite.Client;
using DbnSuite.Client.Archive;
using static DbnSuite.UnitTests.TestData;

namespace DbnSuite.UnitTests.ClientTests;

/// <summary>An archive in a temporary folder, filled a day at a time with generated records.</summary>
internal sealed class ArchiveFolder : IDisposable
{
    private static readonly Dictionary<string, uint> NoMappings = [];

    private readonly string cache = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public string Location { get; } = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public void Add(string schema, string root, DateOnly day, params byte[][] records) =>
        Add(schema, root, day, NoMappings, records);

    /// <param name="mappings">What the day's metadata maps: a requested name to an instrument id.</param>
    public void Add(string schema, string root, DateOnly day, IReadOnlyDictionary<string, uint> mappings, params byte[][] records) =>
        Write(DbnSyncLayout.Data(Dataset, schema, root, day),
            DbnBytes.Compress([DbnBytes.Metadata(Dataset, [$"{root}.FUT"], mappings), .. records]));

    public void AddEmpty(string schema, string root, DateOnly day) =>
        Write(DbnSyncLayout.Empty(Dataset, schema, root, day), []);

    public void Write(string key, byte[] bytes)
    {
        var path = Path.Combine(Location, key.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    public DbnArchive Open() => DbnArchive.Open(new ArchiveOptions { Location = Location }, null);

    /// <summary>This archive behind a store that fails with <paramref name="error"/> for keys that match.</summary>
    public DbnArchive OpenFailing(Func<string, bool> fails, Error error) =>
        new(new FailingStore(new FolderObjectStore(Location), fails, error), new LocalCache(cache));

    public void Dispose()
    {
        foreach (var folder in new[] { Location, cache })
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    private sealed class FailingStore(IObjectStore inner, Func<string, bool> fails, Error error) : IObjectStore
    {
        public async IAsyncEnumerable<string> FoldersAsync(string prefix, [EnumeratorCancellation] CancellationToken cancel)
        {
            await foreach (var folder in inner.FoldersAsync(prefix, cancel))
                yield return folder;
        }

        public async IAsyncEnumerable<StoredObject> ObjectsAsync(string prefix, [EnumeratorCancellation] CancellationToken cancel)
        {
            await foreach (var stored in inner.ObjectsAsync(prefix, cancel))
                yield return stored;
        }

        public Task<StoredObject?> FindAsync(string key, CancellationToken cancel) =>
            fails(key) ? throw new ArchiveException(error) : inner.FindAsync(key, cancel);

        public Task<Stream> OpenAsync(string key, long offset, CancellationToken cancel) =>
            inner.OpenAsync(key, offset, cancel);
    }
}
