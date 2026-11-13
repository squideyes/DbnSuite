using DbnSuite.Client.Archive;
using DbnSuite.Client.Contracts;
using DbnSuite.Client.Dbn;
using DbnSuite.Client.Export;
using DbnSuite.Client.Time;
using DbnSuite.IntegrationTests.Support;
using Parquet;
using Shouldly;
using static DbnSuite.IntegrationTests.Support.Sample;

namespace DbnSuite.IntegrationTests;

/// <summary>The other end of the archive: what the worker wrote, read back through S3 with DbnClient.</summary>
[Collection(ArchiveCollection.Name)]
public sealed class DbnClientReadBackTests(RustFs s3) : IDisposable
{
    private const string Prefix = "desk/archive";
    private const string Trades = "trades";

    private static readonly DateSpan AllDates = new(FirstDay, LastDay);

    private readonly string work = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    private string Output => Path.Combine(work, "output");

    public void Dispose()
    {
        if (Directory.Exists(work))
            Directory.Delete(work, recursive: true);
    }

    private async Task<SyncScenario> ArchiveAsync(string prefix = "")
    {
        var sync = await SyncScenario.CreateAsync(s3, prefix);

        sync.Keeping(Mnq, $"{Mbo},{Ohlcv},{Definition}");

        await sync.FirstPassAsync();
        await sync.NextPassAsync();

        return sync;
    }

    private DbnArchive Open(SyncScenario sync) => DbnArchive.Open(new ArchiveOptions
    {
        Location = s3.Endpoint,
        Bucket = sync.Archive.Bucket,
        Prefix = sync.Archive.Prefix,
        AccessKey = RustFs.AccessKey,
        SecretKey = RustFs.SecretKey
    }, Path.Combine(work, "cache"));

    private ExportRequest Export(string schema) => new()
    {
        Schema = schema,
        Symbol = SymbolSpec.Parse(BatchFactory.FrontContract(Mnq)).Value,
        TradeDates = AllDates,
        Sessions = SessionFilter.Create(AllDates, SessionKind.Eth, Mnq).Value,
        Output = Output,
        Layout = FileLayout.Single
    };

    [Fact]
    public async Task The_archive_is_recognised_and_every_series_listed_with_what_the_worker_stored()
    {
        await using var sync = await ArchiveAsync();
        using var archive = Open(sync);

        var layout = await archive.LayoutAsync(CancellationToken.None);
        var series = (await archive.ListSeriesAsync(CancellationToken.None)).Value;

        layout.Value.ShouldBe(DbnSyncLayout.Name);
        series.Select(s => s.Schema).ShouldBe([Definition, Mbo, Ohlcv], ignoreOrder: true);
        var job = sync.Gateway.Jobs.Single(j => j.Spec.Schema == Mbo);
        series.ShouldContain(new SeriesSummary(Mbo, Mnq, TradingDays.Length, 2,
            TradingDays.Sum(d => job.DataFile(d).Bytes.Length), FirstDay, LastDay));
    }

    [Fact]
    public async Task A_stored_day_comes_back_byte_for_byte()
    {
        await using var sync = await ArchiveAsync();
        using var archive = Open(sync);

        var path = await archive.FetchAsync(Mbo, Mnq, Monday, null, CancellationToken.None);

        (await File.ReadAllBytesAsync(path.Value)).ShouldBe(
            sync.Gateway.Jobs.Single(j => j.Spec.Schema == Mbo).DataFile(Monday).Bytes);
    }

    [Fact]
    public async Task A_weekend_reads_as_no_data_and_a_day_never_fetched_as_not_stored()
    {
        await using var sync = await ArchiveAsync();
        using var archive = Open(sync);

        var weekend = await archive.FetchAsync(Mbo, Mnq, Saturday, null, CancellationToken.None);
        var unfetched = await archive.FetchAsync(Mbo, Mnq, FirstDay.AddDays(-1), null, CancellationToken.None);

        weekend.Error!.Code.ShouldBe(DbnSuite.Client.Errors.NoData);
        unfetched.Error!.Code.ShouldBe(DbnSuite.Client.Errors.NotStored);
    }

    [Fact]
    public async Task Records_decode_from_the_stored_file()
    {
        await using var sync = await ArchiveAsync();
        using var archive = Open(sync);
        var trades = 0;

        await using var reader = (await archive.OpenAsync(Mbo, Mnq, Monday, CancellationToken.None)).Value;

        await foreach (var record in reader.ReadAsync(CancellationToken.None))
            trades += record is MboRecord { Action: 'T' } ? 1 : 0;

        trades.ShouldBe(BatchFactory.TradesPerDay);
    }

    [Fact]
    public async Task Contracts_come_from_the_definitions_the_worker_stored()
    {
        await using var sync = await ArchiveAsync();
        using var archive = Open(sync);

        var contracts = (await archive.ContractsAsync(Mnq, null, CancellationToken.None)).Value;

        contracts.Single(c => c.InstrumentId == BatchFactory.FrontId).Symbol.ShouldBe(BatchFactory.FrontContract(Mnq));
        contracts.Count(c => c.IsSpread).ShouldBe(1);
    }

    [Fact]
    public async Task Bars_export_to_csv_for_one_contract_and_leave_out_the_spread()
    {
        await using var sync = await ArchiveAsync();
        using var archive = Open(sync);

        var export = await new Exporter(archive).RunAsync(Export(Ohlcv), null, CancellationToken.None);

        export.Value.Rows.ShouldBe(BatchFactory.BarsPerDay * TradingDays.Length);
        var lines = await File.ReadAllLinesAsync(export.Value.Files.Single());
        lines[0].ShouldBe("ts_event,symbol,instrument_id,open,high,low,close,volume");
        lines[1].ShouldContain($"{BatchFactory.FrontContract(Mnq)},{BatchFactory.FrontId},{BatchFactory.OpenPrice}");
    }

    [Fact]
    public async Task Mbo_exports_as_trades_with_a_day_never_fetched_reported_missing()
    {
        await using var sync = await ArchiveAsync();
        using var archive = Open(sync);

        var export = await new Exporter(archive).RunAsync(Export(Mbo) with { As = Trades }, null, CancellationToken.None);

        export.Value.Rows.ShouldBe(BatchFactory.TradesPerDay * TradingDays.Length);
        export.Value.MissingDays.ShouldBe([FirstDay.AddDays(-1)]);
    }

    [Fact]
    public async Task A_parquet_export_opens_in_an_independent_reader()
    {
        await using var sync = await ArchiveAsync();
        using var archive = Open(sync);
        var request = Export(Ohlcv) with { Format = OutputFormat.Parquet };

        var export = await new Exporter(archive).RunAsync(request, null, CancellationToken.None);

        await using var file = File.OpenRead(export.Value.Files.Single());
        await using var reader = await ParquetReader.CreateAsync(file);
        using var group = reader.OpenRowGroupReader(0);
        group.RowCount.ShouldBe(export.Value.Rows);
    }

    [Fact]
    public async Task An_archive_kept_under_a_prefix_reads_the_same()
    {
        await using var sync = await ArchiveAsync(Prefix);
        using var archive = Open(sync);

        var days = (await archive.ListDaysAsync(Mbo, Mnq, CancellationToken.None)).Value;

        days.Select(d => d.Day).ShouldBe([FirstDay, Saturday, Sunday, Monday, LastDay]);
        days.Where(d => d.Empty).Select(d => d.Day).ShouldBe([Saturday, Sunday]);
    }
}
