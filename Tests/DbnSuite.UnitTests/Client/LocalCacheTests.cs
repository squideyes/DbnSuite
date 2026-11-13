using DbnSuite.Client.Archive;
using NSubstitute;
using Shouldly;

namespace DbnSuite.UnitTests.ClientTests;

public sealed class LocalCacheTests : IDisposable
{
    private const string Key = "GLBX.MDP3/mbo/MNQ/2026/MNQ.mbo.20261028.dbn.zst";
    private const string ETag = "\"3f2a\"";
    private const string OtherETag = "\"9c21\"";

    private static readonly byte[] Content = [.. Enumerable.Range(0, 1000).Select(i => (byte)i)];

    private readonly string archiveFolder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly string cacheFolder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public LocalCacheTests()
    {
        var path = Path.Combine(archiveFolder, Key.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Content);
    }

    public void Dispose()
    {
        foreach (var folder in new[] { archiveFolder, cacheFolder })
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    private CountingStore Store() => new(new FolderObjectStore(archiveFolder));

    private static StoredObject Object(string etag = ETag) => new(Key, Content.Length, etag);

    [Fact]
    public async Task A_fetched_file_lands_under_its_key_with_its_bytes()
    {
        var cache = new LocalCache(cacheFolder);

        var path = await cache.FetchAsync(Store(), Object(), null, CancellationToken.None);

        path.ShouldBe(Path.Combine(cacheFolder, Key.Replace('/', Path.DirectorySeparatorChar)));
        (await File.ReadAllBytesAsync(path)).ShouldBe(Content);
        cache.Holds(Object()).ShouldBeTrue();
    }

    [Fact]
    public async Task A_file_already_held_is_not_fetched_again_even_by_a_new_cache()
    {
        await new LocalCache(cacheFolder).FetchAsync(Store(), Object(), null, CancellationToken.None);
        var store = Store();

        await new LocalCache(cacheFolder).FetchAsync(store, Object(), null, CancellationToken.None);

        store.Opens.ShouldBe(0);
    }

    [Theory]
    [InlineData("GLBX.MDP3/mbo/ES/../../../../outside.mbo.20260101.dbn.zst")]
    [InlineData("../outside.dbn.zst")]
    public void A_key_that_climbs_out_of_the_cache_is_refused(string key)
    {
        var cache = new LocalCache(cacheFolder);

        Should.Throw<ArchiveException>(() => cache.PathOf(key));
    }

    [Fact]
    public async Task A_changed_object_is_fetched_again()
    {
        var cache = new LocalCache(cacheFolder);
        await cache.FetchAsync(Store(), Object(), null, CancellationToken.None);
        var store = Store();

        await cache.FetchAsync(store, Object(OtherETag), null, CancellationToken.None);

        store.Opens.ShouldBe(1);
    }

    [Fact]
    public async Task A_download_that_goes_silent_is_given_up_and_carries_on_next_time()
    {
        var cache = new LocalCache(cacheFolder) { StallTimeout = TimeSpan.FromMilliseconds(150) };
        var store = new CountingStore(new FolderObjectStore(archiveFolder), silentAfter: 400);

        var stalled = await Should.ThrowAsync<IOException>(() => cache.FetchAsync(store, Object(), null, CancellationToken.None));

        stalled.Message.ShouldContain("stopped arriving");

        var resumed = Store();
        await cache.FetchAsync(resumed, Object(), null, CancellationToken.None);

        resumed.Offsets.ShouldBe([400]);
        (await File.ReadAllBytesAsync(cache.PathOf(Key))).ShouldBe(Content);
    }

    [Fact]
    public async Task A_download_stopped_by_the_caller_is_not_called_a_stall()
    {
        var cache = new LocalCache(cacheFolder);
        var store = new CountingStore(new FolderObjectStore(archiveFolder), silentAfter: 400);
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Should.ThrowAsync<OperationCanceledException>(() => cache.FetchAsync(store, Object(), null, stop.Token));
    }

    [Fact]
    public async Task An_interrupted_download_carries_on_from_where_it_stopped()
    {
        var cache = new LocalCache(cacheFolder);
        var store = new CountingStore(new FolderObjectStore(archiveFolder), failAfter: 400);

        await Should.ThrowAsync<IOException>(() => cache.FetchAsync(store, Object(), null, CancellationToken.None));

        var resumed = Store();
        await cache.FetchAsync(resumed, Object(), null, CancellationToken.None);

        resumed.Offsets.ShouldBe([400]);
        (await File.ReadAllBytesAsync(cache.PathOf(Key))).ShouldBe(Content);
    }

    [Fact]
    public async Task A_part_from_another_version_of_the_object_is_started_over()
    {
        var cache = new LocalCache(cacheFolder);
        var store = new CountingStore(new FolderObjectStore(archiveFolder), failAfter: 400);
        await Should.ThrowAsync<IOException>(() => cache.FetchAsync(store, Object(OtherETag), null, CancellationToken.None));

        var resumed = Store();
        await cache.FetchAsync(resumed, Object(), null, CancellationToken.None);

        resumed.Offsets.ShouldBe([0]);
    }

    [Fact]
    public async Task A_flat_cache_keeps_only_the_file_name()
    {
        var cache = new LocalCache(cacheFolder, flat: true);

        var path = await cache.FetchAsync(Store(), Object(), null, CancellationToken.None);

        path.ShouldBe(Path.Combine(cacheFolder, Path.GetFileName(Key)));
    }

    [Fact]
    public async Task Clearing_a_prefix_frees_its_files()
    {
        var cache = new LocalCache(cacheFolder);
        await cache.FetchAsync(Store(), Object(), null, CancellationToken.None);

        var freed = cache.Remove("GLBX.MDP3/mbo/");

        freed.ShouldBe(Content.Length);
        cache.Files().ShouldBeEmpty();
        File.Exists(cache.PathOf(Key)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_download_of_the_wrong_size_is_thrown_away_so_the_next_try_starts_over()
    {
        var cache = new LocalCache(cacheFolder);
        var claimedLonger = new StoredObject(Key, Content.Length + 1, ETag);

        var refused = await Should.ThrowAsync<IOException>(() => cache.FetchAsync(Store(), claimedLonger, null, CancellationToken.None));

        refused.Message.ShouldContain(Key);
        cache.Holds(claimedLonger).ShouldBeFalse();
        File.Exists(cache.PathOf(Key) + ".part").ShouldBeFalse();
    }

    [Fact]
    public async Task A_part_as_long_as_the_object_is_started_over()
    {
        var cache = new LocalCache(cacheFolder);
        var store = new CountingStore(new FolderObjectStore(archiveFolder), failAfter: 400);
        await Should.ThrowAsync<IOException>(() => cache.FetchAsync(store, Object(), null, CancellationToken.None));
        await File.WriteAllBytesAsync(cache.PathOf(Key) + ".part", Content);

        var resumed = Store();
        await cache.FetchAsync(resumed, Object(), null, CancellationToken.None);

        resumed.Offsets.ShouldBe([0]);
    }

    [Fact]
    public async Task Bytes_received_are_reported()
    {
        var cache = new LocalCache(cacheFolder);
        var progress = Substitute.For<IProgress<long>>();

        await cache.FetchAsync(Store(), Object(), progress, CancellationToken.None);

        progress.Received(1).Report(Content.Length);
    }

    [Fact]
    public async Task A_damaged_index_only_costs_a_download()
    {
        await new LocalCache(cacheFolder).FetchAsync(Store(), Object(), null, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(cacheFolder, ".dbnsync.json"), "{ not json");

        var cache = new LocalCache(cacheFolder);

        cache.Holds(Object()).ShouldBeFalse();
    }

    [Fact]
    public async Task An_index_of_nothing_starts_over_too()
    {
        await new LocalCache(cacheFolder).FetchAsync(Store(), Object(), null, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(cacheFolder, ".dbnsync.json"), "null");

        var cache = new LocalCache(cacheFolder);

        cache.Holds(Object()).ShouldBeFalse();
    }

    [Fact]
    public async Task A_file_cut_short_on_disk_is_not_held()
    {
        var cache = new LocalCache(cacheFolder);
        await cache.FetchAsync(Store(), Object(), null, CancellationToken.None);

        await File.WriteAllBytesAsync(cache.PathOf(Key), [1]);

        cache.Holds(Object()).ShouldBeFalse();
    }

    [Fact]
    public async Task Files_are_listed_with_their_sizes_unless_gone_from_disk()
    {
        var cache = new LocalCache(cacheFolder);
        await cache.FetchAsync(Store(), Object(), null, CancellationToken.None);

        cache.Files().ShouldBe([(Key, (long)Content.Length)]);

        File.Delete(cache.PathOf(Key));

        cache.Files().ShouldBeEmpty();
    }

    [Fact]
    public async Task Clearing_leaves_other_prefixes_and_counts_nothing_for_a_file_already_gone()
    {
        var cache = new LocalCache(cacheFolder);
        await cache.FetchAsync(Store(), Object(), null, CancellationToken.None);

        cache.Remove("GLBX.MDP3/trades/").ShouldBe(0);
        cache.Files().Count.ShouldBe(1);

        File.Delete(cache.PathOf(Key));

        cache.Remove("GLBX.MDP3/").ShouldBe(0);
    }

    /// <summary>A store that counts opens, and can fail partway through one like a dropped connection.</summary>
    /// <param name="silentAfter">The connection goes quiet after this many bytes: no more arrive, and it never ends.</param>
    private sealed class CountingStore(IObjectStore inner, int failAfter = int.MaxValue, int silentAfter = int.MaxValue) : IObjectStore
    {
        public int Opens => Offsets.Count;

        public List<long> Offsets { get; } = [];

        public IAsyncEnumerable<string> FoldersAsync(string prefix, CancellationToken cancel) => inner.FoldersAsync(prefix, cancel);

        public IAsyncEnumerable<StoredObject> ObjectsAsync(string prefix, CancellationToken cancel) => inner.ObjectsAsync(prefix, cancel);

        public Task<StoredObject?> FindAsync(string key, CancellationToken cancel) => inner.FindAsync(key, cancel);

        public async Task<Stream> OpenAsync(string key, long offset, CancellationToken cancel)
        {
            Offsets.Add(offset);

            var stream = await inner.OpenAsync(key, offset, cancel);

            if (silentAfter != int.MaxValue)
                return new FailingStream(stream, silentAfter, silent: true);

            return failAfter == int.MaxValue ? stream : new FailingStream(stream, failAfter);
        }
    }

    private sealed class FailingStream(Stream inner, int failAfter, bool silent = false) : Stream
    {
        private int read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => read; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default)
        {
            if (read >= failAfter && silent)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancel);

            if (read >= failAfter)
                throw new IOException("The connection dropped.");

            var n = await inner.ReadAsync(buffer[..Math.Min(buffer.Length, failAfter - read)], cancel);
            read += n;

            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();

            base.Dispose(disposing);
        }
    }
}
