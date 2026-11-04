using DbnClient;
using DbnClient.Archive;
using DbnClient.Dbn;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using static DbnSync.UnitTests.TestData;

namespace DbnSync.UnitTests.ClientTests;

/// <summary>An archive in a folder, laid out as the worker writes it.</summary>
public sealed class DbnArchiveTests : IDisposable
{
    private const uint FrontId = 42026453;
    private const uint SpreadId = 42001996;
    private const string FrontRaw = "MNQZ6";
    private const string SpreadRaw = "MNQZ6-MNQH7";
    private const long QuarterPoint = 250_000_000;
    private const ulong Listed = 1_700_000_000_000_000_000;
    private const ulong Expires = 1_797_000_000_000_000_000;

    private readonly string folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly DbnArchive archive;

    public DbnArchiveTests()
    {
        Write(DbnSyncLayout.Data(Dataset, Mbo, Root, Day(10, 30)), [1, 2, 3]);
        Write(DbnSyncLayout.Empty(Dataset, Mbo, Root, Day(10, 31)), []);
        Write(DbnSyncLayout.Data(Dataset, Mbo, Root, Day(11, 2)), [4, 5]);
        Write(DbnSyncLayout.Data(Dataset, Definition, Root, Day(10, 30)), Definitions());

        archive = DbnArchive.Open(new ArchiveOptions { Location = folder }, cacheRoot: null);
    }

    public void Dispose()
    {
        archive.Dispose();
        Directory.Delete(folder, recursive: true);
    }

    private void Write(string key, byte[] bytes)
    {
        var path = Path.Combine(folder, key.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static byte[] Definitions() => DbnBytes.Compress(
        DbnBytes.Metadata(Dataset, ["MNQ.FUT"], new Dictionary<string, uint>()),
        DbnBytes.Definition(FrontId, DbnBytes.Start, FrontRaw, Root, DefinitionRecord.Future, QuarterPoint, Listed, Expires, 2026, 12),
        DbnBytes.Definition(SpreadId, DbnBytes.Start, SpreadRaw, Root, DefinitionRecord.Spread, QuarterPoint, Listed, Expires, 2026, 12));

    [Fact]
    public async Task A_folder_with_the_dataset_is_in_the_dbnsync_layout()
    {
        (await archive.LayoutAsync(CancellationToken.None)).Value.ShouldBe(DbnSyncLayout.Name);
    }

    [Fact]
    public async Task A_folder_without_it_is_in_no_layout_dbnsync_knows()
    {
        using var empty = DbnArchive.Open(new ArchiveOptions { Location = Path.Combine(folder, Dataset, Mbo) }, null);

        (await empty.LayoutAsync(CancellationToken.None)).Error!.Code.ShouldBe(Errors.UnknownLayout);
    }

    [Fact]
    public async Task Every_series_is_listed_with_its_days_and_size()
    {
        var series = (await archive.ListSeriesAsync(CancellationToken.None)).Value;

        series.ShouldContain(new SeriesSummary(Mbo, Root, 2, 1, 5, Day(10, 30), Day(11, 2)));
        series.Select(s => s.Schema).ShouldBe([Definition, Mbo], ignoreOrder: true);
    }

    [Fact]
    public async Task A_stored_day_is_its_own_file_in_a_folder_archive()
    {
        var path = (await archive.FetchAsync(Mbo, Root, Day(10, 30), null, CancellationToken.None)).Value;

        (await File.ReadAllBytesAsync(path)).ShouldBe([1, 2, 3]);
    }

    [Fact]
    public async Task A_day_with_no_data_says_so()
    {
        (await archive.FetchAsync(Mbo, Root, Day(10, 31), null, CancellationToken.None)).Error!.Code.ShouldBe(Errors.NoData);
    }

    [Fact]
    public async Task A_day_never_fetched_says_so()
    {
        (await archive.FetchAsync(Mbo, Root, Day(11, 1), null, CancellationToken.None)).Error!.Code.ShouldBe(Errors.NotStored);
    }

    [Fact]
    public async Task A_layout_file_names_the_layout_on_its_own()
    {
        var other = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var path = Path.Combine(other, DbnSyncLayout.LayoutKey.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, []);

        try
        {
            using var marked = DbnArchive.Open(new ArchiveOptions { Location = other }, null);

            (await marked.LayoutAsync(CancellationToken.None)).Value.ShouldBe(DbnSyncLayout.Name);
        }
        finally
        {
            Directory.Delete(other, recursive: true);
        }
    }

    [Fact]
    public async Task A_refused_key_comes_back_as_a_result_from_every_call()
    {
        var refused = new ArchiveException(new Error(Errors.Denied, Errors.Denied));
        var store = Substitute.For<IObjectStore>();
        store.FindAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(refused);
        store.FoldersAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Throws(refused);
        store.ObjectsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Throws(refused);
        using var denied = new DbnArchive(store, null);

        (await denied.LayoutAsync(CancellationToken.None)).Error!.Code.ShouldBe(Errors.Denied);
        (await denied.ListSeriesAsync(CancellationToken.None)).Error!.Code.ShouldBe(Errors.Denied);
        (await denied.ListDaysAsync(Mbo, Root, CancellationToken.None)).Error!.Code.ShouldBe(Errors.Denied);
        (await denied.FetchAsync(Mbo, Root, Day(10, 30), null, CancellationToken.None)).Error!.Code.ShouldBe(Errors.Denied);
        (await denied.OpenAsync(Mbo, Root, Day(10, 30), CancellationToken.None)).Error!.Code.ShouldBe(Errors.Denied);
        (await denied.InstrumentsAsync(Root, Day(10, 30), CancellationToken.None)).Error!.Code.ShouldBe(Errors.Denied);
        (await denied.ContractsAsync(Root, null, CancellationToken.None)).Error!.Code.ShouldBe(Errors.Denied);
    }

    [Fact]
    public async Task A_day_that_vanishes_after_it_was_listed_fails_the_contract_list()
    {
        var store = Substitute.For<IObjectStore>();
        var key = DbnSyncLayout.Data(Dataset, Definition, Root, Day(10, 30));
        store.ObjectsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new[] { new StoredObject(key, 1, "") }.ToAsyncEnumerable());
        using var vanished = new DbnArchive(store, null);

        (await vanished.ContractsAsync(Root, null, CancellationToken.None)).Error!.Code.ShouldBe(Errors.NotStored);
    }

    [Fact]
    public async Task An_s3_style_store_needs_a_cache_to_fetch_into()
    {
        var store = Substitute.For<IObjectStore>();
        using var uncached = new DbnArchive(store, null);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            uncached.FetchAsync(new StoredObject(Root, 1, ""), null, CancellationToken.None));
    }

