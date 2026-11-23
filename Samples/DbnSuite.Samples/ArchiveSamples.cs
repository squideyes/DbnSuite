using DbnSuite.Client.Archive;
using DbnSuite.Client.Contracts;
using DbnSuite.Client.Dbn;
using DbnSuite.Client.Export;
using DbnSuite.Client.Time;

namespace DbnSuite.Samples;

/// <summary>
/// The archive samples on the DbnSuite.Client docs page, word for word. SamplesTests runs them
/// on a synthetic archive and checks the page still quotes them, so the two can't drift apart.
/// </summary>
public static class ArchiveSamples
{
    /// <summary>Opening an archive in S3 or in a folder. Compiled but not run here: S3 needs your key.</summary>
    public static void Open()
    {
        using var archive = DbnArchive.Open(new ArchiveOptions
        {
            Location = "https://s3.example.com",
            AccessKey = "dbn-reader",
            SecretKey = Environment.GetEnvironmentVariable("DBN_SECRET")!
        }, cacheRoot: Path.Combine(Path.GetTempPath(), "dbn-cache"));

        // Or a folder in the archive's layout, such as one dbnf pull --out filled
        using var local = DbnArchive.Open(new ArchiveOptions { Location = "dbn" }, cacheRoot: null);
    }

    /// <summary>Every series in the archive, with its days.</summary>
    public static async Task ListAsync(DbnArchive archive, CancellationToken cancel)
    {
        var series = await archive.ListSeriesAsync(cancel);

        if (!series.IsSuccess)
        {
            Console.Error.WriteLine(series.Error);
            return;
        }

        foreach (var s in series.Value)
            Console.WriteLine($"{s.Root,-4} {s.Schema,-10} {s.Files,6} days  {s.First} to {s.Last}");
    }

    /// <summary>Every trade of one day, from its order book events.</summary>
    public static async Task ReadAsync(DbnArchive archive, CancellationToken cancel)
    {
        var opened = await archive.OpenAsync("mbo", "MES", new DateOnly(2026, 10, 30), cancel);

        if (opened.IsSuccess)
        {
            await using var reader = opened.Value;

            await foreach (var record in reader.ReadAsync(cancel))
            {
                if (record is MboRecord { Action: 'T' } trade)
                    Console.WriteLine($"{DbnTime.Format(trade.TsEvent)} {trade.Quantity} @ {trade.Price}");
            }
        }
    }

    /// <summary>Back-adjusted 5-minute bars of a continuous series to Parquet, then the series' rolls.</summary>
    public static async Task ExportAsync(DbnArchive archive, CancellationToken cancel)
    {
        var symbol = SymbolSpec.Parse("ES.v.0").Value;
        var dates = new DateSpan(new DateOnly(2026, 1, 2), new DateOnly(2026, 10, 30));

        var request = new ExportRequest
        {
            Schema = "ohlcv-1s",
            Symbol = symbol,
            TradeDates = dates,
            Sessions = SessionFilter.Create(dates, SessionKind.Rth, symbol.Root).Value,
            Output = "out",
            Bars = BarSpec.Parse("5m").Value,
            Adjustment = Adjustment.Difference,
            Format = OutputFormat.Parquet,
            Layout = FileLayout.Single
        };

        var result = await new Exporter(archive).RunAsync(request, progress: null, cancel);

        if (result.IsSuccess)
            Console.WriteLine($"{result.Value.Rows:N0} bars in {result.Value.Files.Count} file(s), {result.Value.Rolls.Count} rolls");
        else
            Console.Error.WriteLine(result.Error);

        var schedule = await RollSchedule.BuildAsync(archive, symbol, dates, rollDays: null, progress: null, cancel);

        if (schedule.IsSuccess)
        {
            foreach (var roll in schedule.Value.Rolls)
                Console.WriteLine($"{roll.TradeDate}: {roll.From.Symbol} to {roll.To.Symbol}, gap {roll.ToSettle - roll.FromSettle}");
        }
    }

    /// <summary>Branching on an error's code: here, a day the archive holds as having no data.</summary>
    public static async Task ErrorsAsync(DbnArchive archive, CancellationToken cancel)
    {
        var day = await archive.OpenAsync("mbo", "MES", new DateOnly(2026, 11, 1), cancel);

        if (day.Error?.Code == DbnSuite.Client.Errors.NoData)
            Console.WriteLine("A day with no trading.");
    }
}
