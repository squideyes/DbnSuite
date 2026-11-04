using DbnClient.Contracts;
using DbnClient.Dbn;

namespace DbnClient.Archive;

/// <summary>One stored day of one series: a data file, or an empty marker for a day with no data.</summary>
public sealed record StoredDay(DateOnly Day, StoredObject Object)
{
    public bool Empty => DbnSyncLayout.IsEmpty(Object.Key);
}

/// <summary>One schema of one root, and what the archive holds of it.</summary>
public sealed record SeriesSummary(string Schema, string Root, int Files, int EmptyDays, long Bytes,
    DateOnly? First, DateOnly? Last);

/// <summary>
/// A DbnSync archive, read through any <see cref="IObjectStore"/>: what it
/// holds, and each day's file, fetched once into a <see cref="LocalCache"/>.
/// </summary>
public sealed class DbnArchive(IObjectStore store, LocalCache? cache, string dataset = "GLBX.MDP3") : IDisposable
{
    public const string DefinitionSchema = "definition";

    public IObjectStore Store { get; } = store;

    public LocalCache? Cache { get; } = cache;

    public string Dataset { get; } = dataset;

    /// <summary>
    /// Opens the archive <paramref name="options"/> names, caching under
    /// <paramref name="cacheRoot"/>. A folder archive needs no cache: its files are already local.
    /// </summary>
    public static DbnArchive Open(ArchiveOptions options, string? cacheRoot) =>
        options.IsFolder
            ? new DbnArchive(new FolderObjectStore(options.Location), null, options.Dataset)
            : new DbnArchive(new S3ObjectStore(options), cacheRoot is null ? null : new LocalCache(cacheRoot), options.Dataset);

    /// <summary>The layout the archive's files are in, which this version must know.</summary>
    public Task<Result<string>> LayoutAsync(CancellationToken cancel) => Guard(async () =>
    {
        if (await Store.FindAsync(DbnSyncLayout.LayoutKey, cancel).ConfigureAwait(false) is not null)
            return (Result<string>)DbnSyncLayout.Name;

        await foreach (var folder in Store.FoldersAsync("", cancel).ConfigureAwait(false))
        {
            if (folder == $"{Dataset}/")
                return (Result<string>)DbnSyncLayout.Name;
        }

        return (Result<string>)(new Error(Errors.UnknownLayout,
            $"The archive holds no {Dataset} folder, so it is not in a layout DbnClient knows."));
    });

    /// <summary>Every series the archive holds, with its day count and size.</summary>
    public Task<Result<IReadOnlyList<SeriesSummary>>> ListSeriesAsync(CancellationToken cancel) =>
        GuardValue<IReadOnlyList<SeriesSummary>>(async () =>
        {
            var series = new List<SeriesSummary>();

            await foreach (var schemaFolder in Store.FoldersAsync($"{Dataset}/", cancel).ConfigureAwait(false))
            {
                var schema = Name(schemaFolder);

                await foreach (var rootFolder in Store.FoldersAsync(schemaFolder, cancel).ConfigureAwait(false))
                {
                    var root = Name(rootFolder);
                    var days = await DaysAsync(schema, root, cancel).ConfigureAwait(false);
                    var files = days.Where(d => !d.Empty).ToList();

                    series.Add(new SeriesSummary(schema, root, files.Count, days.Count - files.Count,
                        files.Sum(d => d.Object.Size),
                        files.Count > 0 ? files[0].Day : null,
                        files.Count > 0 ? files[^1].Day : null));
                }
            }

            return series;
        });

    /// <summary>Every stored day of one series, oldest first.</summary>
    public Task<Result<IReadOnlyList<StoredDay>>> ListDaysAsync(string schema, string root, CancellationToken cancel) =>
        GuardValue(() => DaysAsync(schema, root, cancel));

    /// <summary>
    /// The local path of one day's file: fetched into the cache unless it is
    /// there already, or the archive's own path for a folder archive.
    /// </summary>
    public Task<Result<string>> FetchAsync(string schema, string root, DateOnly day, IProgress<long>? progress,
        CancellationToken cancel) => Guard(async () =>
    {
        var key = DbnSyncLayout.Data(Dataset, schema, root, day);
        var stored = await Store.FindAsync(key, cancel).ConfigureAwait(false);

        if (stored is null)
        {
            return await Store.FindAsync(DbnSyncLayout.Empty(Dataset, schema, root, day), cancel).ConfigureAwait(false) is null
                ? (Result<string>)(new Error(Errors.NotStored, $"The archive has no {root} {schema} file for {day:yyyy-MM-dd}."))
                : (Result<string>)(new Error(Errors.NoData, $"{root} {schema} had no data on {day:yyyy-MM-dd}."));
        }

        return (Result<string>)await FetchAsync(stored, progress, cancel).ConfigureAwait(false);
    });

    /// <summary>The local path of a stored object, fetching it into the cache if needed.</summary>
    public async Task<string> FetchAsync(StoredObject stored, IProgress<long>? progress, CancellationToken cancel)
    {
        if (Store is FolderObjectStore folder)
            return folder.PathOf(stored.Key);

        var cache = Cache ?? throw new InvalidOperationException("An S3 archive needs a cache to fetch into.");

        return await cache.FetchAsync(Store, stored, progress, cancel).ConfigureAwait(false);
    }

