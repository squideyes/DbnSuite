# DbnSuite.Client

The engine under [`dbnf`](https://www.nuget.org/packages/DbnSuite.Fetch), as a C# library. It reads a
[DbnSync](https://dbnsuite.squideyes.com/docs/dbnsync) archive of Databento® files, in S3 or a
folder, and turns it into what research and backtests need.

> **Independent project.** DbnSuite has no official connection with Databento; it is not made,
> endorsed or supported by Databento. Databento is a registered trademark of Databento, Inc.
>
> **For licensed access only.** Use it only as your Databento agreements allow, including their
> terms on storing, sharing and redistributing data.
>
> **At your own risk.** Free software under the MIT licence, provided as is, without warranty. See
> [NOTICE.md](https://github.com/squideyes/DbnSuite/blob/main/NOTICE.md).

```bash
dotnet add package DbnSuite.Client
```

It targets .NET 11 and is Native AOT compatible. Its only dependencies are the AWS S3 SDK and
System.IO.Hashing; zstd comes with .NET.

## What it does

- **Archive access:** list series and days, and fetch days through a local cache, following the
  archive's layout so a day never fetched is never taken for a day without trading.
- **A typed DBN reader:** DBN version 3 records as C# records through `IAsyncEnumerable`, with
  prices as decimals.
- **Market building blocks:** instruments, CME trade dates and sessions, order books, bars,
  contracts and roll schedules.
- **The whole exporter:** one `ExportRequest` does whatever `dbnf export` does: bars, back-adjusted
  continuous series, rebuilt order books, and CSV, Parquet, JSON Lines, DBN or NinjaTrader 8.
- **DbnTape:** build, prove and replay compact S1, Trade and L1 tapes, and deal seeded Monte Carlo
  runs of whole sessions.
- **Errors you can branch on:** expected failures come back as `Result<T>` with stable codes.

## A first look

```csharp
using DbnSuite.Client.Archive;

// A folder in the archive's layout; for S3, give Location, AccessKey, SecretKey and a cacheRoot
using var archive = DbnArchive.Open(new ArchiveOptions { Location = "dbn" }, cacheRoot: null);

var series = await archive.ListSeriesAsync(cancel);

if (!series.IsSuccess)
{
    Console.Error.WriteLine(series.Error);
    return;
}

foreach (var s in series.Value)
    Console.WriteLine($"{s.Root,-4} {s.Schema,-10} {s.Files,6} days  {s.First} to {s.Last}");
```

## Learn more

- [DbnSuite.Client docs](https://dbnsuite.squideyes.com/docs/dbnclient): every part, with examples.
- [DbnTape](https://dbnsuite.squideyes.com/docs/dbntape) and [the data layout](https://dbnsuite.squideyes.com/docs/layout).
- [Changelog](https://github.com/squideyes/DbnSuite/blob/main/CHANGELOG.md) and
  [source](https://github.com/squideyes/DbnSuite).
