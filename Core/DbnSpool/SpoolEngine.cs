using DbnClient;
using DbnClient.Archive;
using DbnClient.Tape;

namespace DbnSpool;

/// <summary>The archive and tape work a pass drives.</summary>
public interface ISpoolEngine
{
    string Dataset { get; }

    Task<Result<IReadOnlyList<StoredDay>>> DaysAsync(string schema, string root, CancellationToken cancel);

    Task<Result<Catalog>> CatalogAsync(string root, CancellationToken cancel);

    Task<Result<BuildReport>> BuildAsync(Catalog catalog, DateOnly date, CancellationToken cancel);

    Task<IReadOnlyList<ProofResult>> ProveAsync(Catalog catalog, DateOnly date, CancellationToken cancel);

    /// <summary>Drops <c>mbo</c> files from the cache: they are large, and the next date needs at most one of them.</summary>
    void Release();
}

/// <summary>The engine over a real archive.</summary>
public sealed class SpoolEngine(DbnArchive archive, SpoolOptions options) : ISpoolEngine
{
    private const string MboSchema = "mbo";

    public string Dataset => archive.Dataset;

    public Task<Result<IReadOnlyList<StoredDay>>> DaysAsync(string schema, string root, CancellationToken cancel) =>
        archive.ListDaysAsync(schema, root, cancel);

    public Task<Result<Catalog>> CatalogAsync(string root, CancellationToken cancel) =>
        Catalog.BuildAsync(archive, root, null, cancel);

    public Task<Result<BuildReport>> BuildAsync(Catalog catalog, DateOnly date, CancellationToken cancel) =>
        new TapeBuilder(archive, catalog, options.TapeFolder).BuildAsync(date, options.Compress, null, cancel);

    public Task<IReadOnlyList<ProofResult>> ProveAsync(Catalog catalog, DateOnly date, CancellationToken cancel) =>
        new TapeProof(archive, catalog, options.TapeFolder).RunAsync(date, cancel);

    public void Release() => archive.Cache?.Remove($"{archive.Dataset}/{MboSchema}/");
}
