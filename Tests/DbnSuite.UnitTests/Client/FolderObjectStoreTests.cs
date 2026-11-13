using DbnSuite.Client.Archive;
using Shouldly;

namespace DbnSuite.UnitTests.ClientTests;

public sealed class FolderObjectStoreTests : IDisposable
{
    private const string Key = "GLBX.MDP3/mbo/ES/2026/ES.mbo.20261028.dbn.zst";
    private const string OtherKey = "GLBX.MDP3/mbo/ES/2026/ES.mbo.20261029.dbn.zst";

    private static readonly byte[] Content = [1, 2, 3, 4, 5];

    private readonly string parent = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly string folder;
    private readonly FolderObjectStore store;

    public FolderObjectStoreTests()
    {
        folder = Path.Combine(parent, "archive");

        Write(Key);
        Write(OtherKey);

        store = new FolderObjectStore(folder);
    }

    public void Dispose() => Directory.Delete(parent, recursive: true);

    private void Write(string key)
    {
        var path = Path.Combine(folder, key.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Content);
    }

    [Fact]
    public async Task Folders_are_listed_with_their_prefix_and_a_slash()
    {
        var folders = await store.FoldersAsync("GLBX.MDP3/", CancellationToken.None).ToListAsync();

        folders.ShouldBe(["GLBX.MDP3/mbo/"]);
    }

    [Fact]
    public async Task A_missing_folder_has_no_folders_or_objects()
    {
        (await store.FoldersAsync("nothing/", CancellationToken.None).ToListAsync()).ShouldBeEmpty();
        (await store.ObjectsAsync("nothing/", CancellationToken.None).ToListAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Objects_are_found_at_any_depth_with_forward_slashes()
    {
        var objects = await store.ObjectsAsync("GLBX.MDP3/", CancellationToken.None).ToListAsync();

        objects.ShouldBe([new StoredObject(Key, Content.Length, ""), new StoredObject(OtherKey, Content.Length, "")]);
    }

    [Fact]
    public async Task Listing_stops_when_cancelled()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => store.FoldersAsync("", cancelled.Token).ToListAsync().AsTask());
        await Should.ThrowAsync<OperationCanceledException>(() => store.ObjectsAsync("", cancelled.Token).ToListAsync().AsTask());
    }

    [Fact]
    public async Task One_object_is_described_or_is_not_there()
    {
        (await store.FindAsync(Key, CancellationToken.None)).ShouldBe(new StoredObject(Key, Content.Length, ""));
        (await store.FindAsync("no/such/key", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task An_object_reads_from_the_offset_asked_for()
    {
        await using var stream = await store.OpenAsync(Key, 3, CancellationToken.None);
        var read = new byte[2];

        await stream.ReadExactlyAsync(read);

        read.ShouldBe([4, 5]);
    }

    [Fact]
    public void A_key_is_a_path_in_the_folder()
    {
        store.PathOf(Key).ShouldBe(Path.Combine(folder, Key.Replace('/', Path.DirectorySeparatorChar)));
    }

    [Fact]
    public void A_key_outside_the_folder_is_refused()
    {
        Should.Throw<ArgumentException>(() => store.PathOf("../outside.dbn.zst"));
    }

    [Fact]
    public void A_key_into_a_folder_whose_name_merely_starts_the_same_is_refused()
    {
        Should.Throw<ArgumentException>(() => store.PathOf("../archive-next/secret.dbn.zst"));
    }

    [Fact]
    public async Task A_folder_given_with_a_trailing_separator_works_the_same()
    {
        var trailing = new FolderObjectStore(folder + Path.DirectorySeparatorChar);

        (await trailing.FindAsync(Key, CancellationToken.None)).ShouldNotBeNull();
        (await trailing.FoldersAsync("", CancellationToken.None).ToListAsync()).ShouldBe(["GLBX.MDP3/"]);
    }
}
