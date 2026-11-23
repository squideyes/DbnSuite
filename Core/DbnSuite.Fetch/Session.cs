using System.CommandLine;
using DbnSuite.Client;
using DbnSuite.Client.Archive;

namespace DbnSuite.Fetch;

/// <summary>The options every command shares, and how a command reaches the archive.</summary>
public sealed class Session
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int Usage = 2;
    public const int Cancelled = 130;

    /// <summary>A file or folder on this computer could not be read or written.</summary>
    public const string LocalFile = "local.refused";

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
        get => field ??= new(Profiles.DefaultFolder, SecretVaults.ForThisMachine(Profiles.DefaultFolder));
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

    /// <summary>Standard input as bytes, which <c>dbnf mcp</c> reads the protocol from.</summary>
    public Func<Stream> OpenInput { get; init; } = Console.OpenStandardInput;

    /// <summary>Standard output as bytes, which belongs to the protocol while <c>dbnf mcp</c> serves.</summary>
    public Func<Stream> OpenOutput { get; init; } = Console.OpenStandardOutput;

    /// <summary>The next key pressed, unseen; null when input is piped and nobody can be asked.</summary>
    public Func<ConsoleKeyInfo>? Keyboard { get; init; } =
        Console.IsInputRedirected ? null : () => Console.ReadKey(intercept: true);

    /// <summary>The archive the command line points at: <c>--archive</c>, else the login.</summary>
    public Result<DbnArchive> Open(ParseResult parse)
    {
        var options = Options(parse);

        return options.IsSuccess ? DbnArchive.Open(options.Value, CacheOf(parse)) : options.Error!;
    }

    /// <summary>Where this login's raw files are cached: a folder of its own under <c>--cache</c>.</summary>
    public string CacheOf(ParseResult parse) => Path.Combine(parse.GetValue(Cache)!, parse.GetValue(Profile)!);

    /// <summary>Where the archive the command line points at is, and the key that reads it, ready to open.</summary>
    public Result<ArchiveOptions> Options(ParseResult parse)
    {
        var profile = parse.GetValue(Profile)!;

        if (parse.GetValue(Archive) is { } folder)
        {
            return Directory.Exists(folder)
                ? new ArchiveOptions { Location = Path.GetFullPath(folder) }
                : new Error("cli.no_folder", $"There is no folder {Path.GetFullPath(folder)}.");
        }

        var resolved = Profiles.Resolve(profile);

        if (!resolved.IsSuccess)
            return resolved.Error!;

        if (resolved.Value is not { } options)
        {
            return new Error("cli.no_login", profile == Profiles.DefaultName
                ? "Not logged in. Run dbnf login first."
                : $"No login named {profile}. Run dbnf login --profile {profile} first.");
        }

        return Usable(options, profile) is { } problem ? problem : options;
    }

    /// <summary>What stops a login being opened, or null: a folder that is gone, or a key with no secret to sign with.</summary>
    internal static Error? Usable(ArchiveOptions options, string profile)
    {
        var again = profile == Profiles.DefaultName ? "dbnf login" : $"dbnf login --profile {profile}";

        if (options.IsFolder)
        {
            return Directory.Exists(options.Location)
                ? null
                : new Error("cli.no_folder", $"The login's folder, {options.Location}, is not there. Check the drive, or run {again} again.");
        }

        if (options.AccessKey.Length == 0)
            return new Error("cli.no_key", $"The login has no access key. Run {again} again, or set DBNF_ACCESS_KEY.");

        return options.SecretKey.Length == 0
            ? new Error("cli.no_secret", $"The login's secret key cannot be read on this computer. Run {again} again, or set DBNF_SECRET_KEY.")
            : null;
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
        catch (Exception e) when (ErrorOf(e) is { } error)
        {
            return await FailAsync(error).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            await Error.WriteLineAsync("Stopped.").ConfigureAwait(false);

            return Cancelled;
        }
    }

    /// <summary>The expected failure an exception from reading the archive stands for, or null when it is a bug.</summary>
    internal static Error? ErrorOf(Exception exception) => exception switch
    {
        ArchiveException e => e.Error,
        InvalidDataException e => new Error(Errors.Corrupt, $"A file is damaged: {e.Message}"),

        // A connection dropped part way through a file; the next run carries on from there.
        IOException e => new Error(Errors.Unreachable, $"The transfer stopped: {e.Message}"),
        UnauthorizedAccessException e => new Error(LocalFile, $"A file or folder on this computer refused: {e.Message}"),
        Amazon.S3.AmazonS3Exception e => new Error(Errors.Unreachable,
            $"The archive answered with an error ({(int)e.StatusCode} {e.ErrorCode}): {e.Message}"),
        _ => null
    };

    /// <summary>A schema name as DBN spells it, or an error naming the ones there are.</summary>
    public static Result<string> Schema(string text)
    {
        var name = text.Trim().ToLowerInvariant();

        return DbnSuite.Client.Dbn.DbnMetadata.Schemas.Contains(name)
            ? name
            : new Error("input.bad_schema", $"\"{text}\" is not a schema. Schemas are {string.Join(", ", DbnSuite.Client.Dbn.DbnMetadata.Schemas)}.");
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
