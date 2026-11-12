using System.IO.Compression;
using System.Text;
using DbnClient.Contracts;
using DbnClient.Export;
using DbnClient.Time;
using Parquet;
using Shouldly;

namespace DbnSync.UnitTests.ClientTests;

/// <summary>File names and rollover, against the layout's "File names" table.</summary>
public sealed class OutputFilesTests : IDisposable
{
    private const string Es = "ES";
    private const string Csv = "csv";
    private const string Bars5m = "5m";
    private const string Continuous = "ES.v.0";
    private const string Partitioned = "MNQ.n.0";
    private const string Contract = "ESZ26";
    private const string Close = "close";
    private const string Volume = "volume";
    private const string Time = "ts_event";

    private static readonly DateOnly First = new(2026, 10, 29);
    private static readonly DateOnly Second = new(2026, 10, 30);
    private static readonly DateOnly RangeStart = new(2026, 1, 1);

    private static readonly Column[] All =
    [
        new(Time, CellKind.Time), new(Close, CellKind.Price), new(Volume, CellKind.Integer)
    ];

    private static readonly Cell[] Row = [Cell.Of(1_793_194_200_000_000_000UL), Cell.Of(100.25m), Cell.Of(7L)];

    private static readonly Cell[] BarRow =
    [
        Cell.Of(1_793_194_200_000_000_000UL), Cell.Of(1_793_194_260_000_000_000UL), Cell.Of(Contract),
        Cell.Of(100.25m), Cell.Of(101m), Cell.Of(100m), Cell.Of(100.5m), Cell.Of(7L)
    ];

