using System.CommandLine;
using DbnClient;
using DbnClient.Archive;

namespace DbnFetch;

/// <summary>The options every command shares, and how a command reaches the archive.</summary>
public sealed class Session
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int Usage = 2;
    public const int Cancelled = 130;

    public Option<string> Profile { get; } = new("--profile")
    {
        Description = "Which saved login to use.",
        DefaultValueFactory = _ => Profiles.DefaultName,
        Recursive = true
    };

    public Option<string?> Archive { get; } = new("--archive")
    {
        Description = "Read this archive instead of the login's: a folder, such as one dbnf pull --out filled.",
        Recursive = true
    };

    public Option<string> Cache { get; } = new("--cache")
    {
        Description = "Where raw files are kept once downloaded.",
        DefaultValueFactory = _ => Profiles.DefaultCache,
        Recursive = true
    };

    /// <summary>The saved logins; the config folder is only made once something asks for them.</summary>
    public Profiles Profiles
    {
        get => field ??= new(Profiles.DefaultFolder);
        init;
    }

    public TextWriter Out { get; init; } = Console.Out;

    public TextWriter Error { get; init; } = Console.Error;

    /// <summary>Standard input, which only a piped secret reads.</summary>
    public TextReader In
    {
        get => field ?? Console.In;
        init;
    }

    /// <summary>The next key pressed, unseen; null when input is piped and nobody can be asked.</summary>
    public Func<ConsoleKeyInfo>? Keyboard { get; init; } =
        Console.IsInputRedirected ? null : () => Console.ReadKey(intercept: true);

    /// <summary>The archive the command line points at: <c>--archive</c>, else the login.</summary>
    public Result<DbnArchive> Open(ParseResult parse)
    {
        var profile = parse.GetValue(Profile)!;
        var cache = Path.Combine(parse.GetValue(Cache)!, profile);

        if (parse.GetValue(Archive) is { } folder)
            return DbnArchive.Open(new ArchiveOptions { Location = Path.GetFullPath(folder) }, cache);

        var resolved = Profiles.Resolve(profile);

        if (!resolved.IsSuccess)
            return resolved.Error!;

        return resolved.Value is { } options
            ? DbnArchive.Open(options, cache)
            : new Error("cli.no_login", profile == Profiles.DefaultName
                ? "Not logged in. Run dbnf login first."
                : $"No login named {profile}. Run dbnf login --profile {profile} first.");
    }

    /// <summary>Runs <paramref name="work"/> against the archive, turning expected failures into a message and an exit code.</summary>
    public async Task<int> RunAsync(ParseResult parse, Func<DbnArchive, Task<int>> work, CancellationToken cancel)
    {
        var archive = Open(parse);

        if (!archive.IsSuccess)
            return await FailAsync(archive.Error!).ConfigureAwait(false);

        using var opened = archive.Value;

        try
        {
            return await work(opened).ConfigureAwait(false);
        }
        catch (ArchiveException e)
        {
            return await FailAsync(e.Error).ConfigureAwait(false);
        }
        catch (IOException e)
        {
            // A connection dropped part way through a file; the next run carries on from there.
            return await FailAsync(new Error(Errors.Unreachable, $"The transfer stopped: {e.Message}")).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            await Error.WriteLineAsync("Stopped.").ConfigureAwait(false);

            return Cancelled;
        }
    }

    /// <summary>A schema name as DBN spells it, or an error naming the ones there are.</summary>
    public static Result<string> Schema(string text)
    {
        var name = text.Trim().ToLowerInvariant();

        return DbnClient.Dbn.DbnMetadata.Schemas.Contains(name)
            ? name
            : new Error("input.bad_schema", $"\"{text}\" is not a schema. Schemas are {string.Join(", ", DbnClient.Dbn.DbnMetadata.Schemas)}.");
    }

    public async Task<int> FailAsync(Error error)
    {
        await Error.WriteLineAsync(error.Message).ConfigureAwait(false);

        return error.Code.StartsWith("input.", StringComparison.Ordinal) || error.Code.StartsWith("cli.", StringComparison.Ordinal)
            ? Usage
            : Failed;
    }

    public async Task WriteAsync(IEnumerable<string> lines)
    {
        foreach (var line in lines)
            await Out.WriteLineAsync(line).ConfigureAwait(false);
    }
}
