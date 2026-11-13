using DbnSuite.Client.Archive;
using DbnSuite.Spool;
using DbnSuite.IntegrationTests.Support;
using Shouldly;

namespace DbnSuite.IntegrationTests;

/// <summary>DbnSpool's shelf against a real S3 server.</summary>
[Collection(ArchiveCollection.Name)]
public sealed class S3TapeShelfTests(RustFs s3) : IDisposable
{
    private const string Key = "GLBX.MDP3/s1/ES/2026/ESZ6.s1.20261029.tape";
    private const string LedgerKey = "GLBX.MDP3/spool/ES.json";

    private readonly string file = Path.GetTempFileName();

    public void Dispose() => File.Delete(file);

    private S3TapeShelf Shelf(string prefix = "") => new(new ArchiveOptions
    {
        Location = s3.Endpoint,
        Bucket = $"tapes-{Guid.NewGuid():N}",
        Prefix = prefix,
        AccessKey = RustFs.AccessKey,
        SecretKey = RustFs.SecretKey
    });

    [Fact]
    public async Task A_file_goes_up_and_comes_back_whole()
    {
        byte[] bytes = [1, 2, 3, 4];

        await File.WriteAllBytesAsync(file, bytes);

        using var shelf = Shelf();

        await shelf.EnsureReadyAsync(CancellationToken.None);
        await shelf.PutFileAsync(Key, file, CancellationToken.None);

        (await shelf.ReadAsync(Key, CancellationToken.None)).ShouldBe(bytes);
    }

    [Fact]
    public async Task A_ledger_is_replaced_in_place_under_a_prefix()
    {
        using var shelf = Shelf("desk/tapes");

        await shelf.EnsureReadyAsync(CancellationToken.None);
        await shelf.PutBytesAsync(LedgerKey, [1], CancellationToken.None);
        await shelf.PutBytesAsync(LedgerKey, [2], CancellationToken.None);

        (await shelf.ReadAsync(LedgerKey, CancellationToken.None)).ShouldBe([(byte)2]);
    }

    [Fact]
    public async Task A_key_it_lacks_reads_as_nothing()
    {
        using var shelf = Shelf();

        await shelf.EnsureReadyAsync(CancellationToken.None);

        (await shelf.ReadAsync(Key, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Ready_twice_is_fine()
    {
        using var shelf = Shelf();

        await shelf.EnsureReadyAsync(CancellationToken.None);
        await shelf.EnsureReadyAsync(CancellationToken.None);
    }
}
