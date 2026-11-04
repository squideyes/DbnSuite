using DbnClient.Export;
using DbnClient.Export.Parquet;
using Parquet;
using Parquet.Schema;
using Shouldly;

namespace DbnSync.UnitTests.ClientTests;

/// <summary>Every case read back through Parquet.Net, which shares no code with the writer.</summary>
public sealed class ParquetRowWriterTests
{
    private const string Symbol = "symbol";
    private const string Time = "ts_event";
    private const string Price = "price";
    private const string Size = "size";
    private const ulong T0 = 1_793_194_200_123_456_700;

    private static readonly Column[] Columns =
    [
        new(Time, CellKind.Time), new(Symbol, CellKind.Text), new(Price, CellKind.Price), new(Size, CellKind.Integer)
    ];

    private static Cell[] Row(ulong time, string? text, decimal? price, long size) =>
        [Cell.Of(time), Cell.Of(text), Cell.Of(price), Cell.Of(size)];

    private static async Task<MemoryStream> Written(IReadOnlyList<Column> columns, IEnumerable<Cell[]> rows)
    {
        var bytes = new KeepOpen();

        await using (var writer = new ParquetRowWriter(bytes, columns))
        {
            foreach (var row in rows)
                writer.Write(row, []);
        }

        bytes.Position = 0;

        return bytes;
    }

    /// <summary>A stream that survives the writer disposing it, to be read back.</summary>
    private sealed class KeepOpen : MemoryStream
    {
        protected override void Dispose(bool disposing) => Flush();

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static DataField Field(ParquetReader reader, string name) => reader.Schema.GetDataFields().Single(f => f.Name == name);

    private static async Task<string?[]> Strings(ParquetReader reader, string name, int count, int group = 0)
    {
        using var rows = reader.OpenRowGroupReader(group);
        var values = new string?[count];

        await rows.ReadAsync(Field(reader, name), values.AsMemory());

        return values;
    }

    private static async Task<T?[]> Values<T>(ParquetReader reader, string name, int count, int group = 0)
        where T : struct
    {
        using var rows = reader.OpenRowGroupReader(group);
        var values = new T?[count];

        await rows.ReadAsync<T>(Field(reader, name), values);

        return values;
    }

    [Fact]
    public async Task The_file_names_its_writer_and_counts_its_rows()
    {
        await using var bytes = await Written(Columns, [Row(T0, "ESZ26", 1m, 1)]);

        await using var reader = await ParquetReader.CreateAsync(bytes);

        reader.Metadata!.CreatedBy.ShouldBe("DbnClient");
        reader.Metadata.NumRows.ShouldBe(1);
    }

    [Fact]
    public async Task Every_column_is_optional_in_a_flat_schema()
    {
        await using var bytes = await Written(Columns, [Row(T0, "ESZ26", 1m, 1)]);

        await using var reader = await ParquetReader.CreateAsync(bytes);

        reader.Schema.GetDataFields().Select(f => f.Name).ShouldBe([Time, Symbol, Price, Size]);
        reader.Schema.GetDataFields().ShouldAllBe(f => f.IsNullable);
    }

    [Fact]
    public async Task Types_are_timestamps_strings_doubles_and_longs()
    {
        await using var bytes = await Written(Columns, [Row(T0, "ESZ26", 1m, 1)]);

        await using var reader = await ParquetReader.CreateAsync(bytes);

        var types = reader.Metadata!.Schema.Where(s => s.Type is not null).Select(s => s.Type!.Value.ToString()).ToList();
        types.ShouldBe(["INT64", "BYTE_ARRAY", "DOUBLE", "INT64"]);
    }

    [Fact]
    public async Task Times_are_nanoseconds_in_utc()
    {
        await using var bytes = await Written(Columns, [Row(T0, "ESZ26", 1m, 1)]);

        await using var reader = await ParquetReader.CreateAsync(bytes);

        var times = await Values<DateTime>(reader, Time, 1);
        times[0].ShouldBe(new DateTime(2026, 10, 28, 13, 30, 0, DateTimeKind.Utc).AddTicks(1_234_567));
    }

    [Fact]
    public async Task Text_prices_and_integers_read_back()
    {
        await using var bytes = await Written(Columns, [Row(T0, "ESZ26", 6876.25m, 7), Row(T0, "ESH27", -37.63m, 8)]);

        await using var reader = await ParquetReader.CreateAsync(bytes);

        (await Strings(reader, Symbol, 2)).ShouldBe(["ESZ26", "ESH27"]);
        (await Values<double>(reader, Price, 2)).ShouldBe([6876.25, -37.63]);
        (await Values<long>(reader, Size, 2)).ShouldBe([7L, 8L]);
    }

