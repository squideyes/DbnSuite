using DbnSuite.Fetch;
using Shouldly;

namespace DbnSuite.UnitTests.FetchTests;

[Collection(DbnfCollection.Name)]
public sealed class ProgramTests
{
    private const string HelpOption = "--help";
    private const string Disclaimer = "no official connection with Databento";

    [Fact]
    public void The_program_runs_its_command_line_and_returns_the_exit_code()
    {
        var entry = typeof(App).Assembly.EntryPoint!;
        var console = Console.Out;
        using var captured = new StringWriter();

        Console.SetOut(captured);

        try
        {
            var exit = entry.Invoke(null, [new[] { HelpOption }]);

            exit.ShouldBe(Session.Ok);
            captured.ToString().ShouldContain(Disclaimer);
        }
        finally
        {
            Console.SetOut(console);
        }
    }

    [Fact]
    public void The_program_exits_with_the_usage_code_on_a_line_that_does_not_parse()
    {
        var entry = typeof(App).Assembly.EntryPoint!;
        var console = Console.Error;
        using var captured = new StringWriter();

        Console.SetError(captured);

        try
        {
            entry.Invoke(null, [new[] { UnknownCommand }]).ShouldBe(Session.Usage);
        }
        finally
        {
            Console.SetError(console);
        }
    }

    private const string UnknownCommand = "launch";
}
