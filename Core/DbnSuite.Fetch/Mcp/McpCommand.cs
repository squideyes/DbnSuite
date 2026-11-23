using System.CommandLine;
using System.Reflection;
using DbnSuite.Client;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace DbnSuite.Fetch.Mcp;

/// <summary>
/// <c>dbnf mcp</c>: dbnf as a Model Context Protocol server on standard input and output, for AI agents such as
/// Claude Code (<c>claude mcp add dbnf -- dbnf mcp</c>). It reads the archive the login, <c>--profile</c> or
/// <c>--archive</c> names, as every command does. Facts about the archive are always offered; market data only
/// with <c>--allow-data</c>, and writing files only with <c>--out</c>. Nothing it offers logs in, deletes or spends.
/// </summary>
public static class McpCommand
{
    public const long Gigabyte = 1_000_000_000;

    private const double DefaultGb = 2;

    private const string Instructions =
        "dbnf reads a DbnSync archive of Databento market data. Start with archive_status and list_series. "
        + "Dates are yyyy-MM-dd; a CME trade date's session opens at 17:00 Chicago time the evening before. "
        + "Facts tools never return market records. show_records and bars, when offered, return market data that goes to "
        + "the AI model's provider, which the user chose to allow. export, pull and tape_pull, when offered, write only inside "
        + "the server's --out folder and return paths and sizes; ask the user before passing confirm: true. "
        + "DbnSuite is an independent project with no official connection with Databento, Inc., which owns the registered "
        + "trademark Databento; use it only as your Databento agreements allow. Provided as is, without warranty.";

    public static Command Create(Session session)
    {
        var allowData = new Option<bool>("--allow-data")
        {
            Description = "Also offer show_records and bars, which return market data. A tool's result goes to the AI model's provider, "
                + "so turn this on only if your Databento agreement allows it."
        };

        var output = new Option<string?>("--out") { Description = "Also offer export, pull and tape_pull, which write files only inside this folder." };
        var maxGb = new Option<double>("--max-gb")
        {
            Description = "The most, in GB, that export, pull or tape_pull reads or copies unless the call confirms.",
            DefaultValueFactory = _ => DefaultGb
        };

        var tapes = new Option<string?>("--tapes") { Description = "A folder of tapes and catalogs, as dbnf tape --tapes names it, for tape_catalog and tape_info." };

        var command = new Command("mcp", "Serve the archive to AI agents over the Model Context Protocol, on standard input and output.")
        {
            allowData, output, maxGb, tapes
        };

        command.SetAction(async (parse, cancel) =>
        {
            var gb = parse.GetValue(maxGb);

            // At most a petabyte, which no archive comes near, so the limit in bytes always fits.
            if (gb is not (> 0 and <= 1_000_000))
                return await session.FailAsync(new Error("input.bad_max_gb", "--max-gb is a size in GB: more than 0, and at most 1000000.")).ConfigureAwait(false);

            var outFolder = parse.GetValue(output) is { } folder ? Path.GetFullPath(folder) : null;
            var tapeFolder = parse.GetValue(tapes) is { } t ? Path.GetFullPath(t) : null;

            if (tapeFolder is not null && !Directory.Exists(tapeFolder))
                return await session.FailAsync(new Error("cli.no_folder", $"There is no folder {tapeFolder}.")).ConfigureAwait(false);

            if (outFolder is not null)
            {
                try
                {
                    Directory.CreateDirectory(outFolder);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    return await session.FailAsync(new Error(Session.LocalFile, $"Could not make the --out folder {outFolder}: {e.Message}")).ConfigureAwait(false);
                }
            }

            using var host = new ToolHost(session, parse)
            {
                AllowData = parse.GetValue(allowData),
                Out = outFolder,
                MaxBytes = (long)(gb * Gigabyte),
                Tapes = tapeFolder
            };

            try
            {
                await ServeAsync(host, session.OpenInput(), session.OpenOutput(), cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                // Stopped by Ctrl+C, or by the client closing the pipe; either way the session is over.
            }

            return Session.Ok;
        });

        return command;
    }

    /// <summary>Serves the tools <paramref name="host"/> allows over a pair of streams until the client goes or <paramref name="cancel"/> fires.</summary>
    internal static async Task ServeAsync(ToolHost host, Stream input, Stream output, CancellationToken cancel)
    {
        var json = McpJson.Default.Options;
        var services = new ServiceCollection();

        var server = services
            .AddMcpServer(o =>
            {
                o.ServerInfo = new() { Name = "dbnf", Version = Version };
                o.ServerInstructions = Instructions;
            })
            .WithStreamServerTransport(input, output)
            .WithTools(new FactTools(host), json);

        if (host.AllowData)
            server.WithTools(new DataTools(host), json);

        if (host.Out is not null)
            server.WithTools(new FileTools(host), json);

        await using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<McpServer>().RunAsync(cancel).ConfigureAwait(false);
    }

    /// <summary>This build's version, without the commit id the build appends.</summary>
    private static string Version
    {
        get
        {
            var version = typeof(McpCommand).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
            var plus = version.IndexOf('+', StringComparison.Ordinal);

            return plus < 0 ? version : version[..plus];
        }
    }
}
