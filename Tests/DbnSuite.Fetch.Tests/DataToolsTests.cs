using Shouldly;
using static DbnSuite.Tests.Fetch.SampleArchive;
using static DbnSuite.Testing.TestData;

namespace DbnSuite.Tests.Fetch;

[Collection(DbnfCollection.Name)]
public sealed class DataToolsTests : IAsyncLifetime
{
    private const string Date = "2026-10-30";
    private const string AllowData = "--allow-data";
    private const string FrontSeries = "MNQ.n.0";

    private static readonly DateOnly Traded = Day(10, 30);

    /// <summary>07:00 Chicago: inside the Globex session, before regular hours.</summary>
    private static readonly ulong Early = At(Traded, 12);

    /// <summary>09:00 Chicago: inside regular hours.</summary>
    private static readonly ulong Morning = At(Traded, 14);

    private readonly Dbnf dbnf = new();
    private McpRun run = null!;

    public async ValueTask InitializeAsync()
    {
        dbnf.Archive.AddDefinitions(Traded);
        dbnf.Archive.AddEmpty(SampleArchive.Ohlcv, Traded.AddDays(-1));
        dbnf.Archive.Add(SampleArchive.Ohlcv, Traded,
            Bar(FrontId, Early, 7800m, 7810m, 7790m, 7805m, 5),
            Bar(FrontId, Morning, 7820.25m),
            Bar(FrontId, Morning + 1_000_000_000, 7821m),
            Bar(NextId, Morning, 7900m));
        dbnf.Archive.Add(Mbo, Root, Traded, new Dictionary<string, uint> { [FrontSeries] = FrontId },
            Order(FrontId, Morning, 7820m, 1, 'A', 'B', 1),
            Order(FrontId, Morning, 7820.25m, 2, 'A', 'A', 2),
            Order(NextId, Morning, 7900m, 1, 'A', 'B', 3));

        run = await McpRun.OnArchiveAsync(dbnf, AllowData);
    }

    public async ValueTask DisposeAsync()
    {
        await run.DisposeAsync();
        dbnf.Dispose();
    }

    [Fact]
    public async Task Show_records_gives_a_days_records_a_line_each_with_the_counts()
    {
        var shown = await run.CallAsync("show_records", new() { ["schema"] = Mbo, ["symbol"] = Front, ["day"] = Date, ["limit"] = 1 });

        shown.GetProperty("heading").GetString().ShouldBe("GLBX.MDP3 mbo, DBN v3, 2026-10-30 UTC, requested as MNQ.FUT, times in America/Chicago");
        shown.GetProperty("records").GetArrayLength().ShouldBe(1);
        shown.GetProperty("records")[0].GetString().ShouldNotBeNull().ShouldContain(Front);
        shown.GetProperty("shown").GetInt64().ShouldBe(1);
        shown.GetProperty("matched").GetInt64().ShouldBe(2);
        shown.GetProperty("total").GetInt64().ShouldBe(3);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public async Task Show_records_refuses_a_limit_outside_1_to_200(int limit)
    {
        var refused = await run.RefusedAsync("show_records", new() { ["schema"] = Mbo, ["symbol"] = Root, ["day"] = Date, ["limit"] = limit });

        refused.ShouldStartWith("input.bad_limit");
    }

    [Fact]
    public async Task Show_records_passes_on_why_a_day_has_nothing()
    {
        var refused = await run.RefusedAsync("show_records", new() { ["schema"] = SampleArchive.Ohlcv, ["symbol"] = Root, ["day"] = "2026-10-29" });

        refused.ShouldStartWith("archive.no_data");
    }

    [Fact]
    public async Task Bars_are_built_as_export_builds_them()
    {
        var bars = await run.CallAsync("bars", new() { ["symbol"] = Front, ["date"] = Date, ["bars"] = "1h" });
        var rows = bars.GetProperty("rows").EnumerateArray().ToList();

        bars.GetProperty("total").GetInt32().ShouldBe(2);
        bars.GetProperty("truncated").GetBoolean().ShouldBeFalse();
        rows.Count.ShouldBe(2);
        rows[0].GetProperty("symbol").GetString().ShouldBe(Front);
        rows[0].GetProperty("open").GetDecimal().ShouldBe(7800m);
        rows[0].GetProperty("high").GetDecimal().ShouldBe(7810m);
        rows[0].GetProperty("low").GetDecimal().ShouldBe(7790m);
        rows[0].GetProperty("close").GetDecimal().ShouldBe(7805m);
        rows[0].GetProperty("volume").GetInt64().ShouldBe(5);
        rows[1].GetProperty("open").GetDecimal().ShouldBe(7820.25m);
        rows[1].GetProperty("close").GetDecimal().ShouldBe(7821m);
        rows[1].GetProperty("volume").GetInt64().ShouldBe(20);
    }

    [Fact]
    public async Task Bars_stop_at_the_limit_and_say_how_many_there_are()
    {
        var bars = await run.CallAsync("bars", new() { ["symbol"] = Front, ["date"] = Date, ["bars"] = "1m", ["limit"] = 1 });

        bars.GetProperty("rows").GetArrayLength().ShouldBe(1);
        bars.GetProperty("total").GetInt32().ShouldBe(2);
        bars.GetProperty("truncated").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Bars_of_regular_hours_leave_out_the_early_morning()
    {
        var bars = await run.CallAsync("bars", new() { ["symbol"] = Front, ["date"] = Date, ["bars"] = "1h", ["session"] = "rth" });

        bars.GetProperty("total").GetInt32().ShouldBe(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(500)]
    public async Task Bars_refuse_a_limit_outside_1_to_200(int limit)
    {
        (await run.RefusedAsync("bars", new() { ["symbol"] = Front, ["date"] = Date, ["limit"] = limit })).ShouldStartWith("input.bad_limit");
    }

    [Fact]
    public async Task Bars_refuse_a_bar_size_they_do_not_know()
    {
        (await run.RefusedAsync("bars", new() { ["symbol"] = Front, ["date"] = Date, ["bars"] = "7q" })).ShouldStartWith("input.");
    }

    [Fact]
    public async Task Bars_leave_no_scratch_files_behind()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "dbnf-mcp");

        await run.CallAsync("bars", new() { ["symbol"] = Front, ["date"] = Date });

        (Directory.Exists(scratch) ? Directory.EnumerateFileSystemEntries(scratch) : []).ShouldBeEmpty();
    }
}
