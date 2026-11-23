using DbnSuite.Fetch;
using Shouldly;
using static DbnSuite.UnitTests.TestData;

namespace DbnSuite.UnitTests.FetchTests;

[Collection(DbnfCollection.Name)]
public sealed class McpCommandTests : IDisposable
{
    private const string AllowData = "--allow-data";
    private const string ProviderWarning = "AI model's provider";

    private static readonly string[] Facts =
    [
        "archive_status", "contracts", "coverage", "list_series", "rolls", "tape_catalog", "tape_info", "trading_calendar", "volume_by_day"
    ];

    private static readonly string[] Data = ["bars", "show_records"];

    private static readonly string[] Writers = ["export", "pull", "tape_pull"];

    private readonly Dbnf dbnf = new();

    public McpCommandTests() => dbnf.Archive.AddDefinitions(Day(10, 30));

    public void Dispose() => dbnf.Dispose();

    private static string[] Sorted(params string[][] groups) => [.. groups.SelectMany(g => g).Order(StringComparer.Ordinal)];

    [Fact]
    public async Task Without_flags_it_offers_only_facts_about_the_archive()
    {
        await using var run = await McpRun.OnArchiveAsync(dbnf);

        (await run.ToolNamesAsync()).ShouldBe(Facts);
    }

    [Fact]
    public async Task Allow_data_adds_the_tools_that_return_market_data()
    {
        await using var run = await McpRun.OnArchiveAsync(dbnf, AllowData);

        (await run.ToolNamesAsync()).ShouldBe(Sorted(Facts, Data));
    }

    [Fact]
    public async Task Out_adds_the_tools_that_write_files_and_nothing_else()
    {
        await using var run = await McpRun.OnArchiveAsync(dbnf, dbnf.OutOption);

        (await run.ToolNamesAsync()).ShouldBe(Sorted(Facts, Writers));
    }

    [Fact]
    public async Task Both_flags_offer_every_tool()
    {
        await using var run = await McpRun.OnArchiveAsync(dbnf, $"{AllowData} {dbnf.OutOption}");

        (await run.ToolNamesAsync()).ShouldBe(Sorted(Facts, Data, Writers));
    }

    [Fact]
    public async Task Facts_are_marked_read_only_and_writers_are_not()
    {
        await using var run = await McpRun.OnArchiveAsync(dbnf, dbnf.OutOption);

        var tools = await run.ToolsAsync();

        tools.Where(t => Facts.Contains(t.Name)).ShouldAllBe(t => t.ProtocolTool.Annotations!.ReadOnlyHint == true);
        tools.Where(t => Writers.Contains(t.Name)).ShouldAllBe(t => t.ProtocolTool.Annotations!.ReadOnlyHint != true);
    }

    [Fact]
    public async Task The_data_tools_say_their_results_go_to_the_models_provider()
    {
        await using var run = await McpRun.OnArchiveAsync(dbnf, AllowData);

        var tools = await run.ToolsAsync();

        tools.Where(t => Data.Contains(t.Name)).ShouldAllBe(t => t.Description.Contains(ProviderWarning));
    }

    [Fact]
    public async Task It_names_itself_and_says_how_to_start()
    {
        await using var run = await McpRun.OnArchiveAsync(dbnf);

        run.Client.ServerInfo.ShouldNotBeNull().Name.ShouldBe("dbnf");
        run.Client.ServerInstructions.ShouldNotBeNull().ShouldContain("archive_status");
    }

    [Fact]
    public async Task Standard_output_carries_only_the_protocol_and_it_exits_cleanly_when_the_client_goes()
    {
        var run = await McpRun.OnArchiveAsync(dbnf, AllowData);

        await using (run)
        {
            await run.CallAsync("list_series");
            await run.RefusedAsync("coverage", new() { ["root"] = Root, ["schema"] = "tickles" });

            (await run.StopAsync()).ShouldBe(Session.Ok);
            run.Out.ToString().ShouldBeEmpty();
            run.Error.ToString().ShouldBeEmpty();
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("2000000")]
    public async Task A_size_limit_that_is_no_size_is_bad_usage(string gb)
    {
        var run = await dbnf.OnArchiveAsync($"mcp --max-gb {gb}");

        run.ExitCode.ShouldBe(Session.Usage);
        run.Error.ShouldContain("--max-gb");
    }

    [Fact]
    public async Task A_tapes_folder_that_is_not_there_is_bad_usage()
    {
        var run = await dbnf.OnArchiveAsync($"mcp --tapes \"{Path.Combine(dbnf.Home, "nowhere")}\"");

        run.ExitCode.ShouldBe(Session.Usage);
        run.Error.ShouldContain("There is no folder");
    }

    [Fact]
    public async Task The_out_folder_is_made_when_it_is_not_there()
    {
        await using var run = await McpRun.OnArchiveAsync(dbnf, dbnf.OutOption);

        Directory.Exists(dbnf.Output).ShouldBeTrue();
    }
}
