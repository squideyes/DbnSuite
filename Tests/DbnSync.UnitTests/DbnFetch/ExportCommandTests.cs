using System.IO.Compression;
using DbnFetch;
using Shouldly;
using static DbnSync.UnitTests.FetchTests.SampleArchive;
using static DbnSync.UnitTests.TestData;

namespace DbnSync.UnitTests.FetchTests;

[Collection(DbnfCollection.Name)]
public sealed class ExportCommandTests : IDisposable
{
    private const string Date = "--from 2026-10-30";
    private const string TwoDays = "--from 2026-10-29 --to 2026-10-30";
    private const string FrontFile = "MNQZ26.ohlcv-1s.20261030";
    private const string Csv = FrontFile + ".csv";
    private const string BarsHeader = "ts_event,ts_end,symbol,open,high,low,close,volume";
    private const string RecordsHeader = "ts_event,symbol,instrument_id,open,high,low,close,volume";
    private const string NothingMatched = "Nothing matched, so no files were written.";
    private const string Series = "MNQ.c.0";
    private const string MboSeries = "MNQ.n.0";
    private const string RollRange = "--from 2026-12-09 --to 2026-12-11";

    private static readonly DateOnly Traded = Day(10, 30);
    private static readonly DateOnly DayBefore = Day(10, 29);

    /// <summary>07:00 Chicago: inside the Globex session, before regular hours.</summary>
    private static readonly ulong Early = At(Traded, 12);

    /// <summary>09:00 Chicago: inside regular hours.</summary>
    private static readonly ulong Morning = At(Traded, 14);

    private readonly Dbnf dbnf = new();

    public void Dispose() => dbnf.Dispose();

    private string Into => dbnf.OutOption;

    /// <summary>Two front bars an hour and more apart, one of the next contract, one of the spread.</summary>
    private void OneDayOfBars()
    {
        dbnf.Archive.AddDefinitions(Traded);
        dbnf.Archive.AddEmpty(SampleArchive.Ohlcv, DayBefore);
        dbnf.Archive.Add(SampleArchive.Ohlcv, Traded,
            Bar(FrontId, Early, 7800m, 7810m, 7790m, 7805m, 5),
            Bar(FrontId, Morning, 7820.25m),
            Bar(FrontId, Morning + 1_000_000_000, 7821m),
            Bar(NextId, Morning, 7900m),
            Bar(SpreadId, Morning, 80m));
    }

    private void OneDayOfOrders()
    {
        dbnf.Archive.AddDefinitions(Traded);
        dbnf.Archive.AddEmpty(Mbo, DayBefore);
        dbnf.Archive.Add(Mbo, Root, Traded, new Dictionary<string, uint> { [MboSeries] = FrontId },
            Order(FrontId, Morning, 7820m, 2, 'A', 'B', 1),
            Order(FrontId, Morning + 1, 7821m, 2, 'A', 'A', 2),
            Order(FrontId, Morning + 2, 7821m, 1, 'T', 'B', 3));
    }

    private void OverARoll()
    {
        var before = new DateOnly(2026, 12, 9);
        var rollDay = new DateOnly(2026, 12, 10);

        dbnf.Archive.Add(Definition, new DateOnly(2026, 12, 1), FrontDefinition(), NextDefinition());
        dbnf.Archive.AddDefinitions(before);
        dbnf.Archive.AddDefinitions(rollDay);
        dbnf.Archive.AddEmpty(SampleArchive.Ohlcv, before.AddDays(-1));
        dbnf.Archive.AddEmpty(SampleArchive.Ohlcv, rollDay.AddDays(1));
        dbnf.Archive.Add(SampleArchive.Ohlcv, before, Bar(FrontId, At(before, 15), 100m), Bar(NextId, At(before, 15), 110m));
        dbnf.Archive.Add(SampleArchive.Ohlcv, rollDay, Bar(FrontId, At(rollDay, 15), 101m), Bar(NextId, At(rollDay, 15), 111m));
        dbnf.Archive.Add(Statistics, before,
            Stat(FrontId, At(before, 21), Settlement, Price(100m)),
            Stat(NextId, At(before, 21), Settlement, Price(110m)));
    }

