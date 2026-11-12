using DbnClient;
using DbnClient.Archive;
using DbnClient.Contracts;
using DbnClient.Dbn;
using DbnClient.Export;
using DbnClient.Time;
using NSubstitute;
using Parquet;
using Parquet.Schema;
using Shouldly;
using static DbnSync.UnitTests.TestData;

namespace DbnSync.UnitTests.ClientTests;

/// <summary>Exports from a folder archive of hand-made days, read back as each format writes them.</summary>
public sealed class ExporterTests : IDisposable
{
    private const string Es = "ES";
    private const string Ohlcv = "ohlcv-1s";
    private const uint FrontId = 101;
    private const uint SpreadId = 102;
    private const long Nano = 1_000_000_000;
    private const ulong Second = 1_000_000_000;

    private readonly string archiveFolder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly string output = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly DbnArchive archive;

    /// <summary>08:30 Chicago on 2026-10-28.</summary>
    private readonly ulong rthOpen;

    public ExporterTests()
    {
        var rth = SessionFilter.Create(new DateSpan(Day(10, 28), Day(10, 28)), SessionKind.Rth, Es).Value;
        rthOpen = (ulong)((rth.Anchor(Day(10, 28)) - DateTimeOffset.UnixEpoch).Ticks * 100);

        // 08:30 and 08:30:30 fall in the first RTH minute; 08:29 is before it; the spread is another instrument.
        Write(DbnSyncLayout.Data(Dataset, Ohlcv, Es, Day(10, 28)), DbnBytes.Compress(
            DbnBytes.Metadata(Dataset, ["ES.FUT"], new Dictionary<string, uint>()),
            DbnBytes.Ohlcv(FrontId, rthOpen - 60 * Second, 6000 * Nano, 6000 * Nano, 6000 * Nano, 6000 * Nano, 9),
            DbnBytes.Ohlcv(FrontId, rthOpen, 6000 * Nano, 6002 * Nano, 5999 * Nano, 6001 * Nano, 10),
            DbnBytes.Ohlcv(SpreadId, rthOpen, 50 * Nano, 50 * Nano, 50 * Nano, 50 * Nano, 4),
            DbnBytes.Ohlcv(FrontId, rthOpen + 30 * Second, 6001 * Nano, 6003 * Nano, 6000 * Nano, 6002 * Nano, 5)));

        Write(DbnSyncLayout.Empty(Dataset, Ohlcv, Es, Day(10, 27)), []);

        var definitions = DbnBytes.Compress(
            DbnBytes.Metadata(Dataset, ["ES.FUT"], new Dictionary<string, uint>()),
            DbnBytes.Definition(FrontId, DbnBytes.Start, RawFront, Es, DefinitionRecord.Future, 250_000_000, 1, 1_797_000_000_000_000_000, 2026, 12),
            DbnBytes.Definition(SpreadId, DbnBytes.Start, RawSpread, Es, DefinitionRecord.Spread, 50_000_000, 1, 1_797_000_000_000_000_000, 2026, 12));

        Write(DbnSyncLayout.Data(Dataset, DbnArchive.DefinitionSchema, Es, Day(10, 28)), definitions);

        archive = DbnArchive.Open(new ArchiveOptions { Location = archiveFolder }, null);
    }

