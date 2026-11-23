using System.Collections.Concurrent;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.IO.Pipelines;
using System.Text.Json;
using DbnSuite.Fetch;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;

namespace DbnSuite.Tests.Fetch;

/// <summary>
/// <c>dbnf mcp</c> run in this process as a user starts it, with an MCP client talking to it over
/// in-memory pipes, as Claude Code talks to it over standard input and output.
/// </summary>
internal sealed class McpRun : IAsyncDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly Pipe toServer = new();
    private readonly Pipe toClient = new();
    private readonly CancellationTokenSource stop = new();
    private Task<int> exit = Task.FromResult(0);
    private McpClient? client;

    /// <summary>What dbnf wrote to its text output, which must stay empty: standard output belongs to the protocol.</summary>
    public StringWriter Out { get; } = new();

    public StringWriter Error { get; } = new();

    /// <summary>
    /// Every progress notification the client has received. The SDK stops listening for a call's
    /// progress once its result arrives, and can hand a notification on after that, so a test
    /// reads progress here, where nothing is dropped.
    /// </summary>
    public ConcurrentQueue<ProgressNotificationValue> Progress { get; } = new();

    public McpClient Client => client ?? throw new InvalidOperationException("The server has not started.");

    /// <summary>Starts <c>dbnf mcp</c> with <paramref name="options"/>, in <paramref name="dbnf"/>'s home, and connects to it.</summary>
    public static async Task<McpRun> StartAsync(Dbnf dbnf, string options = "")
    {
        var run = new McpRun();

        var session = new Session
        {
            Profiles = dbnf.Profiles,
            Out = TextWriter.Synchronized(run.Out),
            Error = TextWriter.Synchronized(run.Error),
            In = TextReader.Null,
            Keyboard = null,
            OpenInput = () => run.toServer.Reader.AsStream(),
            OpenOutput = () => run.toClient.Writer.AsStream()
        };

        string[] args = [.. CommandLineParser.SplitCommandLine($"mcp {options}"), "--cache", dbnf.CacheFolder];

        run.exit = Task.Run(() => App.RunAsync(session, args,
            new InvocationConfiguration { Output = session.Out, Error = session.Error, ProcessTerminationTimeout = null }, run.stop.Token));

        run.client = await McpClient.CreateAsync(
            new StreamClientTransport(run.toServer.Writer.AsStream(), run.toClient.Reader.AsStream()),
            cancellationToken: new CancellationTokenSource(Patience).Token);

        run.client.RegisterNotificationHandler(NotificationMethods.ProgressNotification, (notification, _) =>
        {
            if (notification.Params.Deserialize<ProgressNotificationParams>(McpJsonUtilities.DefaultOptions) is { } sent)
                run.Progress.Enqueue(sent.Progress);

            return ValueTask.CompletedTask;
        });

        return run;
    }

    /// <summary>Starts <c>dbnf mcp</c> on the sample archive folder, as <c>--archive</c> does.</summary>
    public static Task<McpRun> OnArchiveAsync(Dbnf dbnf, string options = "") =>
        StartAsync(dbnf, $"{options} --archive \"{dbnf.ArchiveFolder}\"");

    public async Task<IReadOnlyList<McpClientTool>> ToolsAsync() => [.. await Client.ListToolsAsync()];

    public async Task<IReadOnlyList<string>> ToolNamesAsync() => [.. (await ToolsAsync()).Select(t => t.Name).Order(StringComparer.Ordinal)];

    /// <summary>Calls a tool that should succeed, and returns its structured result.</summary>
    public async Task<JsonElement> CallAsync(string tool, Dictionary<string, object?>? arguments = null,
        IProgress<ProgressNotificationValue>? progress = null)
    {
        var result = await RawAsync(tool, arguments, progress);

        result.IsError.ShouldNotBe(true, TextOf(result));

        return result.StructuredContent!.Value;
    }

    /// <summary>Calls a tool that should refuse, and returns what it said.</summary>
    public async Task<string> RefusedAsync(string tool, Dictionary<string, object?>? arguments = null)
    {
        var result = await RawAsync(tool, arguments, null);

        result.IsError.ShouldBe(true, TextOf(result));

        // The SDK puts "An error occurred invoking 'tool': " before what the tool said, which starts with its code.
        var text = TextOf(result);
        var said = text.IndexOf("': ", StringComparison.Ordinal);

        return said < 0 ? text : text[(said + 3)..];
    }

    /// <summary>Calls a tool and returns the whole result, as a client receives it.</summary>
    public Task<CallToolResult> RawAsync(string tool, Dictionary<string, object?>? arguments = null,
        IProgress<ProgressNotificationValue>? progress = null) =>
        Client.CallToolAsync(tool, arguments ?? [], progress, cancellationToken: new CancellationTokenSource(Patience).Token).AsTask();

    public static string TextOf(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    /// <summary>The client goes away, as Claude Code does at the end of a session; dbnf then exits on its own.</summary>
    public async Task<int> StopAsync()
    {
        if (client is not null)
            await client.DisposeAsync();

        client = null;

        // The client leaves its end of the pipe open; closing it is what a closed standard input looks like.
        await toServer.Writer.CompleteAsync();

        return await exit.WaitAsync(Patience);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync();
        }
        finally
        {
            await stop.CancelAsync();
            stop.Dispose();
            Out.Dispose();
            Error.Dispose();
        }
    }
}