    [Fact]
    public async Task A_contract_is_written_to_a_csv_named_for_it()
    {
        OneDayOfBars();

        var run = await dbnf.OnArchiveAsync($"export {SampleArchive.Ohlcv} {Front} {Date} {Into}");

        run.ExitCode.ShouldBe(Session.Ok);
        dbnf.Written().ShouldBe([Csv]);

        var lines = dbnf.ReadOutput(Csv);

        lines.Length.ShouldBe(4);
        lines[0].ShouldBe(RecordsHeader);
        lines[2].ShouldContain($",{Front},{FrontId},7820.25,7820.25,7820.25,7820.25,10");
    }

    [Fact]
    public async Task Export_says_what_it_is_doing_and_what_it_wrote()
    {
        OneDayOfBars();

        var run = await dbnf.OnArchiveAsync($"export {SampleArchive.Ohlcv} {Front} {Date} {Into}");

        run.Lines[0].ShouldBe($"Exporting {Front} {SampleArchive.Ohlcv}, trade dates 2026-10-30 to 2026-10-30...");
        run.Lines[1].ShouldStartWith("Wrote 3 rows to 1 files in ");
        run.Lines[1].ShouldEndWith($"in {dbnf.Output}");
    }

    [Fact]
    public async Task Times_are_iso_in_chicago_by_default()
    {
        OneDayOfBars();

        await dbnf.OnArchiveAsync($"export {SampleArchive.Ohlcv} {Front} {Date} {Into}");

        dbnf.ReadOutput(Csv)[2].ShouldStartWith("2026-10-30T09:00:00.000000000-05:00,");
    }

    [Fact]
    public async Task A_root_writes_every_contract_and_spread()
    {
        OneDayOfBars();

        var run = await dbnf.OnArchiveAsync($"export {SampleArchive.Ohlcv} {Root} {Date} {Into}");

        run.Lines[1].ShouldStartWith("Wrote 5 rows");
    }

    [Fact]
    public async Task No_spreads_leaves_spreads_out()
    {
        OneDayOfBars();

        var run = await dbnf.OnArchiveAsync($"export {SampleArchive.Ohlcv} {Root} {Date} {Into} --no-spreads");

        run.Lines[1].ShouldStartWith("Wrote 4 rows");
    }

    [Fact]
    public async Task Regular_hours_leave_the_early_morning_out()
    {
        OneDayOfBars();

        var run = await dbnf.OnArchiveAsync($"export {SampleArchive.Ohlcv} {Front} {Date} {Into} --session rth");

        run.Lines[1].ShouldStartWith("Wrote 2 rows");
    }

    [Fact]
    public async Task Hours_of_your_own_are_kept_in_their_own_zone()
    {
        OneDayOfBars();

        var run = await dbnf.OnArchiveAsync($"export {SampleArchive.Ohlcv} {Front} {Date} {Into} --hours 07:30-08:30 --hours-tz America/New_York");

        run.Lines[1].ShouldStartWith("Wrote 1 rows");
    }

    [Fact]
    public async Task Hours_default_to_chicago()
    {
        OneDayOfBars();

        var run = await dbnf.OnArchiveAsync($"export {SampleArchive.Ohlcv} {Front} {Date} {Into} --hours 06:30-07:30");

        run.Lines[1].ShouldStartWith("Wrote 1 rows");
    }

    [Fact]
    public async Task Bars_are_built_and_announced()
    {
        OneDayOfBars();

        var run = await dbnf.OnArchiveAsync($"export {SampleArchive.Ohlcv} {Front} {Date} {Into} --bars 1h");

        run.Lines[0].ShouldBe($"Exporting {Front} {SampleArchive.Ohlcv} as 1h bars, trade dates 2026-10-30 to 2026-10-30...");
        run.Lines[1].ShouldStartWith("Wrote 2 rows");

        var lines = dbnf.ReadOutput("MNQZ26.1h.20261030.csv");

        lines[0].ShouldBe(BarsHeader);
    }

    [Fact]
    public async Task Bars_from_mbo_count_its_trades()
    {
        OneDayOfOrders();

        var run = await dbnf.OnArchiveAsync($"export {Mbo} {Front} {Date} {Into} --bars 1t");

        run.ExitCode.ShouldBe(Session.Ok);
        run.Lines[1].ShouldStartWith("Wrote 1 rows");
    }