    [Fact]
    public void An_s3_archive_is_opened_from_its_address_with_or_without_a_cache()
    {
        var cache = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var options = new ArchiveOptions { Location = "http://localhost:1", AccessKey = "a", SecretKey = "s" };

        try
        {
            using var cached = DbnArchive.Open(options, cache);
            using var uncached = DbnArchive.Open(options, null);

            cached.Cache.ShouldNotBeNull();
            cached.Store.ShouldBeOfType<S3ObjectStore>();
            uncached.Cache.ShouldBeNull();
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }

    [Fact]
    public async Task Days_are_listed_oldest_first_with_their_empty_markers()
    {
        var days = (await archive.ListDaysAsync(Mbo, Root, CancellationToken.None)).Value;

        days.Select(d => d.Day).ShouldBe([Day(10, 30), Day(10, 31), Day(11, 2)]);
        days.Select(d => d.Empty).ShouldBe([false, true, false]);
    }

    [Fact]
    public async Task Keys_that_are_not_days_are_ignored()
    {
        Write($"{DbnSyncLayout.Folder(Dataset, Mbo, Root)}2026/notes.txt", [1]);

        var days = (await archive.ListDaysAsync(Mbo, Root, CancellationToken.None)).Value;

        days.Count.ShouldBe(3);
    }

    [Fact]
    public async Task A_series_of_nothing_but_empty_days_has_no_first_or_last()
    {
        Write(DbnSyncLayout.Empty(Dataset, Trades, Root, Day(10, 31)), []);

        var series = (await archive.ListSeriesAsync(CancellationToken.None)).Value;

        series.ShouldContain(new SeriesSummary(Trades, Root, 0, 1, 0, null, null));
    }

    [Fact]
    public async Task A_day_opens_as_a_reader_or_says_why_not()
    {
        await using var reader = (await archive.OpenAsync(Definition, Root, Day(10, 30), CancellationToken.None)).Value;

        reader.Metadata.Dataset.ShouldBe(Dataset);
        (await archive.OpenAsync(Mbo, Root, Day(10, 31), CancellationToken.None)).Error!.Code.ShouldBe(Errors.NoData);
    }

    [Fact]
    public async Task Instruments_are_read_by_id_and_other_records_are_ignored()
    {
        Write(DbnSyncLayout.Data(Dataset, Definition, Root, Day(11, 2)), DbnBytes.Compress(
            DbnBytes.Metadata(Dataset, ["MNQ.FUT"], new Dictionary<string, uint>()),
            DbnBytes.Status(FrontId, DbnBytes.Start, 7, 0, 0, 'Y'),
            DbnBytes.Definition(FrontId, DbnBytes.Start, FrontRaw, Root, DefinitionRecord.Future, QuarterPoint, Listed, Expires, 2026, 12)));

        var instruments = (await archive.InstrumentsAsync(Root, Day(11, 2), CancellationToken.None)).Value;

        instruments.Keys.ShouldBe([FrontId]);
    }

    [Fact]
    public async Task Contracts_are_sampled_from_each_months_first_day_and_the_last_day()
    {
        const uint OctoberOnly = 1;
        const uint LastDayOnly = 2;
        const uint NeverSampled = 3;
        byte[] Listing(uint id) => DbnBytes.Compress(
            DbnBytes.Metadata(Dataset, ["MCL.FUT"], new Dictionary<string, uint>()),
            DbnBytes.Definition(id, DbnBytes.Start, $"MCLZ{id}", OtherRoot, DefinitionRecord.Future, QuarterPoint, Listed, Expires, 2026, 12));
        Write(DbnSyncLayout.Data(Dataset, Definition, OtherRoot, Day(10, 5)), Listing(OctoberOnly));
        Write(DbnSyncLayout.Data(Dataset, Definition, OtherRoot, Day(10, 6)), Listing(NeverSampled));
        Write(DbnSyncLayout.Empty(Dataset, Definition, OtherRoot, Day(10, 7)), []);
        Write(DbnSyncLayout.Data(Dataset, Definition, OtherRoot, Day(10, 8)), Listing(LastDayOnly));
        var progress = Substitute.For<IProgress<long>>();

        var contracts = (await archive.ContractsAsync(OtherRoot, progress, CancellationToken.None)).Value;

        contracts.Select(c => c.InstrumentId).ShouldBe([OctoberOnly, LastDayOnly], ignoreOrder: true);
        progress.Received(2).Report(1);
    }

    [Fact]
    public async Task Contracts_run_by_expiry_then_by_name()
    {
        const uint First = 1;
        const uint Second = 2;
        const uint Third = 3;
        Write(DbnSyncLayout.Data(Dataset, Definition, OtherRoot, Day(10, 5)), DbnBytes.Compress(
            DbnBytes.Metadata(Dataset, ["MCL.FUT"], new Dictionary<string, uint>()),
            DbnBytes.Definition(Third, DbnBytes.Start, "MCLZ7", OtherRoot, DefinitionRecord.Future, QuarterPoint, Listed, Expires + 1, 2027, 12),
            DbnBytes.Definition(Second, DbnBytes.Start, "MCLZ6", OtherRoot, DefinitionRecord.Future, QuarterPoint, Listed, Expires, 2026, 12),
            DbnBytes.Definition(First, DbnBytes.Start, "MCLH6", OtherRoot, DefinitionRecord.Future, QuarterPoint, Listed, Expires, 2026, 3)));

        var contracts = (await archive.ContractsAsync(OtherRoot, null, CancellationToken.None)).Value;

        contracts.Select(c => c.InstrumentId).ShouldBe([First, Second, Third]);
    }

    [Fact]
    public async Task A_root_with_no_definitions_has_no_contracts()
    {
        (await archive.ContractsAsync(OtherRoot, null, CancellationToken.None)).Value.ShouldBeEmpty();
    }

    [Fact]
    public async Task Contracts_come_from_the_definitions_with_two_digit_years()
    {
        var contracts = (await archive.ContractsAsync(Root, null, CancellationToken.None)).Value;

        contracts.Count.ShouldBe(2);
        var front = contracts.Single(c => c.InstrumentId == FrontId);
        front.Symbol.ShouldBe("MNQZ26");
        front.RawSymbol.ShouldBe(FrontRaw);
        front.IsSpread.ShouldBeFalse();
        front.TickSize.ShouldBe(0.25m);
        contracts.Single(c => c.InstrumentId == SpreadId).Symbol.ShouldBe("MNQZ26-MNQH27");
    }

    private static byte[] Compressed(params byte[][] records) =>
        DbnBytes.Compress([DbnBytes.Metadata(Dataset, ["MNQ.FUT"], new Dictionary<string, uint>()), .. records]);

    private string PathOf(string key) => Path.Combine(folder, key.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public async Task A_day_that_cannot_be_opened_is_a_result_naming_the_file()
    {
        var opened = await archive.OpenAsync(Mbo, Root, Day(10, 30), CancellationToken.None);

        opened.Error!.Code.ShouldBe(Errors.Corrupt);
        opened.Error.Message.ShouldContain(PathOf(DbnSyncLayout.Data(Dataset, Mbo, Root, Day(10, 30))));
    }

    [Fact]
    public async Task A_folder_archive_says_a_pull_will_not_mend_its_file()
    {
        var opened = await archive.OpenAsync(Mbo, Root, Day(10, 30), CancellationToken.None);

        opened.Error!.Message.ShouldContain(CopyOver);
        opened.Error.Message.ShouldNotContain(PullForce);
    }

    [Fact]
    public async Task A_cached_file_says_how_to_download_it_again()
    {
        using var source = new ArchiveFolder();
        source.Write(DbnSyncLayout.Data(Dataset, Mbo, Root, Day(10, 30)), [1, 2, 3]);
        using var cached = source.OpenFailing(_ => false, new Error(Errors.Denied, Errors.Denied));

        var opened = await cached.OpenAsync(Mbo, Root, Day(10, 30), CancellationToken.None);

        opened.Error!.Code.ShouldBe(Errors.Corrupt);
        opened.Error.Message.ShouldContain($"{PullForce} {Mbo} {Root} --from 2026-10-30 --force");
    }

    [Fact]
    public async Task Damage_past_the_metadata_comes_from_the_reader_as_an_archive_exception()
    {
        var record = DbnBytes.Mbo(FrontId, DbnBytes.Start, 1, 1, 'A', 'B', 1);
        Write(DbnSyncLayout.Data(Dataset, Mbo, Root, Day(10, 30)), Compressed(record[..30]));
        var opened = await archive.OpenAsync(Mbo, Root, Day(10, 30), CancellationToken.None);
        await using var reader = opened.Value;

        var thrown = await Should.ThrowAsync<ArchiveException>(async () => await reader.ReadAsync(CancellationToken.None).ToListAsync());

        thrown.Error.Code.ShouldBe(Errors.Corrupt);
    }

    [Fact]
    public async Task Definitions_that_cannot_be_opened_are_a_result()
    {
        Write(DbnSyncLayout.Data(Dataset, Definition, Root, Day(10, 30)), [1, 2, 3]);

        var instruments = await archive.InstrumentsAsync(Root, Day(10, 30), CancellationToken.None);

        instruments.Error!.Code.ShouldBe(Errors.Corrupt);
    }

    [Fact]
    public async Task Definitions_damaged_part_way_through_are_a_result()
    {
        var record = DbnBytes.Definition(FrontId, DbnBytes.Start, FrontRaw, Root, DefinitionRecord.Future, QuarterPoint, Listed, Expires, 2026, 12);
        Write(DbnSyncLayout.Data(Dataset, Definition, Root, Day(10, 30)), Compressed(record, record[..30]));

        var instruments = await archive.InstrumentsAsync(Root, Day(10, 30), CancellationToken.None);
        var contracts = await archive.ContractsAsync(Root, null, CancellationToken.None);

        instruments.Error!.Code.ShouldBe(Errors.Corrupt);
        contracts.Error!.Code.ShouldBe(Errors.Corrupt);
    }

    private const string CopyOver = "copy a good file over it";
    private const string PullForce = "dbnf pull";
}
