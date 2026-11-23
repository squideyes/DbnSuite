using System.CommandLine;
using DbnSuite.Client;
using DbnSuite.Client.Archive;
using ModelContextProtocol;

namespace DbnSuite.Fetch.Mcp;

/// <summary>What <c>dbnf mcp</c> was started with, and the archive its tools share.</summary>
internal sealed class ToolHost(Session session, ParseResult parse) : IDisposable
{
    /// <summary>A size, a limit or a date that the server refuses, whatever the tool.</summary>
    public const string TooLarge = "mcp.too_large";

    /// <summary>A path a caller gave that would leave the <c>--out</c> folder.</summary>
    public const string OutsideOut = "mcp.outside_out";

    private const string Hidden = "[hidden]";

    /// <summary>A key this short is no key; hiding it would only garble messages.</summary>
    private const int ShortestKey = 8;

    private readonly Lock gate = new();
    private ArchiveOptions? options;
    private DbnArchive? archive;

    public Session Session { get; } = session;

    public ParseResult Parse { get; } = parse;

    /// <summary>True when started with <c>--allow-data</c>, so market data may go back to the model.</summary>
    public bool AllowData { get; init; }

    /// <summary>The <c>--out</c> folder, as a full path, which every file a tool writes must be inside; null when not given.</summary>
    public string? Out { get; init; }

    /// <summary>The most a tool reads or copies without the call confirming, from <c>--max-gb</c>.</summary>
    public long MaxBytes { get; init; }

    /// <summary>The <c>--tapes</c> folder, as a full path; null when not given.</summary>
    public string? Tapes { get; init; }

    public string Profile => Parse.GetValue(Session.Profile)!;

    public string CacheRoot => Session.CacheOf(Parse);

    /// <summary>The archive every tool reads, opened the first time one asks. A login that fails is tried again next time.</summary>
    public DbnArchive Archive()
    {
        lock (gate)
        {
            if (archive is not null)
                return archive;

            var resolved = Session.Options(Parse);

            if (!resolved.IsSuccess)
                throw Refuse(resolved.Error!);

            options = resolved.Value;
            archive = DbnArchive.Open(options, CacheRoot);

            return archive;
        }
    }

    /// <summary>Notes a login resolved outside <see cref="Archive"/>, so its keys are hidden from messages too.</summary>
    public void Remember(ArchiveOptions resolved)
    {
        lock (gate)
            options ??= resolved;
    }

    /// <summary>
    /// Runs <paramref name="work"/> against the archive. An expected failure, returned or thrown, becomes
    /// an <see cref="McpException"/>, whose message the client sees with no key in it.
    /// </summary>
    public async Task<T> RunAsync<T>(Func<DbnArchive, Task<Result<T>>> work)
    {
        var opened = Archive();
        Result<T> result;

        try
        {
            result = await work(opened).ConfigureAwait(false);
        }
        catch (Exception e) when (Session.ErrorOf(e) is { } error)
        {
            throw Refuse(error);
        }

        return result.IsSuccess ? result.Value : throw Refuse(result.Error!);
    }

    /// <summary>An expected failure as the client sees it: its code, then its message, with any key hidden.</summary>
    public McpException Refuse(Error error) => new($"{error.Code}: {Hide(error.Message)}");

    /// <summary>Text with the login's access key and secret key taken out, in case a message from a store repeats them.</summary>
    public string Hide(string text)
    {
        ArchiveOptions? known;

        lock (gate)
            known = options;

        foreach (var key in new[] { known?.AccessKey, known?.SecretKey })
        {
            if (key is { Length: >= ShortestKey })
                text = text.Replace(key, Hidden, StringComparison.Ordinal);
        }

        return text;
    }

    /// <summary>
    /// A folder inside <see cref="Out"/>, from a path relative to it. A rooted path, or one with a
    /// <c>..</c> in it, is refused before anything is resolved, and so is anything that resolves outside.
    /// </summary>
    public Result<string> Inside(string? relative)
    {
        if (Out is not { } root)
            return new Error(OutsideOut, "This server was started without --out, so it writes no files.");

        if (string.IsNullOrWhiteSpace(relative) || relative.Trim() == ".")
            return root;

        var parts = relative.Split('/', '\\');

        if (Path.IsPathRooted(relative) || relative.Contains(':', StringComparison.Ordinal) || parts.Any(p => p.Trim() == ".."))
            return new Error(OutsideOut, $"\"{relative}\" is not a folder inside {root}: give a relative path with no \"..\".");

        var full = Path.GetFullPath(Path.Combine(root, relative));
        var inside = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;

        return full == root || full.StartsWith(inside, StringComparison.Ordinal)
            ? full
            : new Error(OutsideOut, $"\"{relative}\" is not a folder inside {root}.");
    }

    /// <summary>
    /// Null when <paramref name="bytes"/> is within <see cref="MaxBytes"/> or the call confirmed;
    /// otherwise the refusal, which says how to go ahead.
    /// </summary>
    public Error? Limit(long bytes, bool confirm, string what) =>
        confirm || bytes <= MaxBytes
            ? null
            : new Error(TooLarge, Text.Invariant(
                $"This {what} comes to {Text.Bytes(bytes)}, more than the {Text.Bytes(MaxBytes)} this server allows without confirming. Ask the user, then call again with confirm: true to go ahead."));

    public void Dispose()
    {
        lock (gate)
            archive?.Dispose();
    }
}