    public void Dispose()
    {
        archive.Dispose();
        folder.Dispose();

        foreach (var folder in new[] { archiveFolder, output })
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    private void Write(string key, byte[] bytes)
    {
        var path = Path.Combine(archiveFolder, key.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private ExportRequest Request(string symbol, SessionKind session, OutputFormat format = OutputFormat.Csv) => new()
    {
        Schema = Ohlcv,
        Symbol = SymbolSpec.Parse(symbol).Value,
        TradeDates = new DateSpan(Day(10, 28), Day(10, 28)),
        Sessions = SessionFilter.Create(new DateSpan(Day(10, 28), Day(10, 28)), session, Es).Value,
        Output = output,
        Format = format,
        Zone = Zones.Find(Zones.Default).Value
    };

    [Fact]
    public async Task A_contract_keeps_only_its_records_with_two_digit_names()
    {
        var summary = (await new Exporter(archive).RunAsync(Request("ESZ26", SessionKind.Eth), null, CancellationToken.None)).Value;

        summary.Rows.ShouldBe(3);
        var lines = await File.ReadAllLinesAsync(summary.Files.Single());
        lines[0].ShouldBe("ts_event,symbol,instrument_id,open,high,low,close,volume");
        lines[2].ShouldBe("2026-10-28T08:30:00.000000000-05:00,ESZ26,101,6000,6002,5999,6001,10");
    }

    [Fact]
    public async Task Definitions_are_exported_for_the_day_of_their_file_not_the_day_they_last_changed()
    {
        // As Databento writes them: received at the file day's midnight, last changed by the exchange days before.
        var midnight = (ulong)(new DateTimeOffset(2026, 10, 28, 0, 0, 0, TimeSpan.Zero) - DateTimeOffset.UnixEpoch).Ticks * 100;
        var changed = midnight - 4 * 86_400 * Second;

        Write(DbnSyncLayout.Data(Dataset, DbnArchive.DefinitionSchema, Es, Day(10, 28)), DbnBytes.Compress(
            DbnBytes.Metadata(Dataset, ["ES.FUT"], new Dictionary<string, uint>()),
            DbnBytes.Definition(FrontId, changed, RawFront, Es, DefinitionRecord.Future, 250_000_000, 1, 1_797_000_000_000_000_000, 2026, 12, midnight)));

        var request = Request("ESZ26", SessionKind.Eth) with { Schema = DbnArchive.DefinitionSchema };

        var summary = (await new Exporter(archive).RunAsync(request, null, CancellationToken.None)).Value;

        summary.Rows.ShouldBe(1);
    }

    [Fact]
    public async Task A_failed_export_leaves_no_file_that_looks_complete()
    {
        Write(DbnSyncLayout.Data(Dataset, Ohlcv, Es, Day(10, 29)), [1, 2, 3]);
        var span = new DateSpan(Day(10, 28), Day(10, 29));
        var request = Request(Es, SessionKind.Eth) with { TradeDates = span, Sessions = SessionFilter.Create(span, SessionKind.Eth, Es).Value };

        await Should.ThrowAsync<Exception>(async () =>
        {
            var result = await new Exporter(archive).RunAsync(request, null, CancellationToken.None);

            if (!result.IsSuccess)
                throw new InvalidDataException(result.Error!.Message);
        });

        (Directory.Exists(output) ? Directory.GetFiles(output, "*", SearchOption.AllDirectories) : []).ShouldBeEmpty();
    }

    [Fact]
    public async Task Rth_bars_start_at_the_open_and_skip_what_came_before()
    {
        var request = Request("ESZ26", SessionKind.Rth) with { Bars = BarSpec.Parse(OneMinute).Value };

        var summary = (await new Exporter(archive).RunAsync(request, null, CancellationToken.None)).Value;

        var lines = await File.ReadAllLinesAsync(summary.Files.Single());
        lines.Length.ShouldBe(2);
        lines[1].ShouldBe("2026-10-28T08:30:00.000000000-05:00,2026-10-28T08:31:00.000000000-05:00,ESZ26,6000,6003,5999,6002,15");
    }

    [Fact]
    public async Task A_root_without_spreads_leaves_them_out()
    {
        var request = Request(Es, SessionKind.Eth) with { Spreads = false };

        var summary = (await new Exporter(archive).RunAsync(request, null, CancellationToken.None)).Value;

        summary.Rows.ShouldBe(3);
    }

    [Fact]
    public async Task Prices_in_ticks_divide_by_the_tick_size()
    {
        var request = Request("ESZ26", SessionKind.Rth) with { Prices = PriceMode.Ticks, Columns = [Close] };

        var summary = (await new Exporter(archive).RunAsync(request, null, CancellationToken.None)).Value;

        (await File.ReadAllLinesAsync(summary.Files.Single())).ShouldBe(["close", "24004", "24008"]);
    }

    [Fact]
    public async Task Parquet_reads_back_in_an_independent_reader()
    {
        var request = Request("ESZ26", SessionKind.Eth, OutputFormat.Parquet);

        var summary = (await new Exporter(archive).RunAsync(request, null, CancellationToken.None)).Value;

        await using var file = File.OpenRead(summary.Files.Single());
        await using var reader = await ParquetReader.CreateAsync(file);
        var fields = reader.Schema.GetDataFields();
        var group = reader.OpenRowGroupReader(0);
        var close = new double?[3];
        var symbols = new string?[3];

        await group.ReadAsync<double>(fields.Single(f => f.Name == "close"), close);
        await group.ReadAsync(fields.Single(f => f.Name == "symbol"), symbols.AsMemory());

        reader.RowGroupCount.ShouldBe(1);
        close.ShouldBe([6000d, 6001d, 6002d]);
        symbols.ShouldBe(["ESZ26", "ESZ26", "ESZ26"]);
    }

    [Fact]
    public async Task Ninjatrader_needs_bars()
    {
        var result = await new Exporter(archive).RunAsync(Request("ESZ26", SessionKind.Eth, OutputFormat.NinjaTrader), null, CancellationToken.None);

        result.Error!.Code.ShouldBe("input.bad_format");
    }

    [Fact]
    public async Task A_dataset_without_known_trade_dates_is_refused()
    {
        using var other = DbnArchive.Open(new ArchiveOptions { Location = archiveFolder, Dataset = OtherDataset }, null);

        var result = await new Exporter(other).RunAsync(Request("ESZ26", SessionKind.Eth), null, CancellationToken.None);

        result.Error!.Code.ShouldBe(Errors.UnsupportedDataset);
        result.Error.Message.ShouldContain(OtherDataset);
    }

    [Fact]
    public async Task Rolls_of_a_dataset_without_known_trade_dates_are_refused()
    {
        using var other = DbnArchive.Open(new ArchiveOptions { Location = archiveFolder, Dataset = OtherDataset }, null);

        var result = await RollSchedule.BuildAsync(other, SymbolSpec.Parse("ES.v.0").Value,
            new DateSpan(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 30)), 0, null, CancellationToken.None);

        result.Error!.Code.ShouldBe(Errors.UnsupportedDataset);
    }

    private const string OtherDataset = "XNAS.ITCH";

    [Fact]
    public async Task An_unknown_column_is_refused_by_name()
    {
        var request = Request("ESZ26", SessionKind.Eth) with { Columns = ["nope"] };

        var result = await new Exporter(archive).RunAsync(request, null, CancellationToken.None);

        result.Error!.Message.ShouldContain("nope");
    }

    // The tests below export from an archive filled per test. In them, ESZ26 expires on Friday 18 December 2026,
    // so ES.c.0 rolls to ESH27 on Thursday the 10th.

    private const string Mbp1 = "mbp-1";
    private const string BboSeconds = "bbo-1s";
    private const string OneMinute = "1m";
    private const string MinuteBars = "ohlcv-1m";
    private const string OneDay = "1d";
    private const string OneWeek = "1w";
    private const string ByVolume = "ES.v.0";
    private const int DamagedAt = 30;
    private const long Early = 1;
    private const long NextSession = 2;
    private const long Late = 3;
    private const string BadAdjustment = "input.bad_adjustment";
    private const string Close = "close";
    private const string SymbolColumn = "symbol";
    private const string TsEvent = "ts_event";
    private const string TsEnd = "ts_end";
    private const string RawFront = "ESZ6";
    private const string RawNext = "ESH7";
    private const string RawSpread = "ESZ6-ESH7";
    private const string Spread = "ESZ26-ESH27";
    private const string Statistics = "statistics";
    private const string Status = "status";
    private const string Calendar = "ES.c.0";
    private const string Front = "ESZ26";
    private const string Next = "ESH27";
    private const string FrontSeries = "ES.n.0";
    private const string NextSeries = "ES.n.1";
    private const uint FrontNumber = 1;
    private const uint NextNumber = 2;
    private const uint SpreadNumber = 3;
    private const long Quarter = 250_000_000;
    private const ushort SettlementPrice = 3;

    private static readonly ulong Listed = DbnBytes.Utc(2025, 1, 1, 0);
    private static readonly ulong FrontExpires = DbnBytes.Utc(2026, 12, 18, 14, 30);
    private static readonly ulong NextExpires = DbnBytes.Utc(2027, 3, 19, 13, 30);

    /// <summary>A Wednesday, with the session open at 22:00 UTC the day before and regular hours from 13:30 UTC.</summary>
    private static readonly DateOnly October = new(2026, 10, 28);

    private static readonly byte[] NotZstd = [1, 2, 3, 4, 5, 6, 7, 8];

    /// <summary>17:30 in Chicago on Friday the 11th, which counts to the trade date of Saturday the 12th.</summary>
    private static readonly ulong FridayEvening = DbnBytes.Utc(2026, 12, 11, 23, 30);

    private static readonly Dictionary<string, uint> Series = new() { [FrontSeries] = FrontNumber, [NextSeries] = NextNumber };

    private readonly ArchiveFolder folder = new();

    private static DateOnly Dec(int day) => new(2026, 12, day);

    /// <summary>16:00 UTC, which is 10:00 in Chicago in December.</summary>
    private static ulong Morning(DateOnly day) => DbnBytes.Utc(day.Year, day.Month, day.Day, 16);

    private static ulong At(DateOnly day, int hour, int minute = 0, int second = 0) =>
        DbnBytes.Utc(day.Year, day.Month, day.Day, hour, minute, second);

    private void Define(DateOnly day) => folder.Add(Definition, Es, day,
        DbnBytes.Definition(FrontNumber, DbnBytes.Start, RawFront, Es, DefinitionRecord.Future, Quarter, Listed, FrontExpires, 2026, 12),
        DbnBytes.Definition(NextNumber, DbnBytes.Start, RawNext, Es, DefinitionRecord.Future, Quarter, Listed, NextExpires, 2027, 3),
        DbnBytes.Definition(SpreadNumber, DbnBytes.Start, RawSpread, Es, DefinitionRecord.Spread, Quarter, Listed, FrontExpires, 2026, 12));

    /// <summary>A one-second bar closing at <paramref name="close"/>, a point above and below it, with a volume of 5.</summary>
    private static byte[] Bar(uint id, ulong time, long close) =>
        DbnBytes.Ohlcv(id, time, close * Nano, (close + 1) * Nano, (close - 1) * Nano, close * Nano, 5);

    private static byte[] Settle(uint id, DateOnly day, long price) =>
        DbnBytes.Statistic(id, At(day, 22, 30), DbnTime.Undefined, price * Nano, StatRecord.UndefinedQuantity, SettlementPrice);

    private static byte[] Order(uint id, ulong received, long price, uint size, char action, char side, ulong order, byte flags = MboRecord.LastFlag) =>
        DbnBytes.Mbo(id, received, received, price * Nano, size, action, side, order, flags);

    /// <summary>ES.c.0 over the December roll: ESZ26 at 100 and ESH27 at 110, a day later at 101 and 111, settling a gap of 10 and a ratio of 1.1.</summary>
    private void SeedTheRoll()
    {
        folder.AddEmpty(Ohlcv, Es, Dec(8));
        Define(Dec(9));
        Define(Dec(10));
        folder.Add(Ohlcv, Es, Dec(9), Bar(FrontNumber, Morning(Dec(9)), 100), Bar(NextNumber, Morning(Dec(9)), 110));
        folder.Add(Ohlcv, Es, Dec(10), Bar(FrontNumber, Morning(Dec(10)), 101), Bar(NextNumber, Morning(Dec(10)), 111));
        folder.Add(Statistics, Es, Dec(9), Settle(FrontNumber, Dec(9), 100), Settle(NextNumber, Dec(9), 110));
    }

    private static byte[] Stat(uint id, ulong time, long quantity) =>
        DbnBytes.Statistic(id, time, DbnTime.Undefined, DbnPrice.Undefined, quantity, 9);

    private string FileOf(string schema, DateOnly day) => Path.Combine(folder.Location,
        DbnSyncLayout.Data(Dataset, schema, Es, day).Replace('/', Path.DirectorySeparatorChar));

    private ExportRequest Ask(string schema, string symbol, DateOnly from, DateOnly to, SessionKind session = SessionKind.Eth) => new()
    {
        Schema = schema,
        Symbol = SymbolSpec.Parse(symbol).Value,
        TradeDates = new DateSpan(from, to),
        Sessions = SessionFilter.Create(new DateSpan(from, to), session, Es).Value,
        Output = output
    };

    private async Task<Result<ExportSummary>> Try(ExportRequest request, IProgress<DateOnly>? progress = null)
    {
        using var opened = folder.Open();

        return await new Exporter(opened).RunAsync(request, progress, CancellationToken.None);
    }

    private async Task<ExportSummary> Export(ExportRequest request) => (await Try(request)).Value;

    private async Task<Error> Fail(ExportRequest request) => (await Try(request)).Error.ShouldNotBeNull();

    private static Task<string[]> Lines(string path) => File.ReadAllLinesAsync(path);

    private async Task<string[][]> AllLines(ExportSummary summary) =>
        await Task.WhenAll(summary.Files.Select(f => Lines(f)));

    [Fact]
    public async Task A_continuous_series_takes_the_contract_held_on_each_trade_date()
    {
        SeedTheRoll();

        var summary = await Export(Ask(Ohlcv, Calendar, Dec(9), Dec(10)));

        summary.Files.Select(Path.GetFileName).ShouldBe(["ES.c.0.ohlcv-1s.20261209.csv", "ES.c.0.ohlcv-1s.20261210.csv"]);
        var files = await AllLines(summary);
        files[0].ShouldBe([
            "ts_event,symbol,instrument_id,open,high,low,close,volume,series",
            "2026-12-09T16:00:00.000000000Z,ESZ26,1,100,101,99,100,5,ES.c.0"]);
        files[1][1].ShouldBe("2026-12-10T16:00:00.000000000Z,ESH27,2,111,112,110,111,5,ES.c.0");
    }

    [Fact]
    public async Task The_summary_lists_the_rolls_and_rows()
    {
        SeedTheRoll();

        var summary = await Export(Ask(Ohlcv, Calendar, Dec(9), Dec(10)));

        summary.Rows.ShouldBe(2);
        var roll = summary.Rolls.ShouldHaveSingleItem();
        roll.TradeDate.ShouldBe(Dec(10));
        roll.Gap.ShouldBe(10m);
    }

    [Fact]
    public async Task Difference_shifts_prices_before_the_roll_and_leaves_the_newest_contract_alone()
    {
        SeedTheRoll();

        var summary = await Export(Ask(Ohlcv, Calendar, Dec(9), Dec(10)) with { Adjustment = Adjustment.Difference });

        var files = await AllLines(summary);
        files[0][1].ShouldBe("2026-12-09T16:00:00.000000000Z,ESZ26,1,110,111,109,110,5,ES.c.0");
        files[1][1].ShouldBe("2026-12-10T16:00:00.000000000Z,ESH27,2,111,112,110,111,5,ES.c.0");
    }

    [Fact]
    public async Task Ratio_scales_prices_before_the_roll_and_leaves_the_newest_contract_alone()
    {
        SeedTheRoll();

        var summary = await Export(Ask(Ohlcv, Calendar, Dec(9), Dec(10)) with { Adjustment = Adjustment.Ratio });

        var files = await AllLines(summary);
        files[0][1].ShouldBe("2026-12-09T16:00:00.000000000Z,ESZ26,1,110,111.1,108.9,110,5,ES.c.0");
        files[1][1].ShouldBe("2026-12-10T16:00:00.000000000Z,ESH27,2,111,112,110,111,5,ES.c.0");
    }

    [Fact]
    public async Task Adjusted_ticks_are_of_the_contract_held()
    {
        SeedTheRoll();

        var summary = await Export(Ask(Ohlcv, Calendar, Dec(9), Dec(10)) with { Adjustment = Adjustment.Difference, Prices = PriceMode.Ticks, Columns = [Close] });

        var files = await AllLines(summary);
        files[0][1].ShouldBe("440");
        files[1][1].ShouldBe("444");
    }

    [Fact]
    public async Task Raw_prices_are_databentos_integers()
    {
        SeedTheRoll();

        var summary = await Export(Ask(Ohlcv, Calendar, Dec(9), Dec(9)) with { Prices = PriceMode.Raw, Columns = [Close] });

        (await AllLines(summary))[0][1].ShouldBe("100000000000");
    }

    [Fact]
    public async Task Ticks_of_an_instrument_with_no_definition_stop_the_export_with_a_reason()
    {
        folder.AddEmpty(Ohlcv, Es, Dec(8));
        folder.Add(Ohlcv, Es, Dec(9), Bar(FrontNumber, Morning(Dec(9)), 100));

        var failed = await Fail(Ask(Ohlcv, Es, Dec(9), Dec(9)) with { Prices = PriceMode.Ticks, Columns = [SymbolColumn, Close] });

        failed.Code.ShouldBe(Errors.NoTickSize);
        failed.Message.ShouldContain("2026-12-09");
    }

    [Fact]
    public async Task A_day_without_definitions_names_a_series_by_the_contract_it_holds()
    {
        SeedTheRoll();
        File.Delete(Path.Combine(folder.Location, DbnSyncLayout.Data(Dataset, Definition, Es, Dec(10)).Replace('/', Path.DirectorySeparatorChar)));

        var summary = await Export(Ask(Ohlcv, Calendar, Dec(10), Dec(10)) with { Columns = [SymbolColumn] });

        (await AllLines(summary))[0][1].ShouldBe(Next);
    }

    [Fact]
    public async Task A_bar_of_a_series_is_adjusted_as_its_trade_date_is()
    {
        SeedTheRoll();

        var summary = await Export(Ask(Ohlcv, Calendar, Dec(9), Dec(10)) with { Bars = BarSpec.Parse(OneDay).Value, Adjustment = Adjustment.Difference, Layout = FileLayout.Single });

        (await AllLines(summary))[0].ShouldBe([
            "ts_event,ts_end,symbol,open,high,low,close,volume,series",
            "2026-12-08T23:00:00.000000000Z,2026-12-09T22:00:00.000000000Z,ESZ26,110,111,109,110,5,ES.c.0",
            "2026-12-09T23:00:00.000000000Z,2026-12-10T22:00:00.000000000Z,ESH27,111,112,110,111,5,ES.c.0"]);
    }

    [Fact]
    public async Task A_week_a_series_rolls_in_is_two_bars_of_one_contract_each()
    {
        SeedTheRoll();

        var summary = await Export(Ask(Ohlcv, Calendar, Dec(9), Dec(10)) with { Bars = BarSpec.Parse(OneWeek).Value, Adjustment = Adjustment.Difference, Layout = FileLayout.Single });

        (await AllLines(summary))[0][1..].ShouldBe([
            "2026-12-08T23:00:00.000000000Z,2026-12-09T22:00:00.000000000Z,ESZ26,110,111,109,110,5,ES.c.0",
            "2026-12-09T23:00:00.000000000Z,2026-12-10T22:00:00.000000000Z,ESH27,111,112,110,111,5,ES.c.0"]);
    }

    [Fact]
    public async Task Days_never_fetched_are_listed_and_days_with_no_data_are_not()
    {
        folder.AddEmpty(Ohlcv, Es, Dec(8));
        folder.Add(Ohlcv, Es, Dec(10), Bar(FrontNumber, Morning(Dec(10)), 100));

        var summary = await Export(Ask(Ohlcv, Es, Dec(9), Dec(10)));

        summary.MissingDays.ShouldBe([Dec(9)]);
    }

    [Fact]
    public async Task An_archive_that_stops_answering_is_an_error_not_a_missing_day()
    {
        folder.Add(Ohlcv, Es, Dec(9), Bar(FrontNumber, Morning(Dec(9)), 100));
        using var failing = folder.OpenFailing(key => key.Contains("20261209", StringComparison.Ordinal),
            new Error(Errors.Unreachable, Errors.Unreachable));

        var result = await new Exporter(failing).RunAsync(Ask(Ohlcv, Es, Dec(9), Dec(9)), null, CancellationToken.None);

        result.Error!.Code.ShouldBe(Errors.Unreachable);
    }

    [Fact]
    public async Task Definitions_that_cannot_be_read_are_an_error_not_unnamed_rows()
    {
        folder.Add(Ohlcv, Es, Dec(9), Bar(FrontNumber, Morning(Dec(9)), 100));
        using var failing = folder.OpenFailing(key => key.Contains("/definition/", StringComparison.Ordinal),
            new Error(Errors.Denied, Errors.Denied));

        var result = await new Exporter(failing).RunAsync(Ask(Ohlcv, Es, Dec(9), Dec(9)), null, CancellationToken.None);

        result.Error!.Code.ShouldBe(Errors.Denied);
    }

    [Fact]
    public async Task A_roll_schedule_that_cannot_be_built_stops_the_export()
    {
        Define(Dec(9));
        using var failing = folder.OpenFailing(key => key.Contains("/statistics/", StringComparison.Ordinal),
            new Error(Errors.Unreachable, Errors.Unreachable));

        var result = await new Exporter(failing).RunAsync(Ask(Ohlcv, Calendar, Dec(9), Dec(9)), null, CancellationToken.None);

        result.Error!.Code.ShouldBe(Errors.Unreachable);
    }

    [Fact]
    public async Task Each_day_read_is_reported()
    {
        folder.AddEmpty(Ohlcv, Es, Dec(8));
        folder.Add(Ohlcv, Es, Dec(9), Bar(FrontNumber, Morning(Dec(9)), 100));
        var progress = Substitute.For<IProgress<DateOnly>>();

        (await Try(Ask(Ohlcv, Es, Dec(9), Dec(9)), progress)).IsSuccess.ShouldBeTrue();

        progress.Received(1).Report(Dec(9));
        progress.DidNotReceive().Report(Dec(8));
    }

    [Fact]
    public async Task Nothing_to_write_writes_no_file()
    {
        folder.Add(Ohlcv, Es, Dec(9));

        var summary = await Export(Ask(Ohlcv, Es, Dec(9), Dec(9)));

        summary.Rows.ShouldBe(0);
        summary.Files.ShouldBeEmpty();
    }

    [Fact]
    public async Task With_no_definitions_a_spread_cannot_be_told_from_an_outright_so_it_stays()
    {
        folder.Add(Ohlcv, Es, Dec(9), Bar(SpreadNumber, Morning(Dec(9)), 5));

        var summary = await Export(Ask(Ohlcv, Es, Dec(9), Dec(9)) with { Spreads = false });

        summary.Rows.ShouldBe(1);
    }

    [Fact]
    public async Task A_name_with_no_date_keeps_every_day_of_an_export()
    {
        Define(Dec(9));
        Define(Dec(10));
        folder.Add(Ohlcv, Es, Dec(9), Bar(FrontNumber, Morning(Dec(9)), 100));
        folder.Add(Ohlcv, Es, Dec(10), Bar(FrontNumber, Morning(Dec(10)), 101));

        var summary = await Export(Ask(Ohlcv, Front, Dec(9), Dec(10)) with { NameTemplate = "all.{ext}", Columns = [Close] });

        (await AllLines(summary)).ShouldHaveSingleItem().ShouldBe(["close", "100", "101"]);
    }

    [Fact]
    public async Task A_root_keeps_spreads_unless_told_not_to()
    {
        Define(Dec(9));
        folder.Add(Ohlcv, Es, Dec(9), Bar(FrontNumber, Morning(Dec(9)), 100), Bar(SpreadNumber, Morning(Dec(9)), 5));

        var kept = await Export(Ask(Ohlcv, Es, Dec(9), Dec(9)));
        var left = await Export(Ask(Ohlcv, Es, Dec(9), Dec(9)) with { Spreads = false });

        kept.Rows.ShouldBe(2);
        left.Rows.ShouldBe(1);
    }

    [Theory]
    [InlineData(RawFront)]
    [InlineData(Front)]
    public async Task A_contract_is_found_by_either_form_of_its_name(string name)
    {
        Define(Dec(9));
        folder.Add(Ohlcv, Es, Dec(9), Bar(FrontNumber, Morning(Dec(9)), 100), Bar(NextNumber, Morning(Dec(9)), 110));

        var summary = await Export(Ask(Ohlcv, name, Dec(9), Dec(9)));

        summary.Rows.ShouldBe(1);
    }

    [Fact]
    public async Task A_spread_is_a_contract_of_its_own()
    {
        Define(Dec(9));
        folder.Add(Ohlcv, Es, Dec(9), Bar(FrontNumber, Morning(Dec(9)), 100), Bar(SpreadNumber, Morning(Dec(9)), 5));

        var summary = await Export(Ask(Ohlcv, Spread, Dec(9), Dec(9)) with { Columns = [SymbolColumn, Close] });

        (await AllLines(summary))[0][1].ShouldBe("ESZ26-ESH27,5");
    }

    [Fact]
    public async Task An_mbo_record_belongs_to_the_trade_date_it_was_received_on()
    {
        var sessionOpen = At(October.AddDays(-1), 22);
        var justBefore = sessionOpen - 1;
        Define(October.AddDays(-1));
        folder.Add(Mbo, Es, October.AddDays(-1),
            DbnBytes.Mbo(FrontNumber, sessionOpen, justBefore, 100 * Nano, 1, 'A', 'B', 1, MboRecord.LastFlag),
            DbnBytes.Mbo(FrontNumber, justBefore, sessionOpen, 100 * Nano, 2, 'A', 'B', 2, MboRecord.LastFlag));

        var summary = await Export(Ask(Mbo, Front, October, October) with { Columns = ["order_id"] });

        (await AllLines(summary))[0].ShouldBe(["order_id", "2"]);
    }

    [Fact]
    public async Task Mbo_is_exported_field_by_field()
    {
        var time = At(October, 13, 30);
        Define(October);
        folder.Add(Mbo, Es, October,
            DbnBytes.Mbo(FrontNumber, time, time + 1, 100_250_000_000, 3, 'A', 'B', 77, MboRecord.LastFlag, channel: 4, sequence: 99));

        var summary = await Export(Ask(Mbo, Front, October, October));

        (await AllLines(summary))[0].ShouldBe([
            "ts_recv,ts_event,symbol,instrument_id,action,side,price,size,order_id,flags,channel_id,sequence",
            "2026-10-28T13:30:00.000000001Z,2026-10-28T13:30:00.000000000Z,ESZ26,1,A,B,100.25,3,77,128,4,99"]);
    }

    [Fact]
    public async Task An_mbo_series_keeps_the_instruments_its_files_map_to_that_name()
    {
        var time = At(October, 13, 30);
        Define(October);
        folder.Add(Mbo, Es, October, Series,
            Order(FrontNumber, time, 100, 1, 'A', 'B', 1),
            Order(NextNumber, time, 110, 1, 'A', 'B', 2));

        var summary = await Export(Ask(Mbo, NextSeries, October, October) with { Columns = [SymbolColumn, "order_id", "series"] });

        (await AllLines(summary))[0][1..].ShouldBe(["ESH27,2,ES.n.1"]);
    }

    [Fact]
    public async Task Rebuilt_trades_are_named_by_contract_and_series()
    {
        var time = At(October, 13, 30);
        Define(October);
        folder.Add(Mbo, Es, October, Series, Order(FrontNumber, time, 100, 2, 'T', 'A', 0));

        var summary = await Export(Ask(Mbo, FrontSeries, October, October) with { As = Trades });

        (await AllLines(summary))[0].ShouldBe([
            "ts_recv,ts_event,symbol,instrument_id,side,price,size,sequence,series",
            "2026-10-28T13:30:00.000000000Z,2026-10-28T13:30:00.000000000Z,ESZ26,1,A,100,2,0,ES.n.0"]);
        Path.GetFileName(summary.Files.Single()).ShouldBe("ES.n.0.trades.20261028.csv");
    }

    [Fact]
    public async Task Rebuilt_rows_of_a_root_leave_spreads_out_when_asked()
    {
        var time = At(October, 13, 30);
        Define(October);
        folder.Add(Mbo, Es, October, Order(FrontNumber, time, 100, 2, 'T', 'A', 0), Order(SpreadNumber, time, 5, 1, 'T', 'A', 0));

        var summary = await Export(Ask(Mbo, Es, October, October) with { As = Trades, Spreads = false, Columns = [SymbolColumn] });

        (await AllLines(summary)).SelectMany(l => l.Skip(1)).ShouldBe([Front]);
    }

    [Fact]
    public async Task With_no_definitions_rebuilt_rows_are_named_by_the_files_own_mapping()
    {
        folder.Add(Mbo, Es, October, Series, Order(FrontNumber, At(October, 13, 30), 100, 2, 'T', 'A', 0));

        var summary = await Export(Ask(Mbo, FrontSeries, October, October) with { As = Trades, Columns = [SymbolColumn] });

        (await AllLines(summary))[0][1].ShouldBe(FrontSeries);
    }

    [Fact]
    public async Task A_book_is_fed_from_before_the_hours_it_writes()
    {
        Define(October);
        folder.Add(Mbo, Es, October,
            Order(FrontNumber, At(October, 13), 100, 5, 'A', 'B', 1),
            Order(FrontNumber, At(October, 13, 31), 101, 4, 'A', 'A', 2));

        var summary = await Export(Ask(Mbo, Front, October, October, SessionKind.Rth) with { As = Mbp1, Columns = ["bid_px_00", "ask_px_00"] });

        (await AllLines(summary))[0].ShouldBe(["bid_px_00,ask_px_00", "100,101"]);
    }

    [Fact]
    public async Task Other_records_in_an_mbo_file_are_left_out_of_its_bars()
    {
        var time = At(October, 13, 30);
        Define(October);
        folder.Add(Mbo, Es, October,
            DbnBytes.Unknown(MboRecord.Type, 24, FrontNumber, time),
            Order(FrontNumber, time, 100, 2, 'T', 'A', 0));

        var summary = await Export(Ask(Mbo, Front, October, October) with { Bars = BarSpec.Parse(OneMinute).Value });

        summary.Rows.ShouldBe(1);
    }

    [Fact]
    public async Task Other_records_in_an_mbo_file_are_left_out_of_a_rebuild()
    {
        var time = At(October, 13, 30);
        Define(October);
        folder.Add(Mbo, Es, October,
            DbnBytes.Unknown(0x99, 24, FrontNumber, time),
            DbnBytes.Unknown(MboRecord.Type, 24, FrontNumber, time),
            Order(FrontNumber, time, 100, 2, 'T', 'A', 0));

        var summary = await Export(Ask(Mbo, Front, October, October) with { As = Trades });

        summary.Rows.ShouldBe(1);
    }

    [Fact]
    public async Task The_last_interval_of_an_export_is_written()
    {
        var time = At(October, 13, 30);
        Define(October);
        folder.Add(Mbo, Es, October, Order(FrontNumber, time, 100, 5, 'A', 'B', 1));

        var summary = await Export(Ask(Mbo, Front, October, October) with { As = BboSeconds });

        (await AllLines(summary))[0].ShouldBe([
            "ts_recv,symbol,instrument_id,price,size,bid_px_00,bid_sz_00,bid_ct_00,ask_px_00,ask_sz_00,ask_ct_00",
            "2026-10-28T13:30:01.000000000Z,ESZ26,1,,,100,5,1,,,"]);
    }

    [Fact]
    public async Task Intervals_of_two_instruments_never_reopen_a_finished_file()
    {
        var nextDay = October.AddDays(1);
        Define(October);
        Define(nextDay);
        folder.Add(Mbo, Es, October,
            Order(FrontNumber, At(October, 13, 30), 100, 5, 'A', 'B', 1),
            Order(NextNumber, At(October, 13, 30), 110, 5, 'A', 'B', 2));
        folder.Add(Mbo, Es, nextDay,
            Order(FrontNumber, At(nextDay, 13, 30), 100, 1, 'A', 'B', 3),
            Order(FrontNumber, At(nextDay, 13, 30, 1), 100, 1, 'A', 'B', 4),
            Order(NextNumber, At(nextDay, 13, 30, 5), 110, 1, 'A', 'B', 5));

        var summary = await Export(Ask(Mbo, Es, October, nextDay) with { As = BboSeconds, Columns = ["ts_recv", SymbolColumn] });

        summary.Files.Count.ShouldBe(2);
        (await AllLines(summary)).Select(lines => lines.Length - 1).ShouldBe([2, 3]);
    }

    [Fact]
    public async Task Bars_of_a_root_never_reopen_a_finished_file()
    {
        Define(Dec(9));
        Define(Dec(10));
        folder.Add(Ohlcv, Es, Dec(9), Bar(FrontNumber, Morning(Dec(9)), 100), Bar(NextNumber, Morning(Dec(9)), 110));
        folder.Add(Ohlcv, Es, Dec(10),
            Bar(FrontNumber, Morning(Dec(10)), 101),
            Bar(FrontNumber, Morning(Dec(10)) + 60 * Second, 102),
            Bar(NextNumber, Morning(Dec(10)) + 180 * Second, 111));

        var summary = await Export(Ask(Ohlcv, Es, Dec(9), Dec(10)) with { Bars = BarSpec.Parse(OneMinute).Value, Columns = [SymbolColumn, Close] });

        summary.Rows.ShouldBe(5);
        summary.Files.Count.ShouldBe(2);
        var files = await AllLines(summary);
        files[0][1..].ShouldBe(["ESZ26,100", "ESH27,110"]);
        files[1][1..].ShouldBe(["ESZ26,101", "ESZ26,102", "ESH27,111"]);
    }

    [Fact]
    public async Task Bars_from_mbo_count_its_trades_and_ignore_the_rest()
    {
        var time = At(October, 13, 30);
        Define(October);
        folder.Add(Mbo, Es, October,
            Order(FrontNumber, time, 100, 9, 'A', 'B', 1),
            Order(FrontNumber, time, 100, 1, 'T', 'A', 0),
            Order(FrontNumber, time + Second, 102, 2, 'T', 'A', 0),
            Order(FrontNumber, time + 2 * Second, 101, 4, 'T', 'B', 0));

        var summary = await Export(Ask(Mbo, Front, October, October) with { Bars = BarSpec.Parse("2t").Value });

        (await AllLines(summary))[0].ShouldBe([
            "ts_event,ts_end,symbol,open,high,low,close,volume,trades",
            "2026-10-28T13:30:00.000000000Z,2026-10-28T13:30:01.000000000Z,ESZ26,100,102,100,102,3,2",
            "2026-10-28T13:30:02.000000000Z,2026-10-28T13:30:02.000000000Z,ESZ26,101,101,101,101,4,1"]);
    }

    [Fact]
    public async Task Bars_from_an_mbo_series_are_kept_as_one_series()
    {
        var time = At(October, 13, 30);
        Define(October);
        folder.Add(Mbo, Es, October, Series, Order(FrontNumber, time, 100, 1, 'T', 'A', 0));

        var summary = await Export(Ask(Mbo, FrontSeries, October, October) with { Bars = BarSpec.Parse("1000v").Value, Columns = [SymbolColumn, "volume", "trades", "series"] });

        (await AllLines(summary))[0].ShouldBe(["symbol,volume,trades,series", "ESZ26,1,1,ES.n.0"]);
    }

    [Fact]
    public async Task A_bar_with_no_prices_is_passed_over()
    {
        Define(Dec(9));
        folder.Add(Ohlcv, Es, Dec(9),
            DbnBytes.Ohlcv(FrontNumber, Morning(Dec(9)), DbnPrice.Undefined, DbnPrice.Undefined, DbnPrice.Undefined, DbnPrice.Undefined, 0));

        var summary = await Export(Ask(Ohlcv, Front, Dec(9), Dec(9)) with { Bars = BarSpec.Parse(OneMinute).Value });

        summary.Rows.ShouldBe(0);
    }

    [Fact]
    public async Task Rth_bars_on_the_monday_after_the_clocks_change_open_at_half_past_eight()
    {
        var monday = new DateOnly(2026, 3, 9);
        var open = DbnBytes.Utc(2026, 3, 9, 13, 30);
        folder.Add(Definition, Es, monday, DbnBytes.Definition(FrontNumber, DbnBytes.Start, "ESH6", Es, DefinitionRecord.Future, Quarter, Listed, FrontExpires, 2026, 3));
        folder.Add(Ohlcv, Es, monday,
            Bar(FrontNumber, open - Second, 100),
            Bar(FrontNumber, open, 101));

        var request = Ask(Ohlcv, Es, monday, monday, SessionKind.Rth) with
        {
            Bars = BarSpec.Parse("5m").Value,
            Zone = Zones.Find(Zones.Default).Value,
            Columns = [TsEvent, TsEnd, Close]
        };

        var summary = await Export(request);

        (await AllLines(summary))[0][1..].ShouldBe(["2026-03-09T08:30:00.000000000-05:00,2026-03-09T08:35:00.000000000-05:00,101"]);
    }

    [Fact]
    public async Task The_daily_bar_of_a_clock_change_runs_an_hour_longer()
    {
        var sunday = new DateOnly(2026, 11, 1);
        folder.Add(Definition, Es, sunday, DbnBytes.Definition(FrontNumber, DbnBytes.Start, RawFront, Es, DefinitionRecord.Future, Quarter, Listed, FrontExpires, 2026, 12));
        folder.Add(Ohlcv, Es, sunday, Bar(FrontNumber, DbnBytes.Utc(2026, 11, 1, 18), 100));

        var summary = await Export(Ask(Ohlcv, Es, sunday, sunday) with { Bars = BarSpec.Parse(OneDay).Value, Columns = [TsEvent, TsEnd], Time = TimeMode.UnixNanos });

        var line = (await AllLines(summary))[0][1].Split(',');
        var hours = (long.Parse(line[1], System.Globalization.CultureInfo.InvariantCulture) - long.Parse(line[0], System.Globalization.CultureInfo.InvariantCulture)) / (3600 * Nano);
        hours.ShouldBe(24);
    }

    [Fact]
    public async Task Times_follow_the_zone_and_style_asked_for()
    {
        Define(Dec(9));
        folder.Add(Ohlcv, Es, Dec(9), Bar(FrontNumber, Morning(Dec(9)), 100));

        var summary = await Export(Ask(Ohlcv, Front, Dec(9), Dec(9)) with
        {
            Zone = Zones.Find("America/New_York").Value,
            SplitTime = true,
            Columns = [TsEvent]
        });

        (await AllLines(summary))[0].ShouldBe(["ts_event_date,ts_event_time", "2026-12-09,11:00:00.000000000"]);
    }

    [Fact]
    public async Task Json_lines_are_written_with_their_extension()
    {
        Define(Dec(9));
        folder.Add(Ohlcv, Es, Dec(9), Bar(FrontNumber, Morning(Dec(9)), 100));

        var summary = await Export(Ask(Ohlcv, Front, Dec(9), Dec(9)) with { Format = OutputFormat.JsonLines, Columns = [SymbolColumn, Close, "volume"] });

        Path.GetFileName(summary.Files.Single()).ShouldBe("ESZ26.ohlcv-1s.20261209.jsonl");
        (await AllLines(summary))[0].ShouldBe(["{\"symbol\":\"ESZ26\",\"close\":100,\"volume\":5}"]);
    }

    [Fact]
    public async Task Parquet_is_partitioned_into_folders()
    {
        Define(Dec(9));
        folder.Add(Ohlcv, Es, Dec(9), Bar(FrontNumber, Morning(Dec(9)), 100));

        var summary = await Export(Ask(Ohlcv, Front, Dec(9), Dec(9)) with { Format = OutputFormat.Parquet, Layout = FileLayout.Partitioned });

        var path = summary.Files.Single();
        Path.GetRelativePath(output, path).Replace('\\', '/').ShouldBe("series=ESZ26/date=2026-12-09/ohlcv-1s.parquet");
        await using var file = File.OpenRead(path);
        await using var reader = await ParquetReader.CreateAsync(file);
        reader.Metadata!.NumRows.ShouldBe(1);
    }

    [Fact]
    public async Task A_name_template_is_used_as_given()
    {
        Define(Dec(9));
        folder.Add(Ohlcv, Es, Dec(9), Bar(FrontNumber, Morning(Dec(9)), 100));

        var summary = await Export(Ask(Ohlcv, Front, Dec(9), Dec(9)) with { NameTemplate = "{series}-{kind}.{ext}", Compression = Compression.Gzip });

        Path.GetFileName(summary.Files.Single()).ShouldBe("ESZ26-ohlcv-1s.csv.gz");
    }

    [Fact]
    public async Task Dbn_output_is_a_filtered_copy_that_reads_back_unchanged()
    {
        Define(Dec(9));
        var kept = Bar(FrontNumber, Morning(Dec(9)), 100);
        folder.Add(Ohlcv, Es, Dec(9), kept, Bar(NextNumber, Morning(Dec(9)), 110));

        var summary = await Export(Ask(Ohlcv, Front, Dec(9), Dec(9)) with { Format = OutputFormat.Dbn, Compression = Compression.Zstd });

        var path = summary.Files.Single();
        path.ShouldEndWith(".dbn.zst");
        await using var reader = await DbnReader.OpenAsync(File.OpenRead(path), CancellationToken.None);
        reader.Metadata.Dataset.ShouldBe(Dataset);
        (await reader.ReadRawAsync(CancellationToken.None).Select(r => r.ToArray()).ToListAsync()).ShouldHaveSingleItem().ShouldBe(kept);
    }

    [Fact]
    public async Task Ninjatrader_bars_land_in_one_file_named_for_the_instrument()
    {
        Define(Dec(9));
        Define(Dec(10));
        folder.Add(Ohlcv, Es, Dec(9), Bar(FrontNumber, Morning(Dec(9)), 100));
        folder.Add(Ohlcv, Es, Dec(10), Bar(FrontNumber, Morning(Dec(10)), 101));

        var summary = await Export(Ask(Ohlcv, Front, Dec(9), Dec(10)) with
        {
            Format = OutputFormat.NinjaTrader,
            Bars = BarSpec.Parse(OneMinute).Value,
            Zone = Zones.Find(Zones.Default).Value
        });

        var path = summary.Files.Single();
        Path.GetFileName(path).ShouldBe("ES 12-26.Last.txt");
        (await File.ReadAllTextAsync(path)).ShouldBe("20261209 100100;100;101;99;100;5\r\n20261210 100100;101;102;100;101;5\r\n");
    }

    [Fact]
    public async Task Ninjatrader_ticks_come_from_rebuilt_trades()
    {
        Define(October);
        folder.Add(Mbo, Es, October, Order(FrontNumber, At(October, 13, 30), 100, 2, 'T', 'A', 0));

        var summary = await Export(Ask(Mbo, Front, October, October) with
        {
            Format = OutputFormat.NinjaTrader,
            As = Trades,
            Zone = Zones.Find(Zones.Default).Value
        });

        (await File.ReadAllTextAsync(summary.Files.Single())).ShouldBe("20261028 083000 0000000;100;2\r\n");
    }

    [Fact]
    public async Task Statistics_are_exported_with_their_names_and_empty_fields()
    {
        Define(Dec(9));
        folder.Add(Statistics, Es, Dec(9), Settle(FrontNumber, Dec(9), 100));

        var summary = await Export(Ask(Statistics, Front, Dec(9), Dec(9)));

        (await AllLines(summary))[0].ShouldBe([
            "ts_event,ts_ref,symbol,instrument_id,stat_type,price,quantity,update_action",
            "2026-12-09T22:30:00.000000000Z,,ESZ26,1,settlement_price,100,,1"]);
    }

    [Fact]
    public async Task Status_is_exported()
    {
        Define(Dec(9));
        folder.Add(Status, Es, Dec(9), DbnBytes.Status(FrontNumber, Morning(Dec(9)), 7, 2, 1, 'Y'));

        var summary = await Export(Ask(Status, Front, Dec(9), Dec(9)));

        (await AllLines(summary))[0].ShouldBe([
            "ts_event,symbol,instrument_id,action,reason,trading_event,is_trading",
            "2026-12-09T16:00:00.000000000Z,ESZ26,1,7,2,1,Y"]);
    }

    [Fact]
    public async Task Definitions_are_exported()
    {
        folder.Add(Definition, Es, Dec(9),
            DbnBytes.Definition(FrontNumber, Morning(Dec(9)), RawFront, Es, DefinitionRecord.Future, Quarter, 0, FrontExpires, 2026, 12));

        var summary = await Export(Ask(Definition, Front, Dec(9), Dec(9)));

        (await AllLines(summary))[0].ShouldBe([
            "ts_event,symbol,raw_symbol,instrument_id,asset,instrument_class,min_price_increment,activation,expiration",
            "2026-12-09T16:00:00.000000000Z,ESZ26,ESZ6,1,ES,F,0.25,,2026-12-18T14:30:00.000000000Z"]);
    }

    [Fact]
    public async Task A_schema_it_does_not_decode_is_exported_by_its_header()
    {
        Define(Dec(9));
        folder.Add(Mbp1, Es, Dec(9), DbnBytes.Unknown(0x01, 80, FrontNumber, Morning(Dec(9))));

        var summary = await Export(Ask(Mbp1, Front, Dec(9), Dec(9)));

        (await AllLines(summary))[0].ShouldBe([
            "ts_event,symbol,instrument_id,rtype",
            "2026-12-09T16:00:00.000000000Z,ESZ26,1,1"]);
    }

    [Fact]
    public async Task A_schema_it_does_not_decode_still_copies_to_dbn()
    {
        Define(Dec(9));
        var record = DbnBytes.Unknown(0x01, 80, FrontNumber, Morning(Dec(9)));
        folder.Add(Mbp1, Es, Dec(9), record);

        var summary = await Export(Ask(Mbp1, Front, Dec(9), Dec(9)) with { Format = OutputFormat.Dbn, Compression = Compression.Zstd });

        await using var reader = await DbnReader.OpenAsync(File.OpenRead(summary.Files.Single()), CancellationToken.None);
        (await reader.ReadRawAsync(CancellationToken.None).Select(r => r.ToArray()).ToListAsync()).ShouldHaveSingleItem().ShouldBe(record);
    }

    [Fact]
    public async Task End_of_day_bars_export_as_bars()
    {
        const string EndOfDay = "ohlcv-eod";
        const byte EndOfDayType = 0x24;
        Define(Dec(9));
        folder.Add(EndOfDay, Es, Dec(9),
            DbnBytes.Ohlcv(FrontNumber, Morning(Dec(9)), 100 * Nano, 101 * Nano, 99 * Nano, 100 * Nano, 5, EndOfDayType));

        var summary = await Export(Ask(EndOfDay, Front, Dec(9), Dec(9)));

        (await AllLines(summary))[0][1].ShouldBe("2026-12-09T16:00:00.000000000Z,ESZ26,1,100,101,99,100,5");
    }

    [Fact]
    public async Task A_record_of_another_type_in_a_decoded_schema_is_not_data()
    {
        Define(Dec(9));
        folder.Add(Ohlcv, Es, Dec(9), DbnBytes.Unknown(0x99, 24, FrontNumber, Morning(Dec(9))), Bar(FrontNumber, Morning(Dec(9)), 100));

        var summary = await Export(Ask(Ohlcv, Front, Dec(9), Dec(9)));

        summary.Rows.ShouldBe(1);
    }

    [Fact]
    public async Task Records_outside_the_hours_or_of_other_contracts_are_left_out()
    {
        Define(Dec(9));
        folder.Add(Ohlcv, Es, Dec(9), Bar(FrontNumber, At(Dec(9), 14), 100), Bar(FrontNumber, At(Dec(9), 15), 101));

        var summary = await Export(Ask(Ohlcv, Front, Dec(9), Dec(9), SessionKind.Rth) with { Columns = [Close] });

        (await AllLines(summary))[0].ShouldBe(["close", "101"]);
    }

    public static TheoryData<string, string, string?, string?, OutputFormat, string?, string> Refusals() => new()
    {
        { Mbo, "ES.v.0", null, null, OutputFormat.Csv, null, "input.bad_series" },
        { Mbo, "ES.c.0", null, null, OutputFormat.Csv, null, "input.bad_series" },
        { Mbo, "ES.n.2", null, null, OutputFormat.Csv, null, "input.bad_series" },
        { Ohlcv, Front, null, Trades, OutputFormat.Csv, null, "input.bad_as" },
        { Mbo, Front, OneMinute, Trades, OutputFormat.Csv, null, "input.bad_as" },
        { Mbo, Front, null, "ohlcv-1s", OutputFormat.Csv, null, "input.bad_as" },
        { Mbo, Front, null, Trades, OutputFormat.Dbn, null, "input.bad_format" },
        { Mbo, Front, null, Mbp1, OutputFormat.NinjaTrader, null, "input.bad_format" },
        { Ohlcv, Front, "500t", null, OutputFormat.Csv, null, "input.bad_bars" },
        { Ohlcv, Front, "1000v", null, OutputFormat.Csv, null, "input.bad_bars" },
        { Statistics, Front, OneMinute, null, OutputFormat.Csv, null, "input.bad_bars" },
        { Ohlcv, Front, OneMinute, null, OutputFormat.Dbn, null, "input.bad_format" },
        { Ohlcv, Front, null, null, OutputFormat.NinjaTrader, null, "input.bad_format" },
        { Ohlcv, Front, null, null, OutputFormat.Dbn, Close, "input.bad_format" },
        { Ohlcv, Front, OneMinute, null, OutputFormat.NinjaTrader, Close, "input.bad_format" },
        { Mbo, Front, null, Trades, OutputFormat.NinjaTrader, "price", "input.bad_format" },
        { Mbo, Front, null, Trades, OutputFormat.Csv, "nope", "input.bad_columns" },
        { Ohlcv, Front, OneMinute, null, OutputFormat.Csv, "nope", "input.bad_columns" }
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task A_request_that_cannot_be_met_is_refused_with_its_code(string schema, string symbol, string? bars, string? target,
        OutputFormat format, string? column, string code)
    {
        var request = Ask(schema, symbol, Dec(9), Dec(9)) with
        {
            Bars = bars is null ? null : BarSpec.Parse(bars).Value,
            As = target,
            Format = format,
            Columns = column is null ? [] : [column]
        };

        var result = await Try(request);

        result.Error!.Code.ShouldBe(code);
    }

    [Fact]
    public async Task An_adjusted_series_cannot_be_copied_to_dbn()
    {
        var result = await Try(Ask(Ohlcv, Calendar, Dec(9), Dec(9)) with { Format = OutputFormat.Dbn, Adjustment = Adjustment.Ratio });

        result.Error!.Code.ShouldBe("input.bad_format");
    }

    [Theory]
    [InlineData(OneMinute)]
    [InlineData(OneDay)]
    public async Task Bars_can_be_built_from_ohlcv_1m_in_whole_minutes(string size)
    {
        Define(Dec(9));
        folder.Add(MinuteBars, Es, Dec(9), Bar(FrontNumber, Morning(Dec(9)), 100));

        var summary = await Export(Ask(MinuteBars, Front, Dec(9), Dec(9)) with { Bars = BarSpec.Parse(size).Value });

        summary.Rows.ShouldBe(1);
    }

    [Theory]
    [InlineData("ohlcv-1h", OneDay)]
    [InlineData("ohlcv-1h", OneMinute)]
    [InlineData("ohlcv-1d", OneDay)]
    [InlineData(MinuteBars, "30s")]
    public async Task Bars_that_do_not_line_up_with_their_source_are_refused(string schema, string size)
    {
        var result = await Try(Ask(schema, Front, Dec(9), Dec(9)) with { Bars = BarSpec.Parse(size).Value });

        result.Error!.Code.ShouldBe("input.bad_bars");
    }

    [Fact]
    public async Task A_contract_on_a_day_without_definitions_counts_as_missing()
    {
        folder.Add(Ohlcv, Es, Dec(9), Bar(FrontNumber, Morning(Dec(9)), 100));

        var summary = await Export(Ask(Ohlcv, Front, Dec(9), Dec(9)));

        summary.Rows.ShouldBe(0);
        summary.MissingDays.ShouldContain(Dec(9));
    }

    [Fact]
    public async Task Each_days_dbn_file_carries_that_days_symbol_map()
    {
        var afterRoll = new Dictionary<string, uint> { [FrontSeries] = NextNumber };
        Define(Dec(9));
        Define(Dec(10));
        folder.Add(Mbo, Es, Dec(9), Series, Order(FrontNumber, Morning(Dec(9)), 100, 1, 'A', 'B', 1));
        folder.Add(Mbo, Es, Dec(10), afterRoll, Order(NextNumber, Morning(Dec(10)), 100, 1, 'A', 'B', 2));

        var summary = await Export(Ask(Mbo, FrontSeries, Dec(9), Dec(10)) with { Format = OutputFormat.Dbn, Compression = Compression.Zstd });

        await using var reader = await DbnReader.OpenAsync(File.OpenRead(summary.Files[^1]), CancellationToken.None);
        reader.Metadata.SymbolOf(NextNumber).ShouldBe(FrontSeries);
    }

    [Fact]
    public async Task A_series_over_several_days_is_not_put_in_one_dbn_file()
    {
        var result = await Try(Ask(Mbo, FrontSeries, Dec(9), Dec(10)) with { Format = OutputFormat.Dbn, Layout = FileLayout.Single });

        result.Error!.Code.ShouldBe("input.bad_format");
    }

    [Fact]
    public async Task An_unadjusted_mbo_series_can_be_copied_to_dbn()
    {
        Define(October);
        folder.Add(Mbo, Es, October, Series, Order(FrontNumber, At(October, 13, 30), 100, 1, 'A', 'B', 1));

        var summary = await Export(Ask(Mbo, FrontSeries, October, October) with { Format = OutputFormat.Dbn });

        summary.Rows.ShouldBe(1);
    }

    [Theory]
    [InlineData(null, null, Adjustment.Difference)]
    [InlineData(Trades, null, Adjustment.Difference)]
    [InlineData(Mbp1, null, Adjustment.Ratio)]
    [InlineData(null, OneMinute, Adjustment.Ratio)]
    public async Task An_adjusted_mbo_series_is_refused_as_order_book_events_keep_raw_prices(string? target, string? bars, Adjustment adjustment)
    {
        Define(October);
        folder.Add(Mbo, Es, October, Series, Order(FrontNumber, At(October, 13, 30), 100, 2, 'T', 'A', 0));

        var result = await Try(Ask(Mbo, FrontSeries, October, October) with
        {
            As = target,
            Bars = bars is null ? null : BarSpec.Parse(bars).Value,
            Adjustment = adjustment
        });

        result.Error!.Code.ShouldBe(BadAdjustment);
        result.Error.Message.ShouldContain(Mbo);
    }

    [Fact]
    public async Task A_contract_of_mbo_has_nothing_to_adjust_so_the_request_is_not_refused()
    {
        Define(October);
        folder.Add(Mbo, Es, October, Order(FrontNumber, At(October, 13, 30), 100, 2, 'T', 'A', 0));

        var summary = await Export(Ask(Mbo, Front, October, October) with { Adjustment = Adjustment.Difference });

        summary.Rows.ShouldBe(1);
    }

    [Fact]
    public async Task A_roll_with_no_settlement_stops_a_difference_adjusted_export_before_any_file_is_written()
    {
        SeedTheRoll();
        folder.Add(Statistics, Es, Dec(9), Settle(FrontNumber, Dec(9), 100));

        var result = await Try(Ask(Ohlcv, Calendar, Dec(9), Dec(10)) with { Adjustment = Adjustment.Difference });

        result.Error!.Code.ShouldBe(Errors.NoSettlement);
        result.Error.Message.ShouldContain("2026-12-10");
        result.Error.Message.ShouldContain(Front);
        result.Error.Message.ShouldContain(Next);
        Directory.Exists(output).ShouldBeFalse();
    }

    [Fact]
    public async Task A_roll_with_no_settlement_stops_a_ratio_adjusted_export_too()
    {
        SeedTheRoll();
        folder.Add(Statistics, Es, Dec(9), Settle(NextNumber, Dec(9), 110));

        var result = await Try(Ask(Ohlcv, Calendar, Dec(9), Dec(10)) with { Adjustment = Adjustment.Ratio });

        result.Error!.Code.ShouldBe(Errors.NoSettlement);
    }

    [Fact]
    public async Task A_settlement_of_zero_stops_a_ratio_but_not_a_difference()
    {
        SeedTheRoll();
        folder.Add(Statistics, Es, Dec(9), Settle(FrontNumber, Dec(9), 0), Settle(NextNumber, Dec(9), 110));

        var ratio = await Try(Ask(Ohlcv, Calendar, Dec(9), Dec(10)) with { Adjustment = Adjustment.Ratio });
        var difference = await Try(Ask(Ohlcv, Calendar, Dec(9), Dec(10)) with { Adjustment = Adjustment.Difference });

        ratio.Error!.Code.ShouldBe(Errors.NoSettlement);
        difference.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task A_roll_with_no_settlement_leaves_an_unadjusted_export_as_it_was()
    {
        SeedTheRoll();
        folder.Add(Statistics, Es, Dec(9), Settle(FrontNumber, Dec(9), 100));

        var summary = await Export(Ask(Ohlcv, Calendar, Dec(9), Dec(10)));

        summary.Rows.ShouldBe(2);
        summary.Rolls.ShouldHaveSingleItem().Gap.ShouldBeNull();
    }

    [Fact]
    public async Task A_roll_with_no_settlement_outside_the_range_is_not_needed()
    {
        SeedTheRoll();
        folder.Add(Statistics, Es, Dec(9), Settle(FrontNumber, Dec(9), 100));

        var summary = await Export(Ask(Ohlcv, Calendar, Dec(10), Dec(10)) with { Adjustment = Adjustment.Difference });

        summary.Rows.ShouldBe(1);
        summary.Rolls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_roll_decided_over_a_weekend_shows_monday_and_changes_no_exported_row()
    {
        foreach (var day in new[] { Dec(10), Dec(11), Dec(14) })
            Define(day);

        folder.Add(Ohlcv, Es, Dec(10), Bar(FrontNumber, Morning(Dec(10)), 100));
        folder.Add(Ohlcv, Es, Dec(11),
            Bar(FrontNumber, Morning(Dec(11)), 100),
            Bar(NextNumber, Morning(Dec(11)), 120),
            Bar(NextNumber, Morning(Dec(11)) + Second, 120),
            Bar(NextNumber, FridayEvening, 130));
        folder.Add(Ohlcv, Es, Dec(14), Bar(NextNumber, Morning(Dec(14)), 140));
        folder.Add(Statistics, Es, Dec(11), Settle(FrontNumber, Dec(11), 100), Settle(NextNumber, Dec(11), 110));

        var summary = await Export(Ask(Ohlcv, ByVolume, Dec(10), Dec(14)) with
        {
            Adjustment = Adjustment.Difference,
            Layout = FileLayout.Single,
            Columns = [SymbolColumn, Close]
        });

        summary.Rolls.ShouldHaveSingleItem().TradeDate.ShouldBe(Dec(14));
        (await Lines(summary.Files.Single())).ShouldBe(["symbol,close", "ESZ26,110", "ESZ26,110", "ESH27,130", "ESH27,140"]);
    }

    [Fact]
    public async Task A_file_damaged_part_way_through_is_a_result_naming_it()
    {
        Define(October);
        var record = Order(FrontNumber, At(October, 13, 30), 100, 2, 'T', 'A', 0);
        folder.Add(Mbo, Es, October, record, record[..DamagedAt]);

        var result = await Try(Ask(Mbo, Front, October, October));

        result.Error!.Code.ShouldBe(Errors.Corrupt);
        result.Error.Message.ShouldContain(FileOf(Mbo, October));
    }

    [Fact]
    public async Task A_file_that_cannot_even_be_opened_is_a_result_naming_it()
    {
        Define(October);
        folder.Write(DbnSyncLayout.Data(Dataset, Mbo, Es, October), NotZstd);

        var result = await Try(Ask(Mbo, Front, October, October));

        result.Error!.Code.ShouldBe(Errors.Corrupt);
        result.Error.Message.ShouldContain(FileOf(Mbo, October));
    }

    [Fact]
    public async Task Damaged_definitions_are_a_result_naming_them()
    {
        folder.Add(Mbo, Es, October, Order(FrontNumber, At(October, 13, 30), 100, 2, 'T', 'A', 0));
        folder.Write(DbnSyncLayout.Data(Dataset, Definition, Es, October), NotZstd);

        var result = await Try(Ask(Mbo, Front, October, October));

        result.Error!.Code.ShouldBe(Errors.Corrupt);
        result.Error.Message.ShouldContain(FileOf(Definition, October));
    }

    [Fact]
    public async Task A_record_whose_time_runs_back_over_a_trade_date_loses_nothing_in_the_finished_file()
    {
        var after = October.AddDays(1);
        Define(October);
        folder.Add(Statistics, Es, October,
            Stat(FrontNumber, At(October, 21), Early),
            Stat(FrontNumber, At(October, 22, 0, 1), NextSession),
            Stat(FrontNumber, At(October, 21, 59), Late));

        var summary = await Export(Ask(Statistics, Front, October, after) with { Columns = ["quantity"] });

        summary.Rows.ShouldBe(3);
        summary.Files.Select(Path.GetFileName).ShouldBe(["ESZ26.statistics.20261028.csv", "ESZ26.statistics.20261029.csv"]);
        (await AllLines(summary)).ShouldBe([["quantity", "1", "3"], ["quantity", "2"]]);
    }

    [Theory]
    [InlineData("1d", SessionKind.Rth, "20261028;100;101;99;100;5\r\n")]
    [InlineData("1d", SessionKind.Eth, "20261028;100;101;99;100;5\r\n")]
    [InlineData("1w", SessionKind.Rth, "20261028;100;101;99;100;5\r\n")]
    [InlineData("1mo", SessionKind.Eth, "20261028;100;101;99;100;5\r\n")]
    [InlineData("24h", SessionKind.Eth, "20261028;100;101;99;100;5\r\n")]
    [InlineData("12h", SessionKind.Eth, "20261028 160000;100;101;99;100;5\r\n")]
    [InlineData("1h", SessionKind.Rth, "20261028 093000;100;101;99;100;5\r\n")]
    public async Task Ninjatrader_stamps_by_bar_size_whatever_the_session(string size, SessionKind session, string line)
    {
        Define(October);
        folder.Add(Ohlcv, Es, October, Bar(FrontNumber, At(October, 13, 30), 100));

        var summary = await Export(Ask(Ohlcv, Front, October, October, session) with
        {
            Format = OutputFormat.NinjaTrader,
            Bars = BarSpec.Parse(size).Value,
            Zone = Zones.Find(Zones.Default).Value
        });

        (await File.ReadAllTextAsync(summary.Files.Single())).ShouldBe(line);
    }
}