    private readonly string folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public void Dispose()
    {
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    private ExportRequest Request(string symbol, OutputFormat format = OutputFormat.Csv, FileLayout layout = FileLayout.PerDay, string? template = null,
        Compression compression = Compression.None)
    {
        var dates = new DateSpan(RangeStart, Second);

        return new ExportRequest
        {
            Schema = "ohlcv-1s",
            Symbol = SymbolSpec.Parse(symbol).Value,
            TradeDates = dates,
            Sessions = SessionFilter.Create(dates, SessionKind.Eth, Es).Value,
            Output = folder,
            Format = format,
            Layout = layout,
            NameTemplate = template,
            Compression = compression
        };
    }

    private async Task<IEnumerable<string>> Written(ExportRequest request, string kind, params DateOnly[] dates)
    {
        var ninjaTrader = request.Format == OutputFormat.NinjaTrader;
        var files = new OutputFiles(request, ninjaTrader ? BarBuilder.Columns(series: false, trades: false) : All, kind);

        await using (files)
        {
            foreach (var date in dates)
                await files.WriteAsync(date, ninjaTrader ? BarRow : Row, ReadOnlyMemory<byte>.Empty);

            await files.CompleteAsync();
        }

        return files.Files.Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/'));
    }

    [Fact]
    public async Task One_file_per_trade_date_by_default()
    {
        var names = await Written(Request(Continuous), Bars5m, First, Second);

        names.ShouldBe(["ES.v.0.5m.20261029.csv", "ES.v.0.5m.20261030.csv"]);
    }

    [Fact]
    public async Task Rows_of_one_date_share_a_file()
    {
        var names = await Written(Request(Continuous), Bars5m, First, First);

        names.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_single_file_is_named_for_the_range()
    {
        var names = await Written(Request(Continuous, OutputFormat.Parquet, FileLayout.Single), Bars5m, First, Second);

        names.ShouldBe(["ES.v.0.5m.20260101-20261030.parquet"]);
    }

    [Fact]
    public async Task Partitions_are_folders_a_table_reader_understands()
    {
        var names = await Written(Request(Partitioned, OutputFormat.Parquet, FileLayout.Partitioned), "mbp-10", Second);

        names.ShouldBe(["series=MNQ.n.0/date=2026-10-30/mbp-10.parquet"]);
    }

    [Fact]
    public async Task Compression_shows_in_the_extension()
    {
        var names = await Written(Request(Contract, compression: Compression.Gzip), Csv, First);

        names.ShouldBe(["ESZ26.csv.20261029.csv.gz"]);
    }

    [Theory]
    [InlineData("ESZ9", 2019, "ES 12-19")]
    [InlineData("ESZ9", 2026, "ES 12-29")]
    [InlineData("ESH0", 2019, "ES 03-20")]
    [InlineData("ESZ26", 2019, "ES 12-26")]
    public void A_one_digit_year_takes_its_decade_from_when_the_export_starts(string symbol, int fromYear, string name)
    {
        OutputFiles.NinjaTraderName(DbnClient.Contracts.SymbolSpec.Parse(symbol).Value, fromYear).ShouldBe(name);
    }

    [Theory]
    [InlineData(Contract, "ES 12-26.Last.txt")]
    [InlineData("ESZ6", "ES 12-26.Last.txt")]
    [InlineData("MESH27", "MES 03-27.Last.txt")]
    [InlineData("M2KU7", "M2K 09-27.Last.txt")]
    [InlineData("ESZ26-ESH27", "ESZ26-ESH27.Last.txt")]
    [InlineData(Es, "ES.Last.txt")]
    [InlineData(Continuous, "ES.v.0.Last.txt")]
    public async Task Ninjatrader_files_are_named_for_the_instrument(string symbol, string name)
    {
        var names = await Written(Request(symbol, OutputFormat.NinjaTrader), Bars5m, First);

        names.ShouldBe([name]);
    }

    [Fact]
    public async Task Ninjatrader_writes_one_file_across_dates()
    {
        var names = await Written(Request(Contract, OutputFormat.NinjaTrader), Bars5m, First, Second);

        names.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_template_fills_every_placeholder()
    {
        var request = Request(Continuous, template: "{series}_{kind}_{date}_{from}_{to}.{ext}");

        var names = await Written(request, Bars5m, First);

        names.ShouldBe(["ES.v.0_5m_20261029_20260101_20261030.csv"]);
    }

    [Fact]
    public async Task A_template_with_no_date_keeps_every_trade_date_in_its_one_file()
    {
        var request = Request(Contract, template: "all.{ext}");

        var files = new OutputFiles(request, All, Bars5m);

        await using (files)
        {
            await files.WriteAsync(First, Row, ReadOnlyMemory<byte>.Empty);
            await files.WriteAsync(Second, Row, ReadOnlyMemory<byte>.Empty);

            await files.CompleteAsync();
        }

        files.Files.ShouldHaveSingleItem();
        (await File.ReadAllLinesAsync(files.Files.Single())).Length.ShouldBe(3);
    }

    [Fact]
    public async Task A_template_wins_over_a_ninjatrader_name()
    {
        var request = Request(Contract, OutputFormat.NinjaTrader, template: "mine.{ext}");

        var names = await Written(request, Bars5m, First);

        names.ShouldBe(["mine.txt"]);
    }

    [Fact]
    public async Task A_template_wins_over_a_partition()
    {
        var request = Request(Contract, layout: FileLayout.Partitioned, template: "{date}.{ext}");

        var names = await Written(request, Bars5m, First);

        names.ShouldBe(["20261029.csv"]);
    }

    [Fact]
    public async Task No_rows_write_no_file()
    {
        var names = await Written(Request(Continuous), Bars5m);

        names.ShouldBeEmpty();
        Directory.Exists(folder).ShouldBeFalse();
    }

    [Fact]
    public async Task Rows_are_counted_across_files()
    {
        var files = new OutputFiles(Request(Continuous), All, Bars5m);

        await using (files)
        {
            await files.WriteAsync(First, Row, ReadOnlyMemory<byte>.Empty);
            await files.WriteAsync(Second, Row, ReadOnlyMemory<byte>.Empty);
            await files.WriteAsync(Second, Row, ReadOnlyMemory<byte>.Empty);

            await files.CompleteAsync();
        }

        files.Rows.ShouldBe(3);
    }

    [Fact]
    public async Task Columns_chosen_are_written_in_the_order_chosen()
    {
        var request = Request(Contract) with { Columns = [Volume, Close] };
        var files = new OutputFiles(request, All, Csv);

        await using (files)
        {
            await files.WriteAsync(First, Row, ReadOnlyMemory<byte>.Empty);

            await files.CompleteAsync();
        }

        (await File.ReadAllLinesAsync(files.Files.Single())).ShouldBe(["volume,close", "7,100.25"]);
    }

    [Fact]
    public async Task Dbn_opens_with_the_metadata_it_was_given_and_copies_the_record()
    {
        byte[] metadata = [9, 8, 7];
        byte[] raw = [1, 2];
        var files = new OutputFiles(Request(Contract, OutputFormat.Dbn), All, Csv) { Metadata = metadata };

        await using (files)
        {
            await files.WriteAsync(First, Row, raw);

            await files.CompleteAsync();
        }

        (await File.ReadAllBytesAsync(files.Files.Single())).ShouldBe([.. metadata, .. raw]);
    }

    [Fact]
    public void Unknown_names_are_the_ones_not_among_the_columns()
    {
        OutputFiles.Unknown([Close, "nope", "nor_this"], All).ShouldBe(["nope", "nor_this"]);
    }

    [Fact]
    public void Nothing_asked_for_is_nothing_unknown()
    {
        OutputFiles.Unknown([], All).ShouldBeEmpty();
    }

    /// <summary>Three rows, the last going back to a date whose file was already closed.</summary>
    private async Task<OutputFiles> WrittenWithALateRow(ExportRequest request, byte[]? metadata = null)
    {
        var files = new OutputFiles(request, All, Csv) { Metadata = metadata };

        await using (files)
        {
            await files.WriteAsync(First, Numbered(1), Raw(1));
            await files.WriteAsync(Second, Numbered(2), Raw(2));
            await files.WriteAsync(First, Numbered(3), Raw(3));

            await files.CompleteAsync();
        }

        return files;
    }

    private static Cell[] Numbered(long volume) => [Row[0], Row[1], Cell.Of(volume)];

    private static byte[] Raw(byte value) => [value];

    private static string[] LinesOf(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);

        return reader.ReadToEnd().TrimEnd('\n').Split('\n');
    }

    [Theory]
    [InlineData(Compression.None)]
    [InlineData(Compression.Gzip)]
    [InlineData(Compression.Zstd)]
    public async Task A_late_row_is_added_to_its_csv_under_the_one_header(Compression compression)
    {
        var files = await WrittenWithALateRow(Request(Contract, compression: compression));

        await using var file = File.OpenRead(files.Files[0]);

        Stream stream = compression switch
        {
            Compression.Gzip => new GZipStream(file, CompressionMode.Decompress),
            Compression.Zstd => new ZstandardStream(file, CompressionMode.Decompress),
            _ => file
        };

        LinesOf(stream).Select(l => l[^1..]).ShouldBe(["e", "1", "3"]);
    }

    [Fact]
    public async Task A_late_row_is_added_to_its_json_lines()
    {
        var files = await WrittenWithALateRow(Request(Contract, OutputFormat.JsonLines));

        (await File.ReadAllLinesAsync(files.Files[0])).Length.ShouldBe(2);
    }

    [Fact]
    public async Task A_late_row_is_added_to_its_parquet_file_as_a_row_group()
    {
        var files = await WrittenWithALateRow(Request(Contract, OutputFormat.Parquet));

        await using var file = File.OpenRead(files.Files[0]);
        await using var reader = await ParquetReader.CreateAsync(file);
        var volume = reader.Schema.GetDataFields().Single(f => f.Name == Volume);
        var read = new List<long?>();

        for (var group = 0; group < reader.RowGroupCount; group++)
        {
            using var rows = reader.OpenRowGroupReader(group);
            var values = new long?[1];

            await rows.ReadAsync<long>(volume, values);
            read.Add(values[0]);
        }

        reader.Metadata!.NumRows.ShouldBe(2);
        read.ShouldBe([1L, 3L]);
    }

    [Fact]
    public async Task A_late_row_is_added_to_its_dbn_copy_behind_the_one_metadata_block()
    {
        byte[] metadata = [9, 8, 7];

        var files = await WrittenWithALateRow(Request(Contract, OutputFormat.Dbn), metadata);

        (await File.ReadAllBytesAsync(files.Files[0])).ShouldBe([.. metadata, 1, 3]);
    }

    [Fact]
    public async Task A_late_row_is_added_to_a_ninjatrader_file_of_its_own_date()
    {
        var request = Request(Contract, OutputFormat.NinjaTrader, template: "{date}.{ext}");
        var files = new OutputFiles(request, BarBuilder.Columns(series: false, trades: false), Bars5m);

        await using (files)
        {
            await files.WriteAsync(First, BarRow, ReadOnlyMemory<byte>.Empty);
            await files.WriteAsync(Second, BarRow, ReadOnlyMemory<byte>.Empty);
            await files.WriteAsync(First, BarRow, ReadOnlyMemory<byte>.Empty);

            await files.CompleteAsync();
        }

        (await File.ReadAllLinesAsync(files.Files[0])).Length.ShouldBe(2);
    }

    [Fact]
    public async Task A_late_row_is_added_to_its_partition()
    {
        var files = await WrittenWithALateRow(Request(Contract, layout: FileLayout.Partitioned));

        (await File.ReadAllLinesAsync(files.Files[0])).Length.ShouldBe(3);
    }

    [Fact]
    public async Task A_file_reopened_is_listed_once_and_every_row_is_counted()
    {
        var files = await WrittenWithALateRow(Request(Contract));

        files.Files.Count.ShouldBe(2);
        files.Rows.ShouldBe(3);
    }

    [Fact]
    public async Task A_file_from_before_this_export_is_still_replaced()
    {
        var request = Request(Contract, template: "all.{ext}");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "all.csv"), "left from another export\n");

        var files = await WrittenWithALateRow(request);

        (await File.ReadAllLinesAsync(files.Files[0])).Length.ShouldBe(4);
    }

    [Fact]
    public async Task Files_keep_their_real_names_until_the_export_finishes()
    {
        var request = Request(Contract, template: "all.{ext}");
        var files = new OutputFiles(request, All, Csv);

        await using (files)
        {
            await files.WriteAsync(First, Row, ReadOnlyMemory<byte>.Empty);
        }

        File.Exists(Path.Combine(folder, "all.csv")).ShouldBeFalse();
        Directory.GetFiles(folder).ShouldBeEmpty();
    }
}
