using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DbnClient.Export;
using DbnClient.Time;
using Shouldly;
using ZstdSharp;

namespace DbnSync.UnitTests.ClientTests;

/// <summary>Each format, written to a real file and read back as bytes.</summary>
public sealed class WritersTests : IDisposable
{
    private const string Symbol = "ESZ26";
    private const string Chicago = "America/Chicago";

    /// <summary>2026-10-28 13:30:00 UTC, which is 08:30 in Chicago.</summary>
    private const ulong T0 = 1_793_194_200_000_000_000;

    private const ulong Minute = 60_000_000_000;

    private static readonly Column[] Plain =
    [
        new("ts", CellKind.Time), new("symbol", CellKind.Text), new("px", CellKind.Price), new("n", CellKind.Integer)
    ];

    private static readonly Cell[] Row = [Cell.Of(T0), Cell.Of(Symbol), Cell.Of(100.25m), Cell.Of(7L)];

    private static readonly Cell[] EmptyRow = [Cell.Empty, Cell.Of((string?)null), Cell.Of((decimal?)null), Cell.Empty];

    private static readonly byte[] Metadata = [0x44, 0x42, 0x4E, 3];

    private static readonly byte[] Raw = [1, 2, 3, 4];

    private readonly string folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public void Dispose()
    {
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    private static CellFormat Utc(TimeMode time = TimeMode.Iso) => new(TimeZoneInfo.Utc, time, splitTime: false);

    private string PathOf(string name) => Path.Combine(folder, name);

    private async Task<byte[]> Written(OutputFormat format, IReadOnlyList<Column> columns, CellFormat cells, Compression compression,
        byte[]? metadata, params (Cell[] Cells, byte[] Raw)[] rows)
    {
        var path = PathOf(Path.GetRandomFileName());

        await using (var writer = Writers.Create(format, path, columns, cells, compression, metadata))
        {
            foreach (var (row, raw) in rows)
                writer.Write(row, raw);
        }

        return await File.ReadAllBytesAsync(path);
    }

    private async Task<string> Text(OutputFormat format, IReadOnlyList<Column> columns, CellFormat cells, params Cell[][] rows) =>
        Encoding.UTF8.GetString(await Written(format, columns, cells, Compression.None, null, [.. rows.Select(r => (r, Array.Empty<byte>()))]));

    [Theory]
    [InlineData(OutputFormat.Csv, Compression.None, "csv")]
    [InlineData(OutputFormat.Csv, Compression.Gzip, "csv.gz")]
    [InlineData(OutputFormat.Csv, Compression.Zstd, "csv.zst")]
    [InlineData(OutputFormat.JsonLines, Compression.None, "jsonl")]
    [InlineData(OutputFormat.JsonLines, Compression.Gzip, "jsonl.gz")]
    [InlineData(OutputFormat.JsonLines, Compression.Zstd, "jsonl.zst")]
    [InlineData(OutputFormat.Parquet, Compression.None, "parquet")]
    [InlineData(OutputFormat.Parquet, Compression.Gzip, "parquet")]
    [InlineData(OutputFormat.Dbn, Compression.None, "dbn")]
    [InlineData(OutputFormat.Dbn, Compression.Gzip, "dbn.gz")]
    [InlineData(OutputFormat.Dbn, Compression.Zstd, "dbn.zst")]
    [InlineData(OutputFormat.NinjaTrader, Compression.None, "txt")]
    [InlineData(OutputFormat.NinjaTrader, Compression.Zstd, "txt")]
    public void Extensions_name_the_format_and_its_compression(OutputFormat format, Compression compression, string extension)
    {
        Writers.Extension(format, compression).ShouldBe(extension);
    }

    [Fact]
    public async Task A_file_lands_in_a_folder_made_for_it()
    {
        var path = Path.Combine(folder, "a", "b", "file.csv");

        await using (Writers.Create(OutputFormat.Csv, path, Plain, Utc(), Compression.None, null))
        {
        }

        File.Exists(path).ShouldBeTrue();
    }

    [Fact]
    public async Task Csv_has_one_header_row_and_unix_line_endings()
    {
        var text = await Text(OutputFormat.Csv, Plain, Utc(), Row);

        text.ShouldBe("ts,symbol,px,n\n2026-10-28T13:30:00.000000000Z,ESZ26,100.25,7\n");
    }

    [Fact]
    public async Task Csv_has_no_byte_order_mark()
    {
        var bytes = await Written(OutputFormat.Csv, Plain, Utc(), Compression.None, null);

        bytes[0].ShouldBe((byte)'t');
    }

    [Fact]
    public async Task Csv_leaves_no_value_as_an_empty_field()
    {
        var text = await Text(OutputFormat.Csv, Plain, Utc(), EmptyRow);

        text.Split('\n')[1].ShouldBe(",,,");
    }

    [Theory]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("a\"b", "\"a\"\"b\"")]
    [InlineData("a\nb", "\"a\nb\"")]
    [InlineData("a\rb", "\"a\rb\"")]
    [InlineData("plain", "plain")]
    public async Task Csv_quotes_text_only_when_it_must(string value, string written)
    {
        Column[] columns = [new("text", CellKind.Text)];

        var text = await Text(OutputFormat.Csv, columns, Utc(), [Cell.Of(value)]);

        text.ShouldBe($"text\n{written}\n");
    }

