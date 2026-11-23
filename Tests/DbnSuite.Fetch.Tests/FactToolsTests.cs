using System.Text.Json;
using DbnSuite.Client.Archive;
using DbnSuite.Client.Tape;
using DbnSuite.Fetch;
using Shouldly;
using static DbnSuite.Tests.Fetch.SampleArchive;
using static DbnSuite.Testing.TestData;

namespace DbnSuite.Tests.Fetch;

[Collection(DbnfCollection.Name)]
public sealed class FactToolsTests : IDisposable
{
    private const string Es = "ES";
    private const string Cl = "CL";
    private const string CatalogKey = "GLBX.MDP3/catalog/ES.catalog.json";
    private const string ChicagoWinter = "-06:00";
    private const string ChicagoSummer = "-05:00";

    private static readonly DateOnly Traded = Day(10, 30);
    private static readonly DateOnly Saturday = Day(10, 31);
    private static readonly DateOnly Sunday = Day(11, 1);
    private static readonly DateOnly Monday = Day(11, 2);
    private static readonly DateOnly Thursday = Day(10, 29);
    private static readonly DateOnly FullDate = Day(10, 28);
    private static readonly DateOnly ShortDate = Day(10, 29);

    private readonly Dbnf dbnf = new();
    private readonly FakeS3 s3;

    public FactToolsTests() => s3 = new FakeS3(dbnf.ArchiveFolder);

    public void Dispose()
    {
        s3.Dispose();
        dbnf.Dispose();
    }

    private static string Iso(DateOnly day) => Text.Day(day);

    private static string[] Dates(JsonElement array) => [.. array.EnumerateArray().Select(e => e.GetString()!)];

    /// <summary>A Friday with a file, a weekend of markers, and a Thursday and Monday the archive never fetched.</summary>
    private void AWeekOfDefinitions()
    {
        dbnf.Archive.AddDefinitions(Traded);
        dbnf.Archive.AddEmpty(Definition, Saturday);
        dbnf.Archive.AddEmpty(Definition, Sunday);
    }

