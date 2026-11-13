using DbnSuite.Fetch;
using Shouldly;
using static DbnSuite.UnitTests.TestData;

namespace DbnSuite.UnitTests.FetchTests;

[Collection(DbnfCollection.Name)]
public sealed class AppTests : IDisposable
{
    private const string Disclaimer = "no official connection with Databento";
    private const string UnknownCommand = "launch";

    private readonly Dbnf dbnf = new();

    public void Dispose() => dbnf.Dispose();

    [Theory]
    [InlineData("login")]
    [InlineData("logout")]
    [InlineData("doctor")]
    [InlineData("ls")]
    [InlineData("coverage")]
    [InlineData("contracts")]
    [InlineData("show")]
    [InlineData("pull")]
    [InlineData("export")]
    [InlineData("rolls")]
    [InlineData("cache")]
    public async Task Help_lists_every_command(string command)
    {
        var run = await dbnf.RunAsync("--help");

        run.ExitCode.ShouldBe(Session.Ok);
        run.Out.ShouldContain($"  {command}");
    }

    [Fact]
    public async Task Help_says_the_tool_is_independent()
    {
        (await dbnf.RunAsync("--help")).Out.ShouldContain(Disclaimer);
    }

    [Theory]
    [InlineData("--profile")]
    [InlineData("--archive")]
    [InlineData("--cache")]
    public async Task The_shared_options_are_on_every_command(string option)
    {
        (await dbnf.RunAsync("ls --help")).Out.ShouldContain(option);
    }

    [Fact]
    public async Task An_unknown_command_is_refused()
    {
        var run = await dbnf.RunAsync(UnknownCommand);

        run.ExitCode.ShouldBe(Dbnf.ParseFailure);
        run.Error.ShouldContain(UnknownCommand);
    }

    [Fact]
    public async Task A_shared_option_may_come_before_the_command()
    {
        dbnf.Archive.AddDefinitions(Day(10, 30));

        var run = await dbnf.RunAsync($"--archive \"{dbnf.ArchiveFolder}\" ls");

        run.ExitCode.ShouldBe(Session.Ok);
        run.Out.ShouldContain(Root);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ls --no-such-option")]
    [InlineData("show mbo")]
    [InlineData("show mbo MNQ someday")]
    [InlineData("pull mbo MNQ --from someday")]
    public async Task Every_line_that_does_not_parse_exits_with_the_usage_code(string line)
    {
        var run = await dbnf.RunAsync(line);

        run.ExitCode.ShouldBe(Session.Usage);
        run.Error.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Asking_for_help_is_no_mistake()
    {
        (await dbnf.RunAsync("export --help")).ExitCode.ShouldBe(Session.Ok);
    }

    [Fact]
    public async Task Work_that_fails_exits_with_the_failure_code_not_the_usage_one()
    {
        dbnf.Archive.Write(DbnSuite.Client.Archive.DbnSyncLayout.Data(Dataset, Mbo, Root, Day(10, 30)), [1, 2, 3]);

        var run = await dbnf.OnArchiveAsync($"show {Mbo} {Root} 2026-10-30");

        run.ExitCode.ShouldBe(Session.Failed);
    }
}
