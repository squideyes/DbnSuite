using System.Globalization;
using System.IO.Compression;
using System.Text;
using DbnClient.Export.Parquet;

namespace DbnClient.Export;

/// <summary>The file formats <c>export</c> writes.</summary>
public enum OutputFormat
{
    Csv,
    JsonLines,
    Parquet,

    /// <summary>A filtered copy of the DBN records, as Databento's own tools read.</summary>
    Dbn,

    /// <summary>NinjaTrader 8's text import: bars, or ticks.</summary>
    NinjaTrader
}

/// <summary>Compression for the text formats.</summary>
public enum Compression
{
    None,
    Gzip,
    Zstd
}

/// <summary>
/// One output file, written a row at a time. Rows arrive by the million, so
/// writers fill a large buffer synchronously and the stream behind it does the I/O.
/// </summary>
public interface IRowWriter : IAsyncDisposable
{
    /// <param name="raw">The record's own bytes, for formats that copy them; empty for built rows such as bars.</param>
    void Write(ReadOnlySpan<Cell> cells, ReadOnlySpan<byte> raw);
}

public static class Writers
{
    private const int BufferSize = 1 << 20;

    public static string Extension(OutputFormat format, Compression compression) => format switch
    {
        OutputFormat.Csv => "csv" + Suffix(compression),
        OutputFormat.JsonLines => "jsonl" + Suffix(compression),
        OutputFormat.Parquet => "parquet",
        OutputFormat.Dbn => "dbn" + Suffix(compression),
        _ => "txt"
    };

    /// <param name="metadata">For DBN: the metadata block the copy opens with.</param>
    /// <param name="dateOnly">For NinjaTrader: bars of a day or more, stamped by date alone.</param>
    public static IRowWriter Create(OutputFormat format, string path, IReadOnlyList<Column> columns, CellFormat cells,
        Compression compression, byte[]? metadata, bool dateOnly = false)
    {
        if (format == OutputFormat.Dbn)
            ArgumentNullException.ThrowIfNull(metadata);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        return format switch
        {
            OutputFormat.Csv => new CsvWriter(Open(path, compression), columns, cells),
            OutputFormat.JsonLines => new JsonLinesWriter(Open(path, compression), columns, cells),
            OutputFormat.Parquet => new ParquetRowWriter(File.Create(path, BufferSize), columns),
            OutputFormat.Dbn => new DbnWriter(Open(path, compression), metadata!),
            _ => new NinjaTraderWriter(Open(path, Compression.None), columns, cells, dateOnly)
        };
    }

    /// <summary>
    /// Carries on a file this export already closed, keeping every row in it:
    /// text and DBN are appended to, as a new frame when compressed, and a
    /// Parquet file gains row groups behind a new footer.
    /// </summary>
    /// <param name="parquet">For Parquet: what the closed file holds.</param>
    /// <param name="dateOnly">For NinjaTrader: bars of a day or more, stamped by date alone.</param>
    internal static IRowWriter Reopen(OutputFormat format, string path, IReadOnlyList<Column> columns, CellFormat cells,
        Compression compression, ParquetRowWriter.Closed? parquet, bool dateOnly) => format switch
    {
        OutputFormat.Csv => new CsvWriter(Open(path, compression, FileMode.Append), columns, cells, header: false),
        OutputFormat.JsonLines => new JsonLinesWriter(Open(path, compression, FileMode.Append), columns, cells),
        OutputFormat.Parquet => new ParquetRowWriter(
            new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read, BufferSize), columns, parquet),
        OutputFormat.Dbn => new DbnWriter(Open(path, compression, FileMode.Append), []),
        _ => new NinjaTraderWriter(Open(path, Compression.None, FileMode.Append), columns, cells, dateOnly)
    };

    private static string Suffix(Compression compression) => compression switch
    {
        Compression.Gzip => ".gz",
        Compression.Zstd => ".zst",
        _ => ""
    };

    private static Stream Open(string path, Compression compression, FileMode mode = FileMode.Create)
    {
        var file = new FileStream(path, mode, FileAccess.Write, FileShare.Read, BufferSize);

        return compression switch
        {
            Compression.Gzip => new GZipStream(file, CompressionLevel.Optimal),
            Compression.Zstd => Zstd.Compressing(file),
            _ => file
        };
    }
}

/// <summary>Comma-separated values with a header row; text is quoted only when it must be.</summary>
internal sealed class CsvWriter : IRowWriter
{
    private readonly StreamWriter writer;
    private readonly IReadOnlyList<Column> columns;
    private readonly CellFormat format;
    private readonly List<string> values = [];

    /// <param name="header">False when the file already opens with one.</param>
    public CsvWriter(Stream stream, IReadOnlyList<Column> columns, CellFormat format, bool header = true)
    {
        writer = new StreamWriter(stream, new UTF8Encoding(false), 1 << 16) { NewLine = "\n" };
        this.columns = columns;
        this.format = format;

        if (header)
            writer.WriteLine(string.Join(',', format.Names(columns)));
    }

    public void Write(ReadOnlySpan<Cell> cells, ReadOnlySpan<byte> raw)
    {
        values.Clear();

        for (var i = 0; i < columns.Count; i++)
            format.Write(columns[i], cells[i], values);

        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0)
                writer.Write(',');

            writer.Write(Quote(values[i]));
        }

        writer.WriteLine();
    }

    public ValueTask DisposeAsync() => writer.DisposeAsync();

    private static string Quote(string value) =>
        value.AsSpan().IndexOfAny(",\"\n\r") < 0 ? value : $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}