    [Fact]
    public async Task As_rebuilds_a_schema_from_mbo_and_says_so()
    {
        OneDayOfOrders();

        var run = await dbnf.OnArchiveAsync($"export {Mbo} {Front} {Date} {Into} --as TRADES");

        run.ExitCode.ShouldBe(Session.Ok);
        run.Lines[0].ShouldBe($"Exporting {Front} {Mbo} as trades, trade dates 2026-10-30 to 2026-10-30...");
        dbnf.Written().ShouldBe(["MNQZ26.trades.20261030.csv"]);
    }

    [Fact]
    public async Task Mbo_is_kept_for_the_open_interest_series()
    {
        OneDayOfOrders();

        var run = await dbnf.OnArchiveAsync($"export {Mbo} {MboSeries} {Date} {Into}");

        run.ExitCode.ShouldBe(Session.Ok);
        run.Lines[^1].ShouldStartWith("Wrote 3 rows");
    }

    [Theory]
    [InlineData("parquet", "parquet")]
    [InlineData("jsonl", "jsonl")]
    [InlineData("dbn", "dbn")]
    [InlineData("CSV", "csv")]
    public async Task Format_picks_the_kind_of_file(string format, string extension)
    {
        OneDayOfBars();

        var run = await dbnf.OnArchiveAsync($"export {SampleArchive.Ohlcv} {Front} {Date} {Into} --format {format}");

        run.ExitCode.ShouldBe(Session.Ok);
        dbnf.Written().ShouldBe([$"{FrontFile}.{extension}"]);
    }

    [Fact]
    public async Task Nt8_is_named_for_ninjatraders_instrument()
    {
        OneDayOfBars();

        var run = await dbnf.OnArchiveAsync($"export {SampleArchive.Ohlcv} {Front} {Date} {Into} --format nt8 --bars 1m");

        run.ExitCode.ShouldBe(Session.Ok);
        dbnf.Written().ShouldBe(["MNQ 12-26.Last.txt"]);
    }

    [Fact]
    public async Task Gzip_compresses_the_text_formats()
    {
        OneDayOfBars();

        await dbnf.OnArchiveAsync($"export {SampleArchive.Ohlcv} {Front} {Date} {Into} --compress gzip");

        var written = dbnf.Written().ShouldHaveSingleItem();
        await using var gzip = new GZipStream(File.OpenRead(Path.Combine(dbnf.Output, written)), CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);

        (await reader.ReadLineAsync()).ShouldBe(RecordsHeader);
    }

    [Fact]
    public async Task Zstd_is_a_compression_too()
    {
        OneDayOfBars();

        var run = await dbnf.OnArchiveAsync($"export {SampleArchive.Ohlcv} {Front} {Date} {Into} --compress zstd");

        run.ExitCode.ShouldBe(Session.Ok);
        dbnf.Written().ShouldHaveSingleItem().ShouldContain(FrontFile);
    }

    [Fact]
    public async Task Tz_writes_times_in_another_zone()
    {
        OneDayOfBars();

        await dbnf.OnArchiveAsync($"export {SampleArchive.Ohlcv} {Front} {Date} {Into} --tz UTC");

        dbnf.ReadOutput(Csv)[2].ShouldStartWith("2026-10-30T14:00:00.000000000Z,");
    }

    [Theory]
    [InlineData("unix-ns", "1793368800000000000,")]
    [InlineData("unix-ms", "1793368800000,")]
    public async Task Time_writes_unix_times(string mode, string start)
    {
        OneDayOfBars();

        await dbnf.OnArchiveAsync($"export {SampleArchive.Ohlcv} {Front} {Date} {Into} --time {mode}");

        dbnf.ReadOutput(Csv)[2].ShouldStartWith(start);
    }

    [Fact]
    public async Task Excel_writes_a_serial_date()
    {
        OneDayOfBars();

        var run = await dbnf.OnArchiveAsync($"export {SampleArchive.Ohlcv} {Front} {Date} {Into} --time excel");

        run.ExitCode.ShouldBe(Session.Ok);
        dbnf.ReadOutput(Csv)[2].ShouldStartWith("46");
    }

