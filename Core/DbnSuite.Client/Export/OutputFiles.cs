using System.Globalization;
using DbnSuite.Client.Contracts;
using DbnSuite.Client.Export.Parquet;

namespace DbnSuite.Client.Export;

/// <summary>
/// The files an export writes into, opened as rows arrive: one per trade date,
/// one for the range, or one per partition folder. Only one file is open at a
/// time. Rows nearly always come in date order; one that comes late, as a
/// non-bar record's event time can, reopens its file and adds to it. Files get
/// their names only once the export has finished, so a failed one leaves nothing
/// that looks complete.
/// </summary>
internal sealed class OutputFiles : IAsyncDisposable
{
    private readonly ExportRequest request;
    private readonly IReadOnlyList<Column> columns;
    private readonly int[] projection;
    private readonly string kind;
    private readonly CellFormat format;
    private readonly Cell[] projected;
    private readonly List<string> files = [];
    private bool completed;

    /// <summary>Every file closed so far, with what a Parquet one needs to be carried on.</summary>
    private readonly Dictionary<string, ParquetRowWriter.Closed?> closed = new(StringComparer.Ordinal);

    private IRowWriter? writer;
    private string? writing;
    private DateOnly? openFor;

    public OutputFiles(ExportRequest request, IReadOnlyList<Column> all, string kind)
    {
        this.request = request;
        this.kind = kind;

        projection = request.Columns.Count == 0
            ? [.. Enumerable.Range(0, all.Count)]
            : [.. request.Columns.Select(name => all.Select((c, i) => (c, i)).First(x => x.c.Name == name).i)];

        columns = [.. projection.Select(i => all[i])];
        projected = new Cell[columns.Count];
        format = new CellFormat(request.Zone, request.Time, request.SplitTime);
    }

    /// <summary>The metadata block the next DBN file opens with: the current source file's.</summary>
    public byte[]? Metadata { get; set; }

    public IReadOnlyList<string> Files => files;

    public long Rows { get; private set; }

    /// <summary>Which of <paramref name="all"/>'s names are missing, if <paramref name="wanted"/> names any.</summary>
    public static IReadOnlyList<string> Unknown(IReadOnlyList<string> wanted, IReadOnlyList<Column> all) =>
        [.. wanted.Where(w => all.All(c => c.Name != w))];

    public async ValueTask WriteAsync(DateOnly tradeDate, Cell[] cells, ReadOnlyMemory<byte> raw)
    {
        // A name with no date is one file, or each trade date would overwrite the last.
        var oneFile = request.NameTemplate is { } template
            ? !template.Contains("{date}", StringComparison.Ordinal)
            : request.Layout == FileLayout.Single || request.Format == OutputFormat.NinjaTrader;

        DateOnly? key = oneFile ? null : tradeDate;

        if (writer is null || key != openFor)
        {
            await CloseAsync().ConfigureAwait(false);

            var path = Path.Combine(request.Output, Name(tradeDate));
            var dateOnly = request.Bars?.DayOrLonger == true;

            if (closed.TryGetValue(path, out var earlier))
            {
                writer = Writers.Reopen(request.Format, Unfinished(path), columns, format, request.Compression, earlier, dateOnly);
            }
            else
            {
                writer = Writers.Create(request.Format, Unfinished(path), columns, format, request.Compression, Metadata, dateOnly);
                files.Add(path);
            }

            writing = path;
            openFor = key;
        }

        for (var i = 0; i < projection.Length; i++)
            projected[i] = cells[projection[i]];

        writer.Write(projected, raw.Span);
        Rows++;
    }

    /// <summary>Closes the last file and gives every file its real name.</summary>
    public async ValueTask CompleteAsync()
    {
        await CloseAsync().ConfigureAwait(false);

        foreach (var path in files)
            File.Move(Unfinished(path), path, overwrite: true);

        completed = true;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await CloseAsync().ConfigureAwait(false);
        }
        finally
        {
            // Even when the last file would not close, as on a full disk, nothing half-written stays behind.
            if (!completed)
            {
                foreach (var path in files)
                    File.Delete(Unfinished(path));
            }
        }
    }

    private static string Unfinished(string path) => path + ".partial";

    private async ValueTask CloseAsync()
    {
        if (writer is not null)
        {
            await writer.DisposeAsync().ConfigureAwait(false);

            closed[writing!] = (writer as ParquetRowWriter)?.Finished;
        }

        writer = null;
    }

    private string Name(DateOnly tradeDate)
    {
        var ext = Writers.Extension(request.Format, request.Compression);
        var series = request.Symbol.Text;
        var date = tradeDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var from = request.TradeDates.From.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var to = request.TradeDates.To.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        if (request.NameTemplate is { } template)
        {
            return template.Replace("{series}", series, StringComparison.Ordinal).Replace("{kind}", kind, StringComparison.Ordinal)
                .Replace("{date}", date, StringComparison.Ordinal).Replace("{from}", from, StringComparison.Ordinal)
                .Replace("{to}", to, StringComparison.Ordinal).Replace("{ext}", ext, StringComparison.Ordinal);
        }

        if (request.Format == OutputFormat.NinjaTrader)
            return $"{NinjaTraderName(request.Symbol, request.TradeDates.From.Year)}.Last.{ext}";

        return request.Layout switch
        {
            FileLayout.Single => $"{series}.{kind}.{from}-{to}.{ext}",
            FileLayout.Partitioned => Path.Combine($"series={series}",
                $"date={tradeDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}", $"{kind}.{ext}"),
            _ => $"{series}.{kind}.{date}.{ext}"
        };
    }

    /// <summary>
    /// NinjaTrader names an instrument by root and expiry month (<c>ES 12-26</c>)
    /// and imports a file into the instrument its name gives.
    /// </summary>
    /// <param name="fromYear">The year the export starts in, which gives a one-digit year (<c>ESZ9</c>) its decade.</param>
    internal static string NinjaTraderName(SymbolSpec symbol, int fromYear)
    {
        if (symbol.Kind != SymbolKind.Contract || symbol.Text.Contains('-', StringComparison.Ordinal))
            return symbol.Text;

        var text = ContractSymbol.Expand(symbol.Text, fromYear);
        var digits = text.Length - text.TrimEnd("0123456789".ToCharArray()).Length;
        var month = ContractSymbol.Month(text[^(digits + 1)]);
        var year = text[^digits..];

        return month == 0
            ? text
            : string.Create(CultureInfo.InvariantCulture, $"{symbol.Root} {month:D2}-{year}");
    }
}