/// <summary>One JSON object per line; numbers stay numbers. A split time is two fields, named as CSV names its columns.</summary>
internal sealed class JsonLinesWriter(Stream stream, IReadOnlyList<Column> columns, CellFormat format) : IRowWriter
{
    private readonly StreamWriter writer = new(stream, new UTF8Encoding(false), 1 << 16) { NewLine = "\n" };
    private readonly string[] names = [.. format.Names(columns).Select(n => System.Text.Json.JsonEncodedText.Encode(n).ToString())];
    private readonly List<string> parts = [];

    public void Write(ReadOnlySpan<Cell> cells, ReadOnlySpan<byte> raw)
    {
        var field = 0;

        writer.Write('{');

        for (var i = 0; i < columns.Count; i++)
        {
            if (columns[i].Kind != CellKind.Time || !format.SplitTime)
            {
                Field(field++, Value(columns[i], cells[i]));
                continue;
            }

            parts.Clear();
            format.Write(columns[i], cells[i], parts);

            foreach (var part in parts)
                Field(field++, cells[i].IsEmpty ? "null" : $"\"{part}\"");
        }

        writer.WriteLine('}');
    }

    public ValueTask DisposeAsync() => writer.DisposeAsync();

    private void Field(int index, string value)
    {
        if (index > 0)
            writer.Write(',');

        writer.Write('"');
        writer.Write(names[index]);
        writer.Write("\":");
        writer.Write(value);
    }

    private string Value(Column column, Cell cell)
    {
        if (cell.IsEmpty)
            return "null";

        return column.Kind switch
        {
            CellKind.Time when format.TimeIsNumber => format.TimeText((ulong)cell.Number),
            CellKind.Time => $"\"{format.TimeText((ulong)cell.Number)}\"",
            CellKind.Price => cell.Price is { } p ? p.ToString("0.#########", CultureInfo.InvariantCulture) : "null",
            CellKind.Integer => cell.Number.ToString(CultureInfo.InvariantCulture),
            _ => cell.Text is null ? "null" : $"\"{System.Text.Json.JsonEncodedText.Encode(cell.Text)}\""
        };
    }
}

/// <summary>
/// NinjaTrader 8's import text. Bars: <c>yyyyMMdd HHmmss;open;high;low;close;volume</c>,
/// stamped with the bar's end, or <c>yyyyMMdd;open;high;low;close;volume</c> for
/// bars of a day or more. Ticks: <c>yyyyMMdd HHmmss fffffff;price;volume</c>.
/// Times are in the zone asked for, which NinjaTrader's import must be told.
/// </summary>
internal sealed class NinjaTraderWriter : IRowWriter
{
    private readonly StreamWriter writer;
    private readonly CellFormat format;
    private readonly int time;
    private readonly int[] prices;
    private readonly int volume;
    private readonly bool bars;
    private readonly bool dateOnly;

    /// <param name="dateOnly">Bars of a day or more, which NinjaTrader imports by date alone.</param>
    public NinjaTraderWriter(Stream stream, IReadOnlyList<Column> columns, CellFormat format, bool dateOnly = false)
    {
        writer = new StreamWriter(stream, new UTF8Encoding(false), 1 << 16) { NewLine = "\r\n" };
        this.format = format;
        this.dateOnly = dateOnly;

        int Index(string name) => columns.Select((c, i) => (c, i)).FirstOrDefault(x => x.c.Name == name, (null!, -1)).Item2;

        bars = Index("open") >= 0;
        time = bars ? Index("ts_end") : Index("ts_event");
        prices = bars ? [Index("open"), Index("high"), Index("low"), Index("close")] : [Index("price")];
        volume = bars ? Index("volume") : Index("size");

        if (time < 0 || prices.Any(p => p < 0) || volume < 0)
            throw new InvalidOperationException("NinjaTrader 8 output is for bars and trades only.");
    }

    public void Write(ReadOnlySpan<Cell> cells, ReadOnlySpan<byte> raw)
    {
        var nanos = (ulong)cells[time].Number;
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UnixEpoch.AddTicks((long)(nanos / 100)), format.Zone);

        writer.Write(local.ToString(!bars ? "yyyyMMdd HHmmss fffffff" : dateOnly ? "yyyyMMdd" : "yyyyMMdd HHmmss", CultureInfo.InvariantCulture));

        foreach (var p in prices)
        {
            writer.Write(';');
            writer.Write(CellFormat.Price(cells[p].Price));
        }

        writer.Write(';');
        writer.WriteLine(cells[volume].Number.ToString(CultureInfo.InvariantCulture));
    }

    public ValueTask DisposeAsync() => writer.DisposeAsync();
}

/// <summary>A DBN file holding the chosen records exactly as stored, behind the source file's metadata.</summary>
internal sealed class DbnWriter : IRowWriter
{
    private readonly Stream stream;

    public DbnWriter(Stream stream, byte[] metadata)
    {
        this.stream = new BufferedStream(stream, 1 << 16);
        this.stream.Write(metadata);
    }

    public void Write(ReadOnlySpan<Cell> cells, ReadOnlySpan<byte> raw)
    {
        if (raw.IsEmpty)
            throw new InvalidOperationException("DBN output copies records, so bars and conversions need another format.");

        stream.Write(raw);
    }

    public ValueTask DisposeAsync() => stream.DisposeAsync();
}