    [Fact]
    public async Task Split_time_writes_a_date_column_and_a_time_column()
    {
        OneDayOfBars();

        await dbnf.OnArchiveAsync($"export {SampleArchive.Ohlcv} {Front} {Date} {Into} --split-time");

        dbnf.ReadOutput(Csv)[2].ShouldStartWith("2026-10-30,09:00:00");
    }

    [Theory]
    [InlineData("ticks", "31281")]
    [InlineData("raw", "7820250000000")]
    public async Task Price_writes_other_units(string mode, string price)
    {
        OneDayOfBars();

        await dbnf.OnArchiveAsync($"export {SampleArchive.Ohlcv} {Front} {Date} {Into} --price {mode}");

        dbnf.ReadOutput(Csv)[2].ShouldContain($",{price}");
    }

    [Fact]
    public async Task Columns_keeps_some_in_that_order()
    {
        OneDayOfBars();

        await dbnf.OnArchiveAsync($"export {SampleArchive.Ohlcv} {Front} {Date} {Into} --columns CLOSE,symbol");

        var lines = dbnf.ReadOutput(Csv);

        lines[0].ShouldBe("close,symbol");
        lines[2].ShouldBe($"7820.25,{Front}");
    }

    [Fact]
    public async Task A_column_that_is_not_one_names_those_that_are()
    {
        OneDayOfBars();

        var run = await dbnf.OnArchiveAsync($"export {SampleArchive.Ohlcv} {Front} {Date} {Into} --columns nope");

        run.ExitCode.ShouldBe(Session.Usage);
        run.Error.ShouldContain("No column nope. The columns are ts_event, ");
    }

    [Fact]
    public async Task Each_trade_date_gets_its_own_file_by_default()
    {
        dbnf.Archive.AddDefinitions(Day(10, 28));
        dbnf.Archive.AddDefinitions(DayBefore);
        dbnf.Archive.AddDefinitions(Traded);
        dbnf.Archive.AddEmpty(SampleArchive.Ohlcv, Day(10, 28));
        dbnf.Archive.Add(SampleArchive.Ohlcv, DayBefore, Bar(FrontId, At(DayBefore, 14), 1m));
        dbnf.Archive.Add(SampleArchive.Ohlcv, Traded, Bar(FrontId, Morning, 2m));

        await dbnf.OnArchiveAsync($"export {SampleArchive.Ohlcv} {Front} {TwoDays} {Into}");

        dbnf.Written().ShouldBe(["MNQZ26.ohlcv-1s.20261029.csv", Csv]);
    }

    [Fact]
    public async Task Single_writes_one_file_for_the_range()
    {
        dbnf.Archive.AddDefinitions(DayBefore);
        dbnf.Archive.AddDefinitions(Traded);
        dbnf.Archive.AddEmpty(SampleArchive.Ohlcv, Day(10, 28));
        dbnf.Archive.Add(SampleArchive.Ohlcv, DayBefore, Bar(FrontId, At(DayBefore, 14), 1m));
        dbnf.Archive.Add(SampleArchive.Ohlcv, Traded, Bar(FrontId, Morning, 2m));

        await dbnf.OnArchiveAsync($"export {SampleArchive.Ohlcv} {Front} {TwoDays} {Into} --single");

        dbnf.Written().ShouldBe(["MNQZ26.ohlcv-1s.20261029-20261030.csv"]);
    }

    [Fact]
    public async Task Partition_writes_hive_folders()
    {
        OneDayOfBars();

        await dbnf.OnArchiveAsync($"export {SampleArchive.Ohlcv} {Front} {Date} {Into} --partition");

        dbnf.Written().ShouldBe([$"series={Front}/date=2026-10-30/{SampleArchive.Ohlcv}.csv"]);
    }

    [Fact]
    public async Task Name_is_a_template_for_file_names()
    {
        OneDayOfBars();

        await dbnf.OnArchiveAsync($"export {SampleArchive.Ohlcv} {Front} {Date} {Into} --name {{series}}_{{kind}}_{{date}}_{{from}}_{{to}}.{{ext}}");

        dbnf.Written().ShouldBe(["MNQZ26_ohlcv-1s_20261030_20261030_20261030.csv"]);
    }