    [Fact]
    public async Task Csv_writes_a_split_time_as_two_columns()
    {
        var format = new CellFormat(Zones.Find(Chicago).Value, TimeMode.Iso, splitTime: true);

        var text = await Text(OutputFormat.Csv, Plain, format, Row);

        text.ShouldBe("ts_date,ts_time,symbol,px,n\n2026-10-28,08:30:00.000000000,ESZ26,100.25,7\n");
    }

    [Fact]
    public async Task Json_lines_keep_column_order_and_number_types()
    {
        var text = await Text(OutputFormat.JsonLines, Plain, Utc(), Row, Row);

        text.ShouldBe("""
            {"ts":"2026-10-28T13:30:00.000000000Z","symbol":"ESZ26","px":100.25,"n":7}
            {"ts":"2026-10-28T13:30:00.000000000Z","symbol":"ESZ26","px":100.25,"n":7}

            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Json_lines_write_no_value_as_null()
    {
        var text = await Text(OutputFormat.JsonLines, Plain, Utc(), EmptyRow);

        text.ShouldBe("{\"ts\":null,\"symbol\":null,\"px\":null,\"n\":null}\n");
    }

    [Theory]
    [InlineData(TimeMode.UnixNanos, "1793194200000000000")]
    [InlineData(TimeMode.UnixMillis, "1793194200000")]
    public async Task Json_lines_write_a_numeric_time_as_a_number(TimeMode time, string number)
    {
        Column[] columns = [new("ts", CellKind.Time)];

        var text = await Text(OutputFormat.JsonLines, columns, Utc(time), [Cell.Of(T0)]);

        text.ShouldBe($"{{\"ts\":{number}}}\n");
    }

    [Fact]
    public async Task Json_lines_escape_text_and_names_so_a_parser_gets_them_back()
    {
        const string Awkward = "a\"b\\c\nd";
        Column[] columns = [new("a\"name", CellKind.Text)];

        var text = await Text(OutputFormat.JsonLines, columns, Utc(), [Cell.Of(Awkward)]);

        using var parsed = JsonDocument.Parse(text);
        parsed.RootElement.GetProperty("a\"name").GetString().ShouldBe(Awkward);
    }

    [Fact]
    public async Task Json_lines_keep_a_price_a_plain_number_with_up_to_nine_places()
    {
        Column[] columns = [new("px", CellKind.Price)];

        var text = await Text(OutputFormat.JsonLines, columns, Utc(), [Cell.Of(0.000000001m)]);

        text.ShouldBe("{\"px\":0.000000001}\n");
    }

    [Theory]
    [InlineData(Compression.Gzip)]
    [InlineData(Compression.Zstd)]
    public async Task Text_formats_compress_to_what_they_would_have_written(Compression compression)
    {
        var plain = await Written(OutputFormat.Csv, Plain, Utc(), Compression.None, null, (Row, []));

        var packed = await Written(OutputFormat.Csv, Plain, Utc(), compression, null, (Row, []));

        Unpack(packed, compression).ShouldBe(plain);
    }

    [Theory]
    [InlineData(Compression.Gzip)]
    [InlineData(Compression.Zstd)]
    public async Task Dbn_compresses_the_same_way(Compression compression)
    {
        var packed = await Written(OutputFormat.Dbn, Plain, Utc(), compression, Metadata, (Row, Raw));

        Unpack(packed, compression).ShouldBe([.. Metadata, .. Raw]);
    }

    private static byte[] Unpack(byte[] packed, Compression compression)
    {
        using var source = new MemoryStream(packed);
        using Stream unpacking = compression == Compression.Gzip
            ? new GZipStream(source, CompressionMode.Decompress)
            : new DecompressionStream(source);
        using var target = new MemoryStream();

        unpacking.CopyTo(target);

        return target.ToArray();
    }

    [Fact]
    public async Task Dbn_copies_records_as_stored_behind_the_metadata()
    {
        var bytes = await Written(OutputFormat.Dbn, Plain, Utc(), Compression.None, Metadata, (Row, Raw), (Row, Raw));

        bytes.ShouldBe([.. Metadata, .. Raw, .. Raw]);
    }

    [Fact]
    public async Task Dbn_refuses_a_row_with_no_record_behind_it()
    {
        await using var writer = Writers.Create(OutputFormat.Dbn, PathOf("empty.dbn"), Plain, Utc(), Compression.None, Metadata);

        Should.Throw<InvalidOperationException>(() => writer.Write(Row, []));
    }

    [Fact]
    public void Dbn_needs_a_metadata_block()
    {
        Should.Throw<ArgumentNullException>(() => Writers.Create(OutputFormat.Dbn, PathOf("none.dbn"), Plain, Utc(), Compression.None, null));
    }

    private static IReadOnlyList<Column> BarColumns => BarBuilder.Columns(series: false, trades: false);

    private static Cell[] BarRow(ulong start, ulong end) =>
        [Cell.Of(start), Cell.Of(end), Cell.Of(Symbol), Cell.Of(100.25m), Cell.Of(101m), Cell.Of(100m), Cell.Of(100.5m), Cell.Of(7L)];

    [Fact]
    public async Task Ninjatrader_bars_are_stamped_with_their_end_in_the_zone_asked_for()
    {
        var format = new CellFormat(Zones.Find(Chicago).Value, TimeMode.Iso, splitTime: false);

        var text = await Text(OutputFormat.NinjaTrader, BarColumns, format, BarRow(T0, T0 + Minute));

        text.ShouldBe("20261028 083100;100.25;101;100;100.5;7\r\n");
    }

    [Fact]
    public async Task Ninjatrader_times_follow_the_zone_not_the_clock_of_this_machine()
    {
        var text = await Text(OutputFormat.NinjaTrader, BarColumns, Utc(), BarRow(T0, T0 + Minute));

        text.ShouldStartWith("20261028 133100;");
    }

    [Fact]
    public async Task Ninjatrader_writes_a_bar_of_a_day_or_more_by_its_date_alone()
    {
        var format = new CellFormat(Zones.Find(Chicago).Value, TimeMode.Iso, splitTime: false);
        var path = PathOf(Path.GetRandomFileName());
        var sessionStart = DbnBytes.Utc(2026, 10, 27, 22);
        var sessionEnd = DbnBytes.Utc(2026, 10, 28, 21);

        await using (var writer = Writers.Create(OutputFormat.NinjaTrader, path, BarColumns, format, Compression.None, null, dateOnly: true))
            writer.Write(BarRow(sessionStart, sessionEnd), []);

        (await File.ReadAllTextAsync(path)).ShouldBe("20261028;100.25;101;100;100.5;7\r\n");
    }

    [Fact]
    public async Task Ninjatrader_keeps_a_time_on_any_shorter_bar_however_long_it_ran()
    {
        var format = new CellFormat(Zones.Find(Chicago).Value, TimeMode.Iso, splitTime: false);
        var sessionStart = DbnBytes.Utc(2026, 10, 27, 22);
        var sessionEnd = DbnBytes.Utc(2026, 10, 28, 21);

        var text = await Text(OutputFormat.NinjaTrader, BarColumns, format, BarRow(sessionStart, sessionEnd));

        text.ShouldStartWith("20261028 160000;");
    }

    [Fact]
    public async Task Ninjatrader_ticks_keep_seven_digits_of_the_second()
    {
        var columns = MboTransform.Create("trades", series: false).Value.Columns;
        var format = new CellFormat(Zones.Find(Chicago).Value, TimeMode.Iso, splitTime: false);
        var time = T0 + 123_456_789;
        Cell[] row = [Cell.Of(time), Cell.Of(time), Cell.Of(Symbol), Cell.Of(1L), Cell.Of("A"), Cell.Of(100.25m), Cell.Of(3L), Cell.Of(9L)];

        var text = await Text(OutputFormat.NinjaTrader, columns, format, row);

        text.ShouldBe("20261028 083000 1234567;100.25;3\r\n");
    }

    [Fact]
    public async Task Ninjatrader_has_no_header_and_ignores_compression()
    {
        var bytes = await Written(OutputFormat.NinjaTrader, BarColumns, Utc(), Compression.Gzip, null, (BarRow(T0, T0 + Minute), []));

        Encoding.ASCII.GetString(bytes).ShouldStartWith("2026");
    }

    [Theory]
    [InlineData("symbol")]
    [InlineData("ts_event")]
    [InlineData("ts_event,price")]
    [InlineData("open,high,low,close,volume")]
    [InlineData("ts_end,open,high,low,volume")]
    [InlineData("ts_end,open,high,low,close")]
    public void Ninjatrader_refuses_columns_that_are_not_bars_or_trades(string names)
    {
        var columns = names.Split(',').Select(n => new Column(n, CellKind.Text)).ToList();
        using var stream = new MemoryStream();

        Should.Throw<InvalidOperationException>(() => new NinjaTraderWriter(stream, columns, Utc()));
    }

    [Fact]
    public async Task A_split_time_in_json_lines_is_two_fields_named_as_the_csv_columns_are()
    {
        var format = new CellFormat(Zones.Find(Chicago).Value, TimeMode.Iso, splitTime: true);

        var csv = await Text(OutputFormat.Csv, Plain, format, Row);
        var json = await Text(OutputFormat.JsonLines, Plain, format, Row);

        csv.ShouldStartWith("ts_date,ts_time,symbol,px,n\n2026-10-28,08:30:00.000000000,");
        json.ShouldBe($$"""{"ts_date":"2026-10-28","ts_time":"08:30:00.000000000","symbol":"{{Symbol}}","px":100.25,"n":7}""" + "\n");
    }

    [Fact]
    public async Task A_split_time_with_no_value_is_two_nulls()
    {
        var format = new CellFormat(TimeZoneInfo.Utc, TimeMode.Iso, splitTime: true);

        var json = await Text(OutputFormat.JsonLines, Plain, format, EmptyRow);

        json.ShouldBe("""{"ts_date":null,"ts_time":null,"symbol":null,"px":null,"n":null}""" + "\n");
    }

    [Fact]
    public async Task A_time_that_is_a_number_is_not_split_in_json_lines_any_more_than_in_csv()
    {
        var format = new CellFormat(TimeZoneInfo.Utc, TimeMode.UnixNanos, splitTime: true);

        var json = await Text(OutputFormat.JsonLines, Plain, format, Row);

        json.ShouldStartWith($$"""{"ts":{{T0}},""");
    }
}
