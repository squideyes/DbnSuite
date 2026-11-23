using DbnSuite.Client.Archive;
using DbnSuite.Client.Tape;
using ModelContextProtocol;
using Shouldly;
using static DbnSuite.Tests.Fetch.SampleArchive;
using static DbnSuite.Testing.TestData;

namespace DbnSuite.Tests.Fetch;

[Collection(DbnfCollection.Name)]
public sealed class FileToolsTests : IDisposable
{
    private const string Date = "2026-10-30";
    private const string Csv = "MNQZ26.ohlcv-1s.20261030.csv";
    private const string Es = "ES";
    private const string CatalogKey = "GLBX.MDP3/catalog/ES.catalog.json";
    private const string FullTape = "GLBX.MDP3/s1/ES/2026/ES.s1.20261028.tape";
    private const string ShortTape = "GLBX.MDP3/s1/ES/2026/ES.s1.20261029.tape";
    private const string StaleTape = "GLBX.MDP3/l1/ES/2026/ES.l1.20261029.tape";

    /// <summary>A limit of one byte, so anything at all is over it.</summary>
    private const string Tiny = "--max-gb 0.000000001";

    private static readonly DateOnly Traded = Day(10, 30);
    private static readonly DateOnly NoData = Day(10, 31);

    private readonly Dbnf dbnf = new();

    public FileToolsTests()
    {
        dbnf.Archive.AddDefinitions(Traded);
        dbnf.Archive.AddEmpty(Definition, NoData);
        dbnf.Archive.AddEmpty(SampleArchive.Ohlcv, Traded.AddDays(-1));
        dbnf.Archive.Add(SampleArchive.Ohlcv, Traded,
            Bar(FrontId, At(Traded, 12), 7800m),
            Bar(FrontId, At(Traded, 14), 7820m),
            Bar(FrontId, At(Traded, 15), 7830m),
            Bar(NextId, At(Traded, 14), 7900m));
    }

    public void Dispose() => dbnf.Dispose();

    private Task<McpRun> StartAsync(string options = "") => McpRun.OnArchiveAsync(dbnf, $"{dbnf.OutOption} {options}");

    private long SizeOf(string key) => new FileInfo(dbnf.Archive.PathOf(key)).Length;

    private static Dictionary<string, object?> ExportOf(string? folder = null)
    {
        var arguments = new Dictionary<string, object?> { ["schema"] = SampleArchive.Ohlcv, ["symbol"] = Front, ["from"] = Date };

        if (folder is not null)
            arguments["folder"] = folder;

        return arguments;
    }

    [Fact]
    public async Task Export_writes_inside_the_out_folder_and_returns_paths_rows_and_sizes_not_data()
    {
        await using var run = await StartAsync();

        var exported = await run.CallAsync("export", ExportOf());
        var path = Path.Combine(dbnf.Output, Csv);
        var file = exported.GetProperty("files").EnumerateArray().Single();

        dbnf.Written().ShouldBe([Csv]);
        exported.GetProperty("rows").GetInt64().ShouldBe(3);
        file.GetProperty("path").GetString().ShouldBe(path);
        file.GetProperty("bytes").GetInt64().ShouldBe(new FileInfo(path).Length);
        exported.GetProperty("source_files").GetInt32().ShouldBe(1);
        exported.GetProperty("source_bytes").GetInt64().ShouldBe(SizeOf(DbnSyncLayout.Data(Dataset, SampleArchive.Ohlcv, Root, Traded)));
        exported.GetRawText().ShouldNotContain("7820");
    }

    [Fact]
    public async Task Export_writes_into_a_folder_inside_out()
    {
        await using var run = await StartAsync();

        await run.CallAsync("export", ExportOf("bars/mnq"));

        dbnf.Written().ShouldBe([$"bars/mnq/{Csv}"]);
    }

    [Theory]
    [InlineData("../escaped")]
    [InlineData("inside/../../escaped")]
    [InlineData("..")]
    public async Task Export_refuses_a_folder_that_climbs_out(string folder)
    {
        await using var run = await StartAsync();

        (await run.RefusedAsync("export", ExportOf(folder))).ShouldStartWith("mcp.outside_out");

        Directory.Exists(Path.Combine(dbnf.Home, "escaped")).ShouldBeFalse();
        dbnf.Written().ShouldBeEmpty();
    }

