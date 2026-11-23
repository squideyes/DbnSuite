using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DbnSuite.Fetch.Mcp;

/// <summary>
/// A tool's progress, sent to the client in the order reported, and waited for before the tool returns. The SDK's
/// own <see cref="IProgress{T}"/> sends each notification without waiting for it, so a tool's result could reach
/// the client first, and a client drops progress for a call that has already finished.
/// </summary>
internal sealed class ToolProgress : IProgress<ProgressNotificationValue>
{
    private readonly McpServer server;
    private readonly ProgressToken token;
    private readonly Lock gate = new();
    private Task sent = Task.CompletedTask;

    private ToolProgress(McpServer server, ProgressToken token)
    {
        this.server = server;
        this.token = token;
    }

    /// <summary>Progress for a call, or null when the client didn't ask for any.</summary>
    public static ToolProgress? Of(RequestContext<CallToolRequestParams>? call) =>
        call?.Params?.ProgressToken is { } token ? new ToolProgress(call.Server, token) : null;

    /// <summary>Waits until everything reported so far has been sent; call it before the tool returns.</summary>
    public static Task SentAsync(ToolProgress? progress)
    {
        if (progress is null)
            return Task.CompletedTask;

        lock (progress.gate)
            return progress.sent;
    }

    public void Report(ProgressNotificationValue value)
    {
        lock (gate)
            sent = SendAfterAsync(sent, value);
    }

    private async Task SendAfterAsync(Task before, ProgressNotificationValue value)
    {
        await before.ConfigureAwait(false);
        await server.NotifyProgressAsync(token, value).ConfigureAwait(false);
    }
}
