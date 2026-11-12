using System.Buffers.Binary;
using System.Text;

namespace DbnClient.Export.Parquet;

/// <summary>
/// Writes rows as a Parquet file: flat, every column optional, zstd-compressed.
/// Times are INT64 timestamps in nanoseconds (UTC instants), prices DOUBLE,
/// integers INT64, and text dictionary-encoded strings. Rows are kept in
/// memory a row group at a time.
/// </summary>
internal sealed class ParquetRowWriter : IRowWriter
{
    private const int RowGroupSize = 1_000_000;
    private const string CreatedBy = "DbnClient";

    // Parquet's enums, as the format numbers them.
    private const int Int64Type = 2;
    private const int DoubleType = 5;
    private const int ByteArrayType = 6;
    private const int Optional = 1;
    private const int Utf8 = 0;
    private const int Plain = 0;
    private const int Rle = 3;
    private const int RleDictionary = 8;
    private const int Zstd = 6;
    private const int DataPage = 0;
    private const int DictionaryPage = 2;

    private static readonly byte[] Magic = "PAR1"u8.ToArray();

    private readonly Stream stream;
    private readonly IReadOnlyList<Column> columns;
    private readonly ColumnBuffer[] buffers;
    private readonly List<RowGroup> groups = [];
    private long position;
    private long rows;
    private int buffered;

    /// <param name="earlier">A file already closed, carried on in <paramref name="stream"/>: its footer is replaced.</param>
    public ParquetRowWriter(Stream stream, IReadOnlyList<Column> columns, Closed? earlier = null)
    {
        this.stream = stream;
        this.columns = columns;
        buffers = [.. columns.Select(c => new ColumnBuffer(c.Kind))];

        if (earlier is null)
        {
            Emit(Magic);

            return;
        }

        stream.SetLength(earlier.FooterAt);
        stream.Position = earlier.FooterAt;

        position = earlier.FooterAt;
        rows = earlier.Rows;
        groups.AddRange(earlier.Groups);
    }

    /// <summary>What the file holds once this writer is disposed, for carrying it on.</summary>
    public Closed? Finished { get; private set; }