    [Fact]
    public async Task Export_refuses_a_rooted_folder()
    {
        await using var run = await StartAsync();

        (await run.RefusedAsync("export", ExportOf(Path.Combine(dbnf.Home, "escaped")))).ShouldStartWith("mcp.outside_out");

        Directory.Exists(Path.Combine(dbnf.Home, "escaped")).ShouldBeFalse();
    }

    [Fact]
    public async Task Export_over_the_limit_is_refused_until_the_call_confirms()
    {
        await using var run = await StartAsync(Tiny);

        var refused = await run.RefusedAsync("export", ExportOf());

        refused.ShouldStartWith("mcp.too_large");
        refused.ShouldContain("confirm: true");
        dbnf.Written().ShouldBeEmpty();

        var confirmed = ExportOf();
        confirmed["confirm"] = true;

        await run.CallAsync("export", confirmed);

        dbnf.Written().ShouldBe([Csv]);
    }

    [Fact]
    public async Task Export_dry_run_weighs_it_up_and_writes_nothing()
    {
        await using var run = await StartAsync(Tiny);

        var arguments = ExportOf();
        arguments["dry_run"] = true;

        var weighed = await run.CallAsync("export", arguments);

        weighed.GetProperty("dry_run").GetBoolean().ShouldBeTrue();
        weighed.GetProperty("source_files").GetInt32().ShouldBe(1);
        weighed.TryGetProperty("rows", out _).ShouldBeFalse();
        dbnf.Written().ShouldBeEmpty();
    }

    [Fact]
    public async Task Export_passes_on_a_bad_choice_as_bad_input()
    {
        await using var run = await StartAsync();

        var arguments = ExportOf();
        arguments["format"] = "xlsx";

        (await run.RefusedAsync("export", arguments)).ShouldStartWith("input.bad_format");
    }

