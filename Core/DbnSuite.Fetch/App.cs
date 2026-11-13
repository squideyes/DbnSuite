using System.CommandLine;
using System.CommandLine.Invocation;
using DbnSuite.Fetch.Commands;

namespace DbnSuite.Fetch;

/// <summary>The dbnf command line: every command, sharing one <see cref="Session"/>.</summary>
public static class App
{
    public static RootCommand Create(Session session) => new(
        "DbnFetch (dbnf): download raw and processed market data from a DbnSync archive.\n" +
        "An independent project with no official connection with Databento, Inc., which owns the\n" +
        "registered trademark Databento. Use it only as your Databento agreements allow.\n" +
        "Provided as is, without warranty: use at your own risk.")
    {
        session.Profile,
        session.Archive,
        session.Cache,
        LoginCommand.Create(session),
        LogoutCommand.Create(session),
        DoctorCommand.Create(session),
        ListCommand.Create(session),
        CoverageCommand.Create(session),
        ContractsCommand.Create(session),
        ShowCommand.Create(session),
        PullCommand.Create(session),
        ExportCommand.Create(session),
        TapeCommand.Create(session),
        RollsCommand.Create(session),
        CacheCommand.Create(session)
    };

    /// <summary>
    /// Runs one command line. Its exit code is <see cref="Session.Usage"/> for a
    /// line that does not parse, which System.CommandLine alone would call 1;
    /// in dbnf, 1 means the work failed.
    /// </summary>
    public static async Task<int> RunAsync(Session session, IReadOnlyList<string> args,
        InvocationConfiguration? configuration = null, CancellationToken cancel = default)
    {
        var parse = Create(session).Parse(args);
        var exit = await parse.InvokeAsync(configuration, cancel).ConfigureAwait(false);

        return parse.Action is ParseErrorAction ? Session.Usage : exit;
    }
}