    public void Write(ReadOnlySpan<Cell> cells, ReadOnlySpan<byte> raw)
    {
        for (var i = 0; i < buffers.Length; i++)
            buffers[i].Add(cells[i]);

        rows++;

        if (++buffered == RowGroupSize)
            Flush();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (buffered > 0)
                Flush();

            Finished = new Closed(position, rows, groups);

            var footer = Footer();

            Emit(footer);

            var length = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, footer.Length);
            Emit(length);
            Emit(Magic);
        }
        finally
        {
            // A write that fails, as on a full disk, must still let go of the file, or it could not be deleted.
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Writes the buffered rows as a row group: one chunk per column.</summary>
    private void Flush()
    {
        var chunks = new List<Chunk>();
        var start = position;

        foreach (var buffer in buffers)
        {
            chunks.Add(WriteChunk(buffer));
            buffer.Clear();
        }

        // total_byte_size is the size before compression, by the format's definition.
        groups.Add(new RowGroup(chunks, chunks.Sum(c => c.Uncompressed), buffered));
        buffered = 0;
    }

    private Chunk WriteChunk(ColumnBuffer buffer)
    {
        long? dictionaryOffset = null;
        var uncompressed = 0L;
        var startPosition = position;

        if (buffer.Kind == CellKind.Text)
        {
            dictionaryOffset = position;
            uncompressed += WritePage(DictionaryPage, buffer.DictionaryValues(), buffer.Strings.Count, Plain);
        }

        var dataOffset = position;
        var body = Concat(Levels(buffer.Defined), buffer.Kind == CellKind.Text ? buffer.DictionaryIndices() : buffer.Values());

        uncompressed += WritePage(DataPage, body, buffer.Defined.Count, buffer.Kind == CellKind.Text ? RleDictionary : Plain);

        return new Chunk(buffer.Kind, buffer.Defined.Count, uncompressed, position - startPosition, dataOffset, dictionaryOffset);
    }

    /// <summary>Writes a page header and its zstd-compressed body; returns the uncompressed size, header included.</summary>
    private int WritePage(int type, byte[] body, int count, int encoding)
    {
        var compressed = DbnClient.Zstd.Compress(body);
        var header = new Thrift().BeginStruct()
            .Int(1, type)
            .Int(2, body.Length)
            .Int(3, compressed.Length);

        if (type == DataPage)
        {
            header.StructField(5).Int(1, count).Int(2, encoding).Int(3, Rle).Int(4, Rle).EndStruct();
        }
        else
        {
            header.StructField(7).Int(1, count).Int(2, encoding).EndStruct();
        }

        var bytes = header.EndStruct().ToArray();

        Emit(bytes);
        Emit(compressed);

        return bytes.Length + body.Length;
    }

    private byte[] Footer()
    {
        var thrift = new Thrift().BeginStruct()
            .Int(1, 1)
            .StructList(2, columns.Count + 1);

        thrift.BeginStruct().String(4, "schema").Int(5, columns.Count).EndStruct();

        foreach (var column in columns)
        {
            thrift.BeginStruct()
                .Int(1, PhysicalType(column.Kind))
                .Int(3, Optional)
                .String(4, column.Name);

            if (column.Kind == CellKind.Text)
            {
                thrift.Int(6, Utf8).StructField(10).StructField(1).EndStruct().EndStruct();
            }
            else if (column.Kind == CellKind.Time)
            {
                // TIMESTAMP(isAdjustedToUTC = true, unit = NANOS)
                thrift.StructField(10).StructField(8).Bool(1, true).StructField(2).StructField(3).EndStruct().EndStruct().EndStruct().EndStruct();
            }

            thrift.EndStruct();
        }

        thrift.Long(3, rows).StructList(4, groups.Count);

        foreach (var group in groups)
        {
            thrift.BeginStruct().StructList(1, group.Chunks.Count);

            for (var i = 0; i < group.Chunks.Count; i++)
            {
                var chunk = group.Chunks[i];

                thrift.BeginStruct()
                    .Long(2, chunk.DataOffset)
                    .StructField(3)
                    .Int(1, PhysicalType(chunk.Kind))
                    .IntList(2, chunk.Kind == CellKind.Text ? [Plain, Rle, RleDictionary] : [Plain, Rle])
                    .StringList(3, [columns[i].Name])
                    .Int(4, Zstd)
                    .Long(5, chunk.Values)
                    .Long(6, chunk.Uncompressed)
                    .Long(7, chunk.Compressed)
                    .Long(9, chunk.DataOffset);

                if (chunk.DictionaryOffset is { } offset)
                    thrift.Long(11, offset);

                thrift.EndStruct().EndStruct();
            }

            thrift.Long(2, group.Bytes).Long(3, group.Rows).EndStruct();
        }

        return thrift.String(6, CreatedBy).EndStruct().ToArray();
    }

    private void Emit(byte[] bytes)
    {
        stream.Write(bytes);
        position += bytes.Length;
    }

    private static int PhysicalType(CellKind kind) => kind switch
    {
        CellKind.Price => DoubleType,
        CellKind.Text => ByteArrayType,
        _ => Int64Type
    };

    /// <summary>Definition levels, 1 for a value and 0 for none, run-length encoded behind a 4-byte length.</summary>
    private static byte[] Levels(List<bool> defined)
    {
        var runs = new MemoryStream();
        var i = 0;

        while (i < defined.Count)
        {
            var value = defined[i];
            var run = 1;

            while (i + run < defined.Count && defined[i + run] == value)
                run++;

            Varint(runs, (ulong)run << 1);
            runs.WriteByte(value ? (byte)1 : (byte)0);
            i += run;
        }

        var body = runs.ToArray();
        var levels = new byte[4 + body.Length];

        BinaryPrimitives.WriteInt32LittleEndian(levels, body.Length);
        body.CopyTo(levels, 4);

        return levels;
    }

    internal static void Varint(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        stream.WriteByte((byte)value);
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var both = new byte[a.Length + b.Length];

        a.CopyTo(both, 0);
        b.CopyTo(both, a.Length);

        return both;
    }

    /// <summary>Where a closed file's footer starts, and the rows and row groups before it.</summary>
    internal sealed record Closed(long FooterAt, long Rows, IReadOnlyList<RowGroup> Groups);

    internal sealed record Chunk(CellKind Kind, long Values, long Uncompressed, long Compressed, long DataOffset, long? DictionaryOffset);

    internal sealed record RowGroup(List<Chunk> Chunks, long Bytes, long Rows);

    /// <summary>One column's values for the row group being built.</summary>
    private sealed class ColumnBuffer(CellKind kind)
    {
        private readonly MemoryStream values = new();
        private readonly Dictionary<string, int> dictionary = new(StringComparer.Ordinal);
        private readonly List<int> indices = [];
        private readonly byte[] scratch = new byte[8];

        public CellKind Kind { get; } = kind;

        public List<bool> Defined { get; } = [];

        public List<string> Strings { get; } = [];

        public void Add(Cell cell)
        {
            var empty = cell.IsEmpty || Kind == CellKind.Price && cell.Price is null || Kind == CellKind.Text && cell.Text is null;

            Defined.Add(!empty);

            if (empty)
                return;

            switch (Kind)
            {
                case CellKind.Price:
                    BinaryPrimitives.WriteDoubleLittleEndian(scratch, (double)cell.Price!.Value);
                    values.Write(scratch);
                    break;

                case CellKind.Text:
                    if (!dictionary.TryGetValue(cell.Text!, out var index))
                    {
                        index = Strings.Count;
                        dictionary[cell.Text!] = index;
                        Strings.Add(cell.Text!);
                    }

                    indices.Add(index);
                    break;

                default:
                    BinaryPrimitives.WriteInt64LittleEndian(scratch, cell.Number);
                    values.Write(scratch);
                    break;
            }
        }

        public byte[] Values() => values.ToArray();

        /// <summary>The dictionary page: each distinct string, PLAIN encoded.</summary>
        public byte[] DictionaryValues()
        {
            var page = new MemoryStream();

            foreach (var s in Strings)
            {
                var bytes = Encoding.UTF8.GetBytes(s);

                BinaryPrimitives.WriteInt32LittleEndian(scratch, bytes.Length);
                page.Write(scratch, 0, 4);
                page.Write(bytes);
            }

            return page.ToArray();
        }

        /// <summary>The indices: a bit-width byte, then run-length encoded runs.</summary>
        public byte[] DictionaryIndices()
        {
            var width = Math.Max(1, 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)Math.Max(Strings.Count - 1, 1)));
            var bytesPerValue = (width + 7) / 8;
            var page = new MemoryStream();

            page.WriteByte((byte)width);

            var i = 0;

            while (i < indices.Count)
            {
                var value = indices[i];
                var run = 1;

                while (i + run < indices.Count && indices[i + run] == value)
                    run++;

                Varint(page, (ulong)run << 1);

                for (var b = 0; b < bytesPerValue; b++)
                    page.WriteByte((byte)(value >> 8 * b));

                i += run;
            }

            return page.ToArray();
        }

        public void Clear()
        {
            values.SetLength(0);
            dictionary.Clear();
            indices.Clear();
            Defined.Clear();
            Strings.Clear();
        }
    }
}
