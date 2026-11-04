using System.CommandLine;
using System.CommandLine.Parsing;
using DbnFetch;

namespace DbnSync.UnitTests.FetchTests;

/// <summary>
/// Tests that run dbnf share the process's DBNF_ variables and console, so
/// they run one at a time, after everything that runs in parallel.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DbnfCollection
{
    public const string Name = "dbnf";
}

/// <summary>What one run of dbnf left behind.</summary>
internal sealed record DbnfRun(int ExitCode, string Out, string Error)
{
    public IReadOnlyList<string> Lines => Out.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
}

/// <summary>
/// dbnf as a user runs it, a line at a time, in a home of its own: its logins,
/// its cache, an archive folder and an output folder all sit in one temp
/// folder, and no DBNF_ variable from this machine reaches it.
/// </summary>
internal sealed class Dbnf : IDisposable
{
    public const int ParseFailure = DbnFetch.Session.Usage;

    private static readonly string[] Variables = ["DBNF_LOCATION", "DBNF_BUCKET", "DBNF_ACCESS_KEY", "DBNF_SECRET_KEY"];

    private readonly Dictionary<string, string?> saved = Variables.ToDictionary(v => v, Environment.GetEnvironmentVariable);

    public Dbnf()
    {
        foreach (var variable in Variables)
            Environment.SetEnvironmentVariable(variable, null);

        Directory.CreateDirectory(ArchiveFolder);

        Profiles = new Profiles(ConfigFolder);
        Archive = new SampleArchive(ArchiveFolder);
    }

    public string Home { get; } = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public string ConfigFolder => Path.Combine(Home, "config");

    public string ArchiveFolder => Path.Combine(Home, "archive");

    public string Output => Path.Combine(Home, "out");

    public string CacheFolder => Path.Combine(Home, "cache");

    /// <summary>The <c>--out</c> option for <see cref="Output"/>.</summary>
    public string OutOption => $"--out \"{Output}\"";

    public Profiles Profiles { get; }

    public SampleArchive Archive { get; }

    /// <summary>What a pipe into dbnf holds.</summary>
    public TextReader Stdin { get; set; } = TextReader.Null;

    /// <summary>The keys a user at a terminal presses; none when input is piped.</summary>
    public Func<ConsoleKeyInfo>? Keyboard { get; set; }

    /// <summary>Runs a command line against the saved login, if any.</summary>
    public async Task<DbnfRun> RunAsync(string line, CancellationToken cancel = default)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        // pull writes from several downloads at once, as Console.Out allows.
        var outWriter = TextWriter.Synchronized(output);
        var errorWriter = TextWriter.Synchronized(error);

        var session = new Session
        {
            Profiles = Profiles,
            Out = outWriter,
            Error = errorWriter,
            In = Stdin,
            Keyboard = Keyboard
        };

        string[] args = [.. CommandLineParser.SplitCommandLine(line), "--cache", CacheFolder];

        var exit = await App.RunAsync(session, args,
            new InvocationConfiguration { Output = outWriter, Error = errorWriter, ProcessTerminationTimeout = null },
            cancel);

        return new DbnfRun(exit, output.ToString(), error.ToString());
    }

    /// <summary>Runs a command line against the archive folder, as <c>--archive</c> does.</summary>
    public Task<DbnfRun> OnArchiveAsync(string line, CancellationToken cancel = default) =>
        RunAsync($"{line} --archive \"{ArchiveFolder}\"", cancel);

    /// <summary>Logs in to an S3 endpoint with a made-up key, and fails the test if that fails.</summary>
    public async Task LogInAsync(FakeS3 s3, string options = "")
    {
        Stdin = new StringReader(TestKey.Secret);

        var run = await RunAsync($"login --location {s3.Location} --access-key {TestKey.Access} --secret-stdin {options}");

        if (run.ExitCode != DbnFetch.Session.Ok)
            throw new InvalidOperationException($"Login failed: {run.Error}");
    }

    /// <summary>The files under <see cref="Output"/>, relative to it with forward slashes, without the cache's own index.</summary>
    public IReadOnlyList<string> Written() =>
        Directory.Exists(Output)
            ? [.. Directory.EnumerateFiles(Output, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(Output, f).Replace('\\', '/'))
                .Where(f => !f.StartsWith('.'))
                .Order(StringComparer.Ordinal)]
            : [];

    public string[] ReadOutput(string name) => File.ReadAllLines(Path.Combine(Output, name));

    public void Dispose()
    {
        foreach (var (variable, value) in saved)
            Environment.SetEnvironmentVariable(variable, value);

        if (Directory.Exists(Home))
            Directory.Delete(Home, recursive: true);
    }
}

/// <summary>A made-up key: nothing but <see cref="FakeS3"/> ever sees it.</summary>
internal static class TestKey
{
    public const string Access = "dbn-reader";
    public const string Secret = "a-forty-character-secret-for-the-tests-1";
}