    [Fact]
    public async Task Days_the_archive_lacks_are_reported_and_left_out()
    {
        dbnf.Archive.AddDefinitions(Traded);
        dbnf.Archive.Add(SampleArchive.Ohlcv, Traded, Bar(FrontId, Morning, 2m));

        var run = await dbnf.OnArchiveAsync($"export {SampleArchive.Ohlcv} {Front} {Date} {Into}");

        run.ExitCode.ShouldBe(Session.Ok);
        run.Error.ShouldContain("Not in the archive, so left out: 2026-10-29 (UTC days).");
    }

    [Fact]
    public async Task Days_with_no_data_are_not_reported_as_missing()
    {
        OneDayOfBars();

        var run = await dbnf.OnArchiveAsync($"export {SampleArchive.Ohlcv} {Front} {Date} {Into}");

        run.Error.ShouldBeEmpty();
    }

    [Fact]
    public async Task Nothing_matching_says_so_and_writes_nothing()
    {
        dbnf.Archive.AddEmpty(SampleArchive.Ohlcv, DayBefore);
        dbnf.Archive.AddEmpty(SampleArchive.Ohlcv, Traded);

        var run = await dbnf.OnArchiveAsync($"export {SampleArchive.Ohlcv} {Front} {Date} {Into}");

        run.ExitCode.ShouldBe(Session.Ok);
        run.Lines[^1].ShouldBe(NothingMatched);
        dbnf.Written().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_continuous_series_reports_its_rolls_with_the_gap()
    {
        OverARoll();

        var run = await dbnf.OnArchiveAsync($"export {SampleArchive.Ohlcv} {Series} {RollRange} {Into}");

        run.ExitCode.ShouldBe(Session.Ok);
        run.Lines[1].ShouldBe($"  rolled 2026-12-10: {Front} to {Next}, gap 10");
    }

    [Fact]
    public async Task A_roll_with_no_settlements_has_no_gap_to_report()
    {
        OverARoll();
        File.Delete(dbnf.Archive.PathOf(DbnClient.Archive.DbnSyncLayout.Data(Dataset, Statistics, Root, new DateOnly(2026, 12, 9))));

        var run = await dbnf.OnArchiveAsync($"export {SampleArchive.Ohlcv} {Series} {RollRange} {Into}");

        run.Lines[1].ShouldBe($"  rolled 2026-12-10: {Front} to {Next}");
    }

    [Fact]
    public async Task Difference_adds_the_gap_to_earlier_prices()
    {
        OverARoll();

        await dbnf.OnArchiveAsync($"export {SampleArchive.Ohlcv} {Series} {RollRange} {Into} --adjust difference --single --columns close");

        dbnf.ReadOutput("MNQ.c.0.ohlcv-1s.20261209-20261211.csv").ShouldBe(["close", "110", "111"]);
    }

    [Fact]
    public async Task Ratio_scales_earlier_prices()
    {
        OverARoll();

        await dbnf.OnArchiveAsync($"export {SampleArchive.Ohlcv} {Series} {RollRange} {Into} --adjust ratio --single --columns close");

        dbnf.ReadOutput("MNQ.c.0.ohlcv-1s.20261209-20261211.csv").ShouldBe(["close", "110", "111"]);
    }

    [Fact]
    public async Task Roll_days_moves_the_roll()
    {
        OverARoll();

        var run = await dbnf.OnArchiveAsync($"export {SampleArchive.Ohlcv} {Series} {RollRange} {Into} --roll-days 7");

        run.Lines[1].ShouldStartWith("  rolled 2026-12-11");
    }

    [Theory]
    [InlineData("tickles MNQZ26 --from 2026-10-30", "\"tickles\" is not a schema.")]
    [InlineData("ohlcv-1s NOT!A!SYMBOL --from 2026-10-30", "\"NOT!A!SYMBOL\" is not a symbol")]
    [InlineData("ohlcv-1s MNQZ26", "Give the days as one of")]
    [InlineData("ohlcv-1s MNQ --from 2026-10-30 --bars 1m --format nt8", "NinjaTrader output is one instrument a file")]
    [InlineData("ohlcv-1s MNQZ26 --from 2026-10-30 --tz Mars/Olympus", "\"Mars/Olympus\" is not a time zone")]
    [InlineData("ohlcv-1s MNQZ26 --from 2026-10-30 --session night", "\"night\" is not a valid session; use eth, rth.")]
    [InlineData("ohlcv-1s ZZZ --from 2026-10-30 --session rth", "There are no regular hours for ZZZ; give them with --hours.")]
    [InlineData("ohlcv-1s MNQZ26 --from 2026-10-30 --hours noon", "\"noon\" is not a window of hours")]
    [InlineData("ohlcv-1s MNQZ26 --from 2026-10-30 --hours 09:00-10:00 --hours-tz Mars/Olympus", "\"Mars/Olympus\" is not a time zone")]
    [InlineData("ohlcv-1s MNQZ26 --from 2026-10-30 --bars 7x", "\"7x\" is not a bar size")]
    [InlineData("ohlcv-1s MNQZ26 --from 2026-10-30 --format xml", "\"xml\" is not a valid format; use csv, parquet, jsonl, dbn, nt8.")]
    [InlineData("ohlcv-1s MNQZ26 --from 2026-10-30 --compress rar", "\"rar\" is not a valid compression; use none, gzip, zstd.")]
    [InlineData("ohlcv-1s MNQZ26 --from 2026-10-30 --time julian", "\"julian\" is not a valid time; use iso, unix-ns, unix-ms, excel.")]
    [InlineData("ohlcv-1s MNQZ26 --from 2026-10-30 --price pips", "\"pips\" is not a valid price; use decimal, ticks, raw.")]
    [InlineData("ohlcv-1s MNQZ26 --from 2026-10-30 --adjust panama", "\"panama\" is not a valid adjustment; use none, difference, ratio.")]
    [InlineData("ohlcv-1s MNQZ26 --from 2026-10-30 --single --partition", "--single and --partition cannot both be given.")]
    [InlineData("ohlcv-1s MNQZ26 --from 2026-10-30 --partition --name {series}.{ext}", "--name cannot be given with it")]
    [InlineData("ohlcv-1s MNQZ26 --from 2026-10-30 --format parquet --compress gzip", "A Parquet file is always compressed inside.")]
    [InlineData("ohlcv-1s MNQZ26 --from 2026-10-30 --bars 1m --format nt8 --compress zstd", "NinjaTrader imports plain text only.")]
    [InlineData("ohlcv-1s MNQZ26 --from 2026-10-30 --hours-tz America/New_York", "it needs --hours")]
    [InlineData("ohlcv-1s MNQZ26 --from 2026-10-30 --hours 09:00-10:00 --session rth", "give only one of them")]
    [InlineData("ohlcv-1s MNQZ26 --from 2026-10-30 --split-time --time unix-ns", "--split-time splits an iso time")]
    [InlineData("ohlcv-1s MNQZ26 --from 10/30/2026", "\"10/30/2026\" is not a date")]
    [InlineData("ohlcv-1s MNQZ26 --last 5000y", "\"5000y\" is not a length")]
    [InlineData("ohlcv-1s MNQZ26 --from 2026-10-30 --as trades", "the schema must be mbo")]
    [InlineData("mbo MNQZ26 --from 2026-10-30 --as nonsense", "\"nonsense\" is not something mbo becomes")]
    [InlineData("mbo MNQZ26 --from 2026-10-30 --as trades --bars 1m", "--as and --bars cannot both be given")]
    [InlineData("mbo MNQ.v.0 --from 2026-10-30", "mbo is kept for MNQ.n.0 and MNQ.n.1 only.")]
    [InlineData("ohlcv-1s MNQZ26 --from 2026-10-30 --bars 500t", "500t bars count trades, so they come from mbo.")]
    [InlineData("statistics MNQZ26 --from 2026-10-30 --bars 1m", "Bars are built from ohlcv-1s")]
    [InlineData("ohlcv-1s MNQZ26 --from 2026-10-30 --format nt8", "NinjaTrader output is for bars")]
    [InlineData("ohlcv-1s MNQZ26 --from 2026-10-30 --format dbn --bars 1m", "DBN output copies records as stored")]
    [InlineData("ohlcv-1s MNQZ26 --from 2026-10-30 --format dbn --columns close", "DBN output keeps every field")]
    public async Task Bad_input_is_refused_with_a_reason(string arguments, string message)
    {
        var run = await dbnf.OnArchiveAsync($"export {arguments} {Into}");

        run.ExitCode.ShouldBe(Session.Usage);
        run.Error.ShouldContain(message);
        dbnf.Written().ShouldBeEmpty();
    }

    [Fact]
    public async Task The_schema_and_symbol_are_required()
    {
        var run = await dbnf.OnArchiveAsync("export");

        run.ExitCode.ShouldBe(Dbnf.ParseFailure);
    }

    [Fact]
    public async Task Without_a_login_nothing_is_read()
    {
        var run = await dbnf.RunAsync($"export {SampleArchive.Ohlcv} {Front} {Date} {Into}");

        run.ExitCode.ShouldBe(Session.Usage);
    }

    [Fact]
    public async Task An_s3_archive_reads_through_the_cache()
    {
        using var s3 = new FakeS3(dbnf.ArchiveFolder);
        OneDayOfBars();
        await dbnf.LogInAsync(s3);

        var first = await dbnf.RunAsync($"export {SampleArchive.Ohlcv} {Front} {Date} {Into}");
        var downloads = s3.Downloads.Count;
        var second = await dbnf.RunAsync($"export {SampleArchive.Ohlcv} {Front} {Date} {Into}");

        first.ExitCode.ShouldBe(Session.Ok);
        second.ExitCode.ShouldBe(Session.Ok);
        downloads.ShouldBeGreaterThan(0);
        s3.Downloads.Count.ShouldBe(downloads);
    }

    [Theory]
    [InlineData("--as trades")]
    [InlineData("--bars 1m")]
    [InlineData("")]
    public async Task An_mbo_series_cannot_be_adjusted(string options)
    {
        OneDayOfOrders();

        var run = await dbnf.OnArchiveAsync($"export {Mbo} {MboSeries} {Date} {options} --adjust difference {Into}");

        run.ExitCode.ShouldBe(Session.Usage);
        run.Error.ShouldContain("mbo keeps raw prices");
        dbnf.Written().ShouldBeEmpty();
    }

    [Fact]
    public async Task An_adjusted_export_over_a_roll_with_no_settlement_fails_and_names_it()
    {
        OverARoll();
        dbnf.Archive.Add(Statistics, new DateOnly(2026, 12, 9), Stat(FrontId, At(new DateOnly(2026, 12, 9), 21), Settlement, Price(100m)));

        var run = await dbnf.OnArchiveAsync($"export {SampleArchive.Ohlcv} {Series} {RollRange} --adjust difference {Into}");

        run.ExitCode.ShouldBe(Session.Failed);
        run.Error.ShouldContain($"The roll on 2026-12-10 from {Front} to {Next}");
        dbnf.Written().ShouldBeEmpty();
    }

    [Fact]
    public async Task The_same_export_unadjusted_goes_ahead_and_shows_no_gap()
    {
        OverARoll();
        dbnf.Archive.Add(Statistics, new DateOnly(2026, 12, 9), Stat(FrontId, At(new DateOnly(2026, 12, 9), 21), Settlement, Price(100m)));

        var run = await dbnf.OnArchiveAsync($"export {SampleArchive.Ohlcv} {Series} {RollRange} {Into}");

        run.ExitCode.ShouldBe(Session.Ok);
        run.Out.ShouldContain($"rolled 2026-12-10: {Front} to {Next}{Environment.NewLine}");
    }

    [Fact]
    public async Task Split_time_works_in_json_lines()
    {
        OneDayOfBars();

        var run = await dbnf.OnArchiveAsync($"export {SampleArchive.Ohlcv} {Front} {Date} --format jsonl --split-time {Into}");

        run.ExitCode.ShouldBe(Session.Ok);
        dbnf.ReadOutput($"{FrontFile}.jsonl")[0].ShouldStartWith("""{"ts_event_date":"2026-10-30","ts_event_time":"07:00:00.000000000","symbol":""");
    }

    [Fact]
    public async Task A_daily_rth_bar_for_ninjatrader_has_no_time()
    {
        OneDayOfBars();

        var run = await dbnf.OnArchiveAsync($"export {SampleArchive.Ohlcv} {Front} {Date} --bars 1d --session rth --format nt8 {Into}");

        run.ExitCode.ShouldBe(Session.Ok);
        dbnf.ReadOutput(dbnf.Written().Single()).ShouldHaveSingleItem().ShouldStartWith("20261030;7820.25;");
    }
}