    [Fact]
    public async Task Archive_status_names_a_folder_archive_and_reads_it()
    {
        AWeekOfDefinitions();
        await using var run = await McpRun.OnArchiveAsync(dbnf);

        var status = await run.CallAsync("archive_status");

        status.GetProperty("kind").GetString().ShouldBe("folder");
        status.GetProperty("location").GetString().ShouldBe(dbnf.ArchiveFolder);
        status.GetProperty("reachable").GetBoolean().ShouldBeTrue();
        status.GetProperty("layout").GetString().ShouldBe(DbnSyncLayout.Name);
        status.GetProperty("data_tools").GetBoolean().ShouldBeFalse();
        status.TryGetProperty("profile", out _).ShouldBeFalse();
        status.TryGetProperty("out", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Archive_status_with_no_login_says_so_and_does_not_fail()
    {
        await using var run = await McpRun.StartAsync(dbnf);

        var status = await run.CallAsync("archive_status");

        status.GetProperty("reachable").GetBoolean().ShouldBeFalse();
        status.GetProperty("profile").GetString().ShouldBe(Profiles.DefaultName);
        status.GetProperty("problem").GetString().ShouldNotBeNull().ShouldContain("dbnf login");
    }

    [Fact]
    public async Task Archive_status_of_an_s3_login_shows_where_it_is_and_never_the_key()
    {
        AWeekOfDefinitions();
        await dbnf.LogInAsync(s3);
        await using var run = await McpRun.StartAsync(dbnf);

        var result = await run.RawAsync("archive_status");
        var status = result.StructuredContent!.Value;

        status.GetProperty("kind").GetString().ShouldBe("s3");
        status.GetProperty("location").GetString().ShouldBe(s3.Location);
        status.GetProperty("bucket").GetString().ShouldBe(FakeS3.Bucket);
        status.GetProperty("key_saved").GetBoolean().ShouldBeTrue();
        status.GetProperty("reachable").GetBoolean().ShouldBeTrue();
        status.GetProperty("cache").GetString().ShouldBe(Path.Combine(dbnf.CacheFolder, Profiles.DefaultName));
        status.GetRawText().ShouldNotContain(TestKey.Secret);
        status.GetRawText().ShouldNotContain(TestKey.Access);
        McpRun.TextOf(result).ShouldNotContain(TestKey.Secret);
        McpRun.TextOf(result).ShouldNotContain(TestKey.Access);
    }

    [Fact]
    public async Task No_tool_result_or_refusal_ever_carries_the_key()
    {
        AWeekOfDefinitions();
        await dbnf.LogInAsync(s3);
        await using var run = await McpRun.StartAsync(dbnf, $"--allow-data {dbnf.OutOption}");

        var texts = new List<string>();

        async Task CallAsync(string tool, Dictionary<string, object?>? arguments = null)
        {
            var result = await run.RawAsync(tool, arguments);

            texts.Add(McpRun.TextOf(result));
            texts.Add(result.StructuredContent?.GetRawText() ?? "");
        }

        await CallAsync("archive_status");
        await CallAsync("list_series");
        await CallAsync("coverage", new() { ["root"] = Root, ["schema"] = Definition });
        await CallAsync("contracts", new() { ["root"] = Root });
        await CallAsync("show_records", new() { ["schema"] = Definition, ["symbol"] = Root, ["day"] = Iso(Traded) });
        await CallAsync("pull", new() { ["schemas"] = Definition, ["roots"] = Root, ["from"] = Iso(Traded) });

        s3.Deny = true;

        await CallAsync("archive_status");
        await CallAsync("list_series");
        await CallAsync("coverage", new() { ["root"] = Root, ["schema"] = Definition });

        texts.ShouldAllBe(t => !t.Contains(TestKey.Secret) && !t.Contains(TestKey.Access));
        texts.ShouldContain(t => t.Contains("archive.denied"));
    }

    [Fact]
    public async Task List_series_gives_each_series_days_no_data_days_and_span()
    {
        AWeekOfDefinitions();
        dbnf.Archive.Add(SampleArchive.Ohlcv, OtherRoot, Traded);
        await using var run = await McpRun.OnArchiveAsync(dbnf);

        var listed = await run.CallAsync("list_series", new() { ["root"] = Root });
        var series = listed.GetProperty("series").EnumerateArray().ToList();

        series.Count.ShouldBe(1);
        series[0].GetProperty("root").GetString().ShouldBe(Root);
        series[0].GetProperty("schema").GetString().ShouldBe(Definition);
        series[0].GetProperty("days").GetInt32().ShouldBe(1);
        series[0].GetProperty("no_data_days").GetInt32().ShouldBe(2);
        series[0].GetProperty("first").GetString().ShouldBe(Iso(Traded));
        series[0].GetProperty("last").GetString().ShouldBe(Iso(Traded));
        listed.GetProperty("total_bytes").GetInt64().ShouldBe(new FileInfo(dbnf.Archive.PathOf(DbnSyncLayout.Data(Dataset, Definition, Root, Traded))).Length);
    }

    [Fact]
    public async Task Coverage_tells_a_day_with_no_data_from_a_day_never_fetched()
    {
        AWeekOfDefinitions();
        await using var run = await McpRun.OnArchiveAsync(dbnf);

        var coverage = await run.CallAsync("coverage", new()
        {
            ["root"] = Root, ["schema"] = Definition, ["from"] = Iso(Thursday), ["to"] = Iso(Monday)
        });

        coverage.GetProperty("stored").GetInt32().ShouldBe(1);
        Dates(coverage.GetProperty("no_data")).ShouldBe([Iso(Saturday), Iso(Sunday)]);
        Dates(coverage.GetProperty("missing")).ShouldBe([Iso(Thursday), Iso(Monday)]);
        coverage.GetProperty("first_held").GetString().ShouldBe(Iso(Traded));
        coverage.GetProperty("last_held").GetString().ShouldBe(Iso(Sunday));
    }

    [Fact]
    public async Task Coverage_by_default_covers_what_the_series_holds()
    {
        AWeekOfDefinitions();
        await using var run = await McpRun.OnArchiveAsync(dbnf);

        var coverage = await run.CallAsync("coverage", new() { ["root"] = Root, ["schema"] = Definition });

        coverage.GetProperty("from").GetString().ShouldBe(Iso(Traded));
        coverage.GetProperty("to").GetString().ShouldBe(Iso(Sunday));
        Dates(coverage.GetProperty("missing")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Coverage_refuses_a_bad_schema_and_too_long_a_range()
    {
        AWeekOfDefinitions();
        await using var run = await McpRun.OnArchiveAsync(dbnf);

        (await run.RefusedAsync("coverage", new() { ["root"] = Root, ["schema"] = "tickles" })).ShouldStartWith("input.bad_schema");
        (await run.RefusedAsync("coverage", new() { ["root"] = Root, ["schema"] = Definition, ["from"] = "2020-01-01", ["to"] = "2026-01-01" }))
            .ShouldStartWith("input.bad_range");
    }

    [Fact]
    public async Task Coverage_of_a_series_the_archive_lacks_is_refused()
    {
        AWeekOfDefinitions();
        await using var run = await McpRun.OnArchiveAsync(dbnf);

        (await run.RefusedAsync("coverage", new() { ["root"] = OtherRoot, ["schema"] = Definition })).ShouldStartWith("archive.not_stored");
    }

    [Fact]
    public async Task Contracts_lists_outrights_and_spreads_only_when_asked()
    {
        AWeekOfDefinitions();
        await using var run = await McpRun.OnArchiveAsync(dbnf);

        var outrights = await run.CallAsync("contracts", new() { ["root"] = Root });
        var all = await run.CallAsync("contracts", new() { ["root"] = Root, ["spreads"] = true });

        var listed = outrights.GetProperty("contracts").EnumerateArray().ToList();

        listed.Select(c => c.GetProperty("symbol").GetString()).ShouldBe([Front, Next]);
        listed[0].GetProperty("raw_symbol").GetString().ShouldBe(FrontRaw);
        listed[0].GetProperty("tick_size").GetDecimal().ShouldBe(0.25m);
        listed[0].GetProperty("expires").GetString().ShouldNotBeNull().ShouldStartWith("2026-12-18");
        all.GetProperty("contracts").GetArrayLength().ShouldBe(3);
    }

    [Fact]
    public async Task Rolls_gives_the_date_the_contracts_settlements_and_gap()
    {
        var listing = new DateOnly(2026, 12, 1);
        var dayBefore = new DateOnly(2026, 12, 9);

        dbnf.Archive.Add(Definition, listing, FrontDefinition(), NextDefinition(), SpreadDefinition());
        dbnf.Archive.Add(Statistics, dayBefore,
            Stat(FrontId, At(dayBefore, 21), Settlement, Price(7820.25m)),
            Stat(NextId, At(dayBefore, 21), Settlement, Price(7900.5m)));

        await using var run = await McpRun.OnArchiveAsync(dbnf);

        var rolls = await run.CallAsync("rolls", new() { ["series"] = "MNQ.c.0", ["from"] = "2026-12-08", ["to"] = "2026-12-14" });
        var roll = rolls.GetProperty("rolls").EnumerateArray().Single();

        rolls.GetProperty("held_on_from").GetString().ShouldBe(Front);
        roll.GetProperty("trade_date").GetString().ShouldBe("2026-12-10");
        roll.GetProperty("from").GetString().ShouldBe(Front);
        roll.GetProperty("to").GetString().ShouldBe(Next);
        roll.GetProperty("from_settle").GetDecimal().ShouldBe(7820.25m);
        roll.GetProperty("to_settle").GetDecimal().ShouldBe(7900.5m);
        roll.GetProperty("gap").GetDecimal().ShouldBe(80.25m);
    }

    [Fact]
    public async Task Rolls_refuses_a_contract_that_is_no_series()
    {
        await using var run = await McpRun.OnArchiveAsync(dbnf);

        (await run.RefusedAsync("rolls", new() { ["series"] = Front, ["from"] = "2026-12-08" })).ShouldStartWith("input.bad_series");
    }

    [Fact]
    public async Task Trading_calendar_gives_sessions_early_closes_and_holidays_and_skips_weekends()
    {
        await using var run = await McpRun.OnArchiveAsync(dbnf);

        var calendar = await run.CallAsync("trading_calendar", new() { ["root"] = Es, ["from"] = "2026-12-23", ["to"] = "2026-12-28" });
        var days = calendar.GetProperty("days").EnumerateArray().ToList();

        days.Select(d => d.GetProperty("trade_date").GetString()).ShouldBe(["2026-12-23", "2026-12-24", "2026-12-25", "2026-12-28"]);

        days[0].GetProperty("open_time").GetString().ShouldBe($"2026-12-22T17:00:00{ChicagoWinter}");
        days[0].GetProperty("close_time").GetString().ShouldBe($"2026-12-23T16:00:00{ChicagoWinter}");
        days[0].GetProperty("rth_open").GetString().ShouldBe($"2026-12-23T08:30:00{ChicagoWinter}");
        days[0].GetProperty("rth_close").GetString().ShouldBe($"2026-12-23T15:00:00{ChicagoWinter}");
        days[0].GetProperty("early_close").GetBoolean().ShouldBeFalse();

        days[1].GetProperty("early_close").GetBoolean().ShouldBeTrue();
        days[1].GetProperty("close_time").GetString().ShouldBe($"2026-12-24T12:15:00{ChicagoWinter}");
        days[1].GetProperty("rth_close").GetString().ShouldBe($"2026-12-24T12:15:00{ChicagoWinter}");

        days[2].GetProperty("open").GetBoolean().ShouldBeFalse();
        days[2].GetProperty("notes")[0].GetProperty("kind").GetString().ShouldBe("closed");
        days[2].GetProperty("notes")[0].GetProperty("note").GetString().ShouldBe("Christmas");
    }

    [Fact]
    public async Task Trading_calendar_shows_a_halt_inside_a_session()
    {
        await using var run = await McpRun.OnArchiveAsync(dbnf);

        var calendar = await run.CallAsync("trading_calendar", new() { ["root"] = "MES", ["from"] = "2025-11-28" });
        var day = calendar.GetProperty("days")[0];
        var halt = day.GetProperty("halts").EnumerateArray().Single();

        halt.GetProperty("from").GetString().ShouldBe($"2025-11-27T20:44:27{ChicagoWinter}");
        halt.GetProperty("to").GetString().ShouldBe($"2025-11-28T07:30:00{ChicagoWinter}");
        day.GetProperty("close_time").GetString().ShouldBe($"2025-11-28T12:15:00{ChicagoWinter}");
    }

    [Fact]
    public async Task Trading_calendar_refuses_a_root_it_does_not_cover()
    {
        await using var run = await McpRun.OnArchiveAsync(dbnf);

        (await run.RefusedAsync("trading_calendar", new() { ["root"] = Cl, ["from"] = "2026-12-23" })).ShouldStartWith("calendar.not_covered");
    }

    private static Catalog TapeCatalog() => new(TapeFixture.Dataset, Es, [], [TapeFixture.FullDay(FullDate), TapeFixture.ShortDay(ShortDate)]);

    private static async Task<string> SaveTapeAsync(string tapeRoot)
    {
        var writer = new S1Writer(TapeFixture.Info());

        writer.Add(TapeFixture.OpenMicros, 30_842, 30_843, 30_841, 30_843, 5);

        var path = TapePaths.For(tapeRoot, TapeFixture.Dataset, TapeKind.S1, Es, TapeFixture.TradeDate);
        var saved = await writer.SaveAsync(path, CancellationToken.None);

        saved.IsSuccess.ShouldBeTrue(saved.Error?.Message);

        return path;
    }

    [Fact]
    public async Task Tape_catalog_reads_a_tapes_folder_day_by_day()
    {
        var folder = Path.Combine(dbnf.Home, "tapes");

        (await TapeCatalog().SaveAsync(folder, CancellationToken.None)).IsSuccess.ShouldBeTrue();
        var l1 = TapePaths.For(folder, TapeFixture.Dataset, TapeKind.L1, Es, FullDate);
        Directory.CreateDirectory(Path.GetDirectoryName(l1)!);
        await File.WriteAllBytesAsync(l1, [1, 2, 3]);

        await using var run = await McpRun.OnArchiveAsync(dbnf, $"--tapes \"{folder}\"");

        var catalog = await run.CallAsync("tape_catalog", new() { ["root"] = Es });
        var days = catalog.GetProperty("days").EnumerateArray().ToList();

        catalog.GetProperty("first").GetString().ShouldBe(Iso(FullDate));
        catalog.GetProperty("last").GetString().ShouldBe(Iso(ShortDate));
        days.Count.ShouldBe(2);
        days[0].GetProperty("full_day").GetBoolean().ShouldBeTrue();
        Dates(days[0].GetProperty("tapes")).ShouldBe(["l1"]);
        days[1].GetProperty("full_day").GetBoolean().ShouldBeFalse();
        days[1].GetProperty("excluded").GetString().ShouldNotBeNull().ShouldContain("not 16:00");
        Dates(days[1].GetProperty("tapes")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Tape_catalog_reads_the_archives_store_without_a_tapes_folder()
    {
        dbnf.Archive.Write(CatalogKey, await TapeFixture.JsonOfAsync(TapeCatalog()));
        dbnf.Archive.Write("GLBX.MDP3/s1/ES/2026/ES.s1.20261028.tape", [1, 2, 3]);

        await using var run = await McpRun.OnArchiveAsync(dbnf);

        var catalog = await run.CallAsync("tape_catalog", new() { ["root"] = Es, ["from"] = Iso(FullDate), ["to"] = Iso(FullDate) });
        var day = catalog.GetProperty("days").EnumerateArray().Single();

        catalog.GetProperty("source").GetString().ShouldBe(CatalogKey);
        Dates(day.GetProperty("tapes")).ShouldBe(["s1"]);
    }

    [Fact]
    public async Task Tape_catalog_with_none_to_read_says_where_tapes_live_and_builds_nothing()
    {
        AWeekOfDefinitions();
        await using var run = await McpRun.OnArchiveAsync(dbnf);

        var refused = await run.RefusedAsync("tape_catalog", new() { ["root"] = Es });

        refused.ShouldStartWith("tape.no_catalog");
        refused.ShouldContain("--tapes");
        File.Exists(dbnf.Archive.PathOf(CatalogKey)).ShouldBeFalse();
    }

    [Fact]
    public async Task Tape_info_reads_a_header_from_the_tapes_folder()
    {
        var folder = Path.Combine(dbnf.Home, "tapes");
        var path = await SaveTapeAsync(folder);

        await using var run = await McpRun.OnArchiveAsync(dbnf, $"--tapes \"{folder}\"");

        var info = await run.CallAsync("tape_info", new() { ["root"] = Es, ["date"] = Iso(TapeFixture.TradeDate), ["kind"] = "s1" });

        info.GetProperty("source").GetString().ShouldBe(path);
        info.GetProperty("symbol").GetString().ShouldBe(TapeFixture.Symbol);
        info.GetProperty("kind").GetString().ShouldBe("s1");
        info.GetProperty("events").GetInt64().ShouldBe(1);
        info.GetProperty("tick").GetDecimal().ShouldBe(0.25m);
        info.GetProperty("session_open").GetString().ShouldBe($"2026-10-28T17:00:00{ChicagoSummer}");
        info.GetProperty("session_close").GetString().ShouldBe($"2026-10-29T16:00:00{ChicagoSummer}");
        info.GetProperty("summary")[1].GetString().ShouldBe("session  10-28 17:00 to 10-29 16:00 Chicago");
    }

    [Fact]
    public async Task Tape_info_reads_a_header_from_the_store()
    {
        await SaveTapeAsync(dbnf.ArchiveFolder);
        await using var run = await McpRun.OnArchiveAsync(dbnf);

        var info = await run.CallAsync("tape_info", new() { ["root"] = Es, ["date"] = Iso(TapeFixture.TradeDate), ["kind"] = "S1" });

        info.GetProperty("source").GetString().ShouldBe("GLBX.MDP3/s1/ES/2026/ES.s1.20261029.tape");
        info.GetProperty("trade_date").GetString().ShouldBe(Iso(TapeFixture.TradeDate));
    }

    [Fact]
    public async Task Tape_info_refuses_an_unknown_kind_and_a_tape_that_is_not_there()
    {
        await using var run = await McpRun.OnArchiveAsync(dbnf);

        (await run.RefusedAsync("tape_info", new() { ["root"] = Es, ["date"] = "2026-10-29", ["kind"] = "order" })).ShouldStartWith("input.bad_tape_kind");
        (await run.RefusedAsync("tape_info", new() { ["root"] = Es, ["date"] = "2026-10-29", ["kind"] = "l1" })).ShouldStartWith("tape.not_found");
    }

    [Fact]
    public async Task Volume_by_day_sums_outrights_by_trade_date_and_names_the_leader()
    {
        // 18:00 Chicago on Thursday belongs to Friday's trade date; Friday 09:00 does too.
        var thursdayEvening = At(Thursday, 23);
        var fridayMorning = At(Traded, 14);

        dbnf.Archive.AddDefinitions(Thursday);
        dbnf.Archive.Add(SampleArchive.Ohlcv, Thursday,
            Bar(FrontId, At(Thursday, 14), 7800m, 7),
            Bar(FrontId, thursdayEvening, 7800m, 30),
            Bar(NextId, thursdayEvening, 7900m, 40));
        dbnf.Archive.Add(SampleArchive.Ohlcv, Traded,
            Bar(FrontId, fridayMorning, 7810m, 25),
            Bar(NextId, fridayMorning, 7910m, 5),
            Bar(SpreadId, fridayMorning, 100m, 1000));

        await using var run = await McpRun.OnArchiveAsync(dbnf);

        var volume = await run.CallAsync("volume_by_day", new() { ["root"] = Root, ["from"] = Iso(Thursday), ["to"] = Iso(Traded) });
        var days = volume.GetProperty("days").EnumerateArray().ToList();

        days.Select(d => d.GetProperty("trade_date").GetString()).ShouldBe([Iso(Thursday), Iso(Traded)]);
        days[0].GetProperty("volume").GetInt64().ShouldBe(7);
        days[0].GetProperty("leader").GetString().ShouldBe(Front);
        days[1].GetProperty("volume").GetInt64().ShouldBe(30 + 40 + 25 + 5);
        days[1].GetProperty("leader").GetString().ShouldBe(Front);
        days[1].GetProperty("leader_volume").GetInt64().ShouldBe(55);
        Dates(volume.GetProperty("missing_days")).ShouldBe([Iso(Thursday.AddDays(-1))]);
    }

    [Fact]
    public async Task Volume_by_day_refuses_more_than_92_days()
    {
        await using var run = await McpRun.OnArchiveAsync(dbnf);

        (await run.RefusedAsync("volume_by_day", new() { ["root"] = Root, ["from"] = "2026-01-01", ["to"] = "2026-06-30" }))
            .ShouldStartWith("input.bad_range");
    }
}
