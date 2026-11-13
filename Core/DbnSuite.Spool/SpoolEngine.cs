using DbnSuite.Client;
using DbnSuite.Client.Archive;
using DbnSuite.Client.Tape;

namespace DbnSuite.Spool;

/// <summary>The archive and tape work a pass drives.</summary>
public interface ISpoolEngine
{
    string Dataset { get; }

    /// <summary>
    /// Names the code that builds and proves tapes and the memory it has, so a date set aside is
    /// tried again once either changes.
    /// </summary>
    string Build { get; }

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

    // Builds are deterministic, so a module's id changes only when its code does. The memory DbnSpool may
    // use is part of the name too: a date set aside for running out of it deserves another try with more.
    private static readonly string CodeBuild =
        $"{typeof(TapeBuilder).Assembly.ManifestModule.ModuleVersionId:N}.{typeof(SpoolEngine).Assembly.ManifestModule.ModuleVersionId:N}"
        + $".{GC.GetGCMemoryInfo().TotalAvailableMemoryBytes >> 20}m";

    public string Dataset => archive.Dataset;

    public string Build => CodeBuild;

    public Task<Result<IReadOnlyList<StoredDay>>> DaysAsync(string schema, string root, CancellationToken cancel) =>
        archive.ListDaysAsync(schema, root, cancel);

    public Task<Result<Catalog>> CatalogAsync(string root, CancellationToken cancel) =>
        Catalog.BuildAsync(archive, root, null, cancel);

    public Task<Result<BuildReport>> BuildAsync(Catalog catalog, DateOnly date, CancellationToken cancel) =>
        new TapeBuilder(archive, catalog, options.TapeFolder).BuildAsync(date, options.Compress, cancel);

    public Task<IReadOnlyList<ProofResult>> ProveAsync(Catalog catalog, DateOnly date, CancellationToken cancel) =>
        new TapeProof(archive, catalog, options.TapeFolder).RunAsync(date, cancel);

    public void Release() => archive.Cache?.Remove($"{archive.Dataset}/{MboSchema}/");
}