    [Fact]
    public async Task Pull_copies_raw_files_in_the_archives_layout_and_skips_them_next_time()
    {
        await using var run = await StartAsync();

        var arguments = new Dictionary<string, object?> { ["schemas"] = Definition, ["roots"] = Root, ["from"] = Date, ["to"] = "2026-10-31" };
        var key = DbnSyncLayout.Data(Dataset, Definition, Root, Traded);

        var pulled = await run.CallAsync("pull", arguments);
        var again = await run.CallAsync("pull", arguments);

        dbnf.Written().ShouldBe([key]);
        pulled.GetProperty("downloaded").GetInt32().ShouldBe(1);
        pulled.GetProperty("no_data_days").GetInt32().ShouldBe(1);
        pulled.GetProperty("bytes").GetInt64().ShouldBe(SizeOf(key));
        pulled.GetProperty("files")[0].GetString().ShouldBe(Path.Combine(dbnf.Output, key.Replace('/', Path.DirectorySeparatorChar)));
        again.GetProperty("downloaded").GetInt32().ShouldBe(0);
        again.GetProperty("already_here").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Pull_writes_markers_when_asked()
    {
        await using var run = await StartAsync();

        await run.CallAsync("pull", new() { ["schemas"] = Definition, ["roots"] = Root, ["from"] = Date, ["to"] = "2026-10-31", ["markers"] = true });

        dbnf.Written().ShouldBe([DbnSyncLayout.Data(Dataset, Definition, Root, Traded), DbnSyncLayout.Empty(Dataset, Definition, Root, NoData)], ignoreOrder: true);
    }

    [Fact]
    public async Task Pull_over_the_limit_is_refused_and_a_dry_run_weighs_it()
    {
        await using var run = await StartAsync(Tiny);

        var arguments = new Dictionary<string, object?> { ["schemas"] = Definition, ["roots"] = Root, ["from"] = Date };

        (await run.RefusedAsync("pull", arguments)).ShouldStartWith("mcp.too_large");

        arguments["dry_run"] = true;

        var weighed = await run.CallAsync("pull", arguments);

        weighed.GetProperty("wanted").GetInt32().ShouldBe(1);
        weighed.GetProperty("bytes").GetInt64().ShouldBe(SizeOf(DbnSyncLayout.Data(Dataset, Definition, Root, Traded)));
        dbnf.Written().ShouldBeEmpty();
    }

    [Fact]
    public async Task Pull_reports_progress_a_file_at_a_time()
    {
        dbnf.Archive.AddDefinitions(Day(10, 29));
        await using var run = await StartAsync();

        await run.CallAsync("pull", new() { ["schemas"] = Definition, ["roots"] = Root, ["from"] = "2026-10-29", ["to"] = Date },
            new Progress<ProgressNotificationValue>());

        await WaitAsync(() => run.Progress.Count == 2);

        run.Progress.Select(s => s.Progress).Order().ShouldBe([1f, 2f]);
        run.Progress.ShouldAllBe(s => s.Total == 2);
    }

    [Fact]
    public async Task Pull_refuses_a_folder_that_climbs_out()
    {
        await using var run = await StartAsync();

        (await run.RefusedAsync("pull", new() { ["schemas"] = Definition, ["roots"] = Root, ["from"] = Date, ["folder"] = "../x" }))
            .ShouldStartWith("mcp.outside_out");
    }

    private async Task TapeStoreAsync()
    {
        var catalog = new Catalog(TapeFixture.Dataset, Es, [], [TapeFixture.FullDay(Day(10, 28)), TapeFixture.ShortDay(Day(10, 29))]);

        dbnf.Archive.Write(CatalogKey, await TapeFixture.JsonOfAsync(catalog));
        dbnf.Archive.Write(FullTape, [1, 2, 3]);
        dbnf.Archive.Write(ShortTape, [1, 2, 3]);
    }

    [Fact]
    public async Task Tape_pull_copies_the_catalog_and_full_days_and_never_removes_a_tape()
    {
        await TapeStoreAsync();

        var stale = Path.Combine(dbnf.Output, StaleTape.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(stale)!);
        await File.WriteAllBytesAsync(stale, [1, 2, 3], TestContext.Current.CancellationToken);

        await using var run = await StartAsync();

        var pulled = await run.CallAsync("tape_pull", new() { ["root"] = Es });

        pulled.GetProperty("catalog_copied").GetBoolean().ShouldBeTrue();
        pulled.GetProperty("copied").GetInt32().ShouldBe(1);
        pulled.GetProperty("bytes").GetInt64().ShouldBe(3);
        dbnf.Written().ShouldBe([CatalogKey, FullTape, StaleTape], ignoreOrder: true);
    }

    [Fact]
    public async Task Tape_pull_takes_only_the_kinds_asked_for()
    {
        await TapeStoreAsync();
        await using var run = await StartAsync();

        var pulled = await run.CallAsync("tape_pull", new() { ["root"] = Es, ["kinds"] = new[] { "l1" } });

        pulled.GetProperty("copied").GetInt32().ShouldBe(0);
        dbnf.Written().ShouldBe([CatalogKey]);
    }

    [Fact]
    public async Task Tape_pull_over_the_limit_is_refused_after_only_the_catalog()
    {
        await TapeStoreAsync();
        await using var run = await StartAsync(Tiny);

        (await run.RefusedAsync("tape_pull", new() { ["root"] = Es })).ShouldStartWith("mcp.too_large");

        dbnf.Written().ShouldBe([CatalogKey]);
    }

    [Fact]
    public async Task Tape_pull_refuses_an_unknown_kind()
    {
        await TapeStoreAsync();
        await using var run = await StartAsync();

        (await run.RefusedAsync("tape_pull", new() { ["root"] = Es, ["kinds"] = new[] { "orders" } })).ShouldStartWith("input.bad_tape_kind");
    }

    /// <summary>
    /// Waits for notifications, which the client hands on by themselves and can after the call's
    /// result. A notification that never comes still fails the test, after 30 seconds.
    /// </summary>
    private static async Task WaitAsync(Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(30);

        while (!done() && DateTime.UtcNow < until)
            await Task.Delay(20);
    }
}