    /// <summary>Opens one day's file for reading, fetching it first if needed.</summary>
    public async Task<Result<DbnReader>> OpenAsync(string schema, string root, DateOnly day, CancellationToken cancel)
    {
        var path = await FetchAsync(schema, root, day, null, cancel).ConfigureAwait(false);

        if (!path.IsSuccess)
            return path.Error!;

        return await ReadAsync(schema, root, day, path.Value, cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens a day's file, already local. A file that turns out damaged is
    /// <see cref="Errors.Corrupt"/>: here, or as an <see cref="ArchiveException"/>
    /// from the reader if the damage is further in.
    /// </summary>
    internal async Task<Result<DbnReader>> ReadAsync(string schema, string root, DateOnly day, string path,
        CancellationToken cancel)
    {
        try
        {
            return await DbnReader.OpenAsync(File.OpenRead(path),
                detail => new ArchiveException(Damaged(schema, root, day, path, detail)), cancel).ConfigureAwait(false);
        }
        catch (ArchiveException e)
        {
            return e.Error;
        }
    }

    /// <summary>
    /// The instruments of <paramref name="root"/> as one day's <c>definition</c>
    /// file describes them, by instrument id.
    /// </summary>
    public async Task<Result<IReadOnlyDictionary<uint, Contract>>> InstrumentsAsync(string root, DateOnly day,
        CancellationToken cancel)
    {
        var opened = await OpenAsync(DefinitionSchema, root, day, cancel).ConfigureAwait(false);

        if (!opened.IsSuccess)
            return opened.Error!;

        var contracts = new Dictionary<uint, Contract>();

        await using var reader = opened.Value;

        try
        {
            await foreach (var record in reader.ReadAsync(cancel).ConfigureAwait(false))
            {
                if (record is DefinitionRecord definition)
                    contracts[definition.InstrumentId] = Contract.From(definition);
            }
        }
        catch (ArchiveException e)
        {
            return e.Error;
        }

        return contracts;
    }

    /// <summary>
    /// Every contract of <paramref name="root"/> the archive's definitions
    /// name, read from the first stored day of each month and the last day,
    /// since an exchange lists each contract for months.
    /// </summary>
    public async Task<Result<IReadOnlyList<Contract>>> ContractsAsync(string root, IProgress<long>? progress,
        CancellationToken cancel)
    {
        var days = await ListDaysAsync(DefinitionSchema, root, cancel).ConfigureAwait(false);

        if (!days.IsSuccess)
            return days.Error!;

        var stored = days.Value.Where(d => !d.Empty).ToList();
        var sample = stored
            .GroupBy(d => (d.Day.Year, d.Day.Month))
            .Select(g => g.First())
            .Concat(stored.TakeLast(1))
            .Distinct()
            .ToList();

        var contracts = new Dictionary<uint, Contract>();

        foreach (var day in sample)
        {
            var instruments = await InstrumentsAsync(root, day.Day, cancel).ConfigureAwait(false);

            if (!instruments.IsSuccess)
                return instruments.Error!;

            foreach (var (id, contract) in instruments.Value)
                contracts[id] = contract;

            progress?.Report(1);
        }

        return contracts.Values.OrderBy(c => c.Expiration).ThenBy(c => c.Symbol, StringComparer.Ordinal).ToList();
    }

    public void Dispose() => (Store as IDisposable)?.Dispose();

    private async Task<IReadOnlyList<StoredDay>> DaysAsync(string schema, string root, CancellationToken cancel)
    {
        var days = new List<StoredDay>();

        await foreach (var stored in Store.ObjectsAsync(DbnSyncLayout.Folder(Dataset, schema, root), cancel)
            .ConfigureAwait(false))
        {
            if (DbnSyncLayout.DayOf(stored.Key) is { } day)
                days.Add(new StoredDay(day, stored));
        }

        // A day re-fetched after it had nothing keeps its marker: the data is what the day holds.
        return [.. days.GroupBy(d => d.Day).Select(g => g.FirstOrDefault(d => !d.Empty) ?? g.First()).OrderBy(d => d.Day)];
    }

    /// <summary>What a damaged file is, and what to do: a cached copy can come down again; a folder's can't.</summary>
    private Error Damaged(string schema, string root, DateOnly day, string path, string detail) =>
        new(Errors.Corrupt, Store is FolderObjectStore
            ? $"{path} is damaged ({detail}). This archive is a folder, so copy a good file over it."
            : $"{path} is damaged ({detail}). Download it again with dbnf pull {schema} {root} --from {day:yyyy-MM-dd} --force.");

    private static string Name(string folder) => folder.TrimEnd('/')[(folder.TrimEnd('/').LastIndexOf('/') + 1)..];

    private static async Task<Result<T>> Guard<T>(Func<Task<Result<T>>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (ArchiveException e)
        {
            return e.Error;
        }
    }

    private static Task<Result<T>> GuardValue<T>(Func<Task<T>> call) =>
        Guard(async () => (Result<T>)await call().ConfigureAwait(false));
}
