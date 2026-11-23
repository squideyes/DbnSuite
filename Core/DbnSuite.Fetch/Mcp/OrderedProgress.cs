using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DbnSuite.Fetch.Mcp;

/// <summary>
/// Sends a tool's progress to the client in order, and finishes sending it before the tool returns.
/// The SDK's own <see cref="IProgress{T}"/> sends each notification without waiting, so one could
/// reach the client after the call's result, when the client has stopped listening for it.
/// </summary>
internal sealed class OrderedProgress : IProgress<ProgressNotificationValue>, IAsyncDisposable
{
    private readonly McpServer server;
    private readonly ProgressToken token;
    private readonly Lock gate = new();
    private Task sent = Task.CompletedTask;

    private OrderedProgress(McpServer server, ProgressToken token)
    {
        this.server = server;
        this.token = token;
    }

    /// <summary>A reporter for this call, or null when the client asked for no progress.</summary>
    public static OrderedProgress? For(RequestContext<CallToolRequestParams>? request) =>
        request?.Params?.ProgressToken is { } token ? new OrderedProgress(request.Server, token) : null;

    public void Report(ProgressNotificationValue value)
    {
        lock (gate)
            sent = SendAfterAsync(sent, value);
    }

    private async Task SendAfterAsync(Task before, ProgressNotificationValue value)
    {
        await before.ConfigureAwait(false);

        try
        {
            await server.NotifyProgressAsync(token, value).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Progress is a courtesy: a client that has gone away must not fail the work.
        }
    }

    /// <summary>Waits until every notification reported so far has been sent.</summary>
    public async ValueTask DisposeAsync()
    {
        Task last;

        lock (gate)
            last = sent;

        await last.ConfigureAwait(false);
    }
}