    [Fact]
    public async Task No_value_reads_as_null_in_every_kind_of_column()
    {
        await using var bytes = await Written(Columns,
        [
            Row(T0, "ESZ26", 1m, 1),
            [Cell.Empty, Cell.Of((string?)null), Cell.Of((decimal?)null), Cell.Empty],
            Row(T0, "ESZ26", 3m, 3)
        ]);

        await using var reader = await ParquetReader.CreateAsync(bytes);

        (await Values<DateTime>(reader, Time, 3))[1].ShouldBeNull();
        (await Strings(reader, Symbol, 3)).ShouldBe(["ESZ26", null, "ESZ26"]);
        (await Values<double>(reader, Price, 3)).ShouldBe([1d, null, 3d]);
        (await Values<long>(reader, Size, 3)).ShouldBe([1L, null, 3L]);
    }

    [Fact]
    public async Task A_text_column_with_no_values_at_all_reads_as_nulls()
    {
        Column[] columns = [new(Symbol, CellKind.Text)];

        await using var bytes = await Written(columns, [[Cell.Of((string?)null)], [Cell.Of((string?)null)]]);

        await using var reader = await ParquetReader.CreateAsync(bytes);

        (await Strings(reader, Symbol, 2)).ShouldBe([null, null]);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(257)]
    [InlineData(70_000)]
    public async Task Dictionaries_of_any_width_read_back(int distinct)
    {
        Column[] columns = [new(Symbol, CellKind.Text)];
        var names = Enumerable.Range(0, distinct).Select(i => $"S{i}").ToArray();

        await using var bytes = await Written(columns, names.Select(n => new[] { Cell.Of(n) }));

        await using var reader = await ParquetReader.CreateAsync(bytes);

        (await Strings(reader, Symbol, distinct)).ShouldBe(names);
    }

    [Fact]
    public async Task Long_runs_of_one_value_read_back()
    {
        const int Run = 1000;
        Column[] columns = [new(Symbol, CellKind.Text)];
        var names = Enumerable.Repeat("ESZ26", Run).Concat(Enumerable.Repeat("ESH27", Run)).ToArray();

        await using var bytes = await Written(columns, names.Select(n => new[] { Cell.Of(n) }));

        await using var reader = await ParquetReader.CreateAsync(bytes);

        (await Strings(reader, Symbol, 2 * Run)).ShouldBe(names);
    }

    [Fact]
    public async Task Text_is_utf8()
    {
        const string Accented = "Café";
        Column[] columns = [new(Symbol, CellKind.Text)];

        await using var bytes = await Written(columns, [[Cell.Of(Accented)]]);

        await using var reader = await ParquetReader.CreateAsync(bytes);

        (await Strings(reader, Symbol, 1)).ShouldBe([Accented]);
    }

    [Fact]
    public async Task Row_groups_hold_a_million_rows_at_most()
    {
        const int Limit = 1_000_000;
        Column[] columns = [new(Size, CellKind.Integer)];
        var row = new[] { Cell.Of(7L) };

        await using var bytes = await Written(columns, Enumerable.Repeat(row, Limit + 1));

        await using var reader = await ParquetReader.CreateAsync(bytes);

        reader.RowGroupCount.ShouldBe(2);
        reader.Metadata!.NumRows.ShouldBe(Limit + 1);
        reader.Metadata.RowGroups[0].NumRows.ShouldBe(Limit);
        (await Values<long>(reader, Size, 1, group: 1)).ShouldBe([7L]);
    }

    [Fact]
    public async Task A_second_row_group_has_its_own_dictionary()
    {
        const int Limit = 1_000_000;
        Column[] columns = [new(Symbol, CellKind.Text)];
        var first = new[] { Cell.Of("ESZ26") };
        var second = new[] { Cell.Of("ESH27") };

        await using var bytes = await Written(columns, Enumerable.Repeat(first, Limit).Append(second));

        await using var reader = await ParquetReader.CreateAsync(bytes);

        (await Strings(reader, Symbol, 1, group: 1)).ShouldBe(["ESH27"]);
    }

    [Fact]
    public async Task More_columns_than_a_short_list_holds_read_back()
    {
        var columns = MboTransform.Create("mbp-10", series: true).Value.Columns;
        var row = columns.Select(c => c.Kind switch
        {
            CellKind.Price => Cell.Of(1m),
            CellKind.Text => Cell.Of("x"),
            _ => Cell.Of(1L)
        }).ToArray();

        await using var bytes = await Written(columns, [row]);

        await using var reader = await ParquetReader.CreateAsync(bytes);

        reader.Schema.GetDataFields().Length.ShouldBe(columns.Count);
    }

    [Fact]
    public async Task A_file_with_no_rows_is_still_a_valid_file()
    {
        await using var bytes = await Written(Columns, []);

        await using var reader = await ParquetReader.CreateAsync(bytes);

        reader.RowGroupCount.ShouldBe(0);
    }

    [Fact]
    public void A_varint_takes_seven_bits_a_byte_low_first()
    {
        using var stream = new MemoryStream();

        ParquetRowWriter.Varint(stream, 300);

        stream.ToArray().ShouldBe([0xAC, 0x02]);
    }
}
