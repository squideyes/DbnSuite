using System.CommandLine;
using DbnSuite.Client.Archive;

namespace DbnSuite.Fetch.Commands;

/// <summary><c>dbnf doctor</c>: checks the login, the archive, this PC's clock and the cache, one line each.</summary>
public static class DoctorCommand
{
    /// <summary>S3 refuses requests signed more than 15 minutes off; warn well before that.</summary>
    private static readonly TimeSpan ClockTolerance = TimeSpan.FromMinutes(5);

    /// <summary>Less free space than this on the cache's drive fails the check: a month of mbo for one root needs about as much.</summary>
    private const long LeastFree = 10_000_000_000;

    public static Command Create(Session session)
    {
        var command = new Command("doctor", "Check the login, the archive, this computer's clock and the cache.");

        command.SetAction(async (parse, cancel) =>
        {
            var name = parse.GetValue(session.Profile)!;
            var resolved = Resolve(session, parse);

            if (!resolved.IsSuccess)
            {
                await Report(session, false, "Login", resolved.Error!.Message).ConfigureAwait(false);

                return Session.Failed;
            }

            var options = resolved.Value;

            if (options is null)
                return await Report(session, false, "Login", $"none saved as {name}; run dbnf login") ? Session.Ok : Session.Failed;

            var checkup = await CheckAsync(options, session.CacheOf(parse), cancel).ConfigureAwait(false);
            var healthy = await Report(session, true, "Archive", Describe(options)).ConfigureAwait(false);

            if (!options.IsFolder)
            {
                healthy &= await Report(session, checkup.KeyUsable, "Key", checkup.KeyUsable
                    ? options.AccessKey
                    : options.AccessKey.Length == 0
                        ? "no access key is saved; run dbnf login"
                        : $"{options.AccessKey}, but no secret is saved; run dbnf login").ConfigureAwait(false);

                healthy &= await Report(session, checkup.ClockOk, "Clock", checkup.ClockSkew is { } s
                    ? Text.Invariant($"{Math.Abs(s.TotalSeconds):0} s {(s > TimeSpan.Zero ? "ahead of" : "behind")} the archive's")
                    : "the archive sent no time to compare with").ConfigureAwait(false);
            }

            // Without a key and its secret the archive cannot be opened at all, so the checks that need it are left out.
            if (checkup.Layout is not { } layout)
                return Session.Failed;

            healthy &= await Report(session, layout.IsSuccess, "Reading", layout.IsSuccess
                ? $"works; the files are in the {layout.Value} layout"
                : layout.Error!.Message).ConfigureAwait(false);

            if (checkup.Cache is { } cache)
            {
                healthy &= await Report(session, cache.Ok, "Cache", cache.Free is { } bytes
                    ? $"{cache.Root}: {Text.Bytes(cache.Used)} used, {Text.Bytes(bytes)} free on the drive"
                    : $"{cache.Root}: {Text.Bytes(cache.Used)} used; the free space could not be read").ConfigureAwait(false);
            }

            return healthy ? Session.Ok : Session.Failed;
        });

        return command;
    }

    /// <summary>The login the command line names, or the <c>--archive</c> folder; null when there is no login of that name.</summary>
    internal static DbnSuite.Client.Result<ArchiveOptions?> Resolve(Session session, ParseResult parse) =>
        parse.GetValue(session.Archive) is { } folder
            ? new ArchiveOptions { Location = Path.GetFullPath(folder) }
            : session.Profiles.Resolve(parse.GetValue(session.Profile)!);

    /// <summary>Where the archive is, without its key: a folder, or an endpoint, its bucket and prefix.</summary>
    internal static string Describe(ArchiveOptions options) => options.IsFolder
        ? options.Location
        : $"{options.Location}, bucket {options.Bucket}{(options.Prefix.Length > 0 ? $"/{options.Prefix}" : "")}";

    /// <summary>
    /// Checks a login: its key, the clock against the archive's, whether the archive reads, and the cache.
    /// What needs a key and its secret is left unchecked without them.
    /// </summary>
    internal static async Task<Checkup> CheckAsync(ArchiveOptions options, string cacheRoot, CancellationToken cancel)
    {
        var usable = options.IsFolder || options.AccessKey.Length > 0 && options.SecretKey.Length > 0;
        var skew = options.IsFolder ? null : await ClockSkewAsync(options.Location, cancel).ConfigureAwait(false);

        if (!usable)
            return new Checkup(usable, skew, null, null);

        using var archive = DbnArchive.Open(options, cacheRoot);

        var layout = await archive.LayoutAsync(cancel).ConfigureAwait(false);
        CacheCheck? cache = null;

        if (archive.Cache is { } local)
        {
            var free = FreeSpace(local.Root);

            cache = new CacheCheck(local.Root, local.Files().Sum(f => f.Size), free, free is null or > LeastFree);
        }

        return new Checkup(usable, skew, layout, cache);
    }

    /// <summary>
    /// The free space where a folder is, or null when it cannot be read, as for a network share.
    /// Windows asks by drive; elsewhere the folder itself names its mount.
    /// </summary>
    internal static long? FreeSpace(string folder)
    {
        try
        {
            return new DriveInfo(OperatingSystem.IsWindows() ? Path.GetPathRoot(folder)! : folder).AvailableFreeSpace;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task<bool> Report(Session session, bool ok, string what, string detail)
    {
        await session.Out.WriteLineAsync($"{(ok ? "ok  " : "FAIL")}  {what,-8} {detail}").ConfigureAwait(false);

        return ok;
    }

    /// <summary>What <see cref="CheckAsync"/> found.</summary>
    /// <param name="ClockSkew">How far this computer's clock is ahead of the archive's; null for a folder, or when the archive sent no time.</param>
    /// <param name="Layout">The archive's layout, or why it could not be read; null when the login cannot open it at all.</param>
    /// <param name="Cache">Null for a folder archive, which needs no cache.</param>
    internal sealed record Checkup(bool KeyUsable, TimeSpan? ClockSkew, DbnSuite.Client.Result<string>? Layout, CacheCheck? Cache)
    {
        public bool ClockOk => ClockSkew is null || ClockSkew.Value.Duration() < ClockTolerance;
    }

    /// <param name="Free">The free space on the cache's drive, or null when it cannot be read.</param>
    internal sealed record CacheCheck(string Root, long Used, long? Free, bool Ok);

    /// <summary>How far this PC's clock is from the server's, from the Date header of any reply.</summary>
    private static async Task<TimeSpan?> ClockSkewAsync(string location, CancellationToken cancel)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, location), cancel).ConfigureAwait(false);

            return response.Headers.Date is { } date ? DateTimeOffset.UtcNow - date : null;
        }
        catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException && !cancel.IsCancellationRequested)
        {
            return null;
        }
    }
}
