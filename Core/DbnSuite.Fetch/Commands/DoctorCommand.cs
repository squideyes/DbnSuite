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
            var resolved = parse.GetValue(session.Archive) is { } folder
                ? new ArchiveOptions { Location = Path.GetFullPath(folder) }
                : session.Profiles.Resolve(name);

            if (!resolved.IsSuccess)
            {
                await Report(session, false, "Login", resolved.Error!.Message).ConfigureAwait(false);

                return Session.Failed;
            }

            var options = resolved.Value;

            if (options is null)
                return await Report(session, false, "Login", $"none saved as {name}; run dbnf login") ? Session.Ok : Session.Failed;

            var healthy = true;
            var usable = true;

            healthy &= await Report(session, true, "Archive", options.IsFolder
                ? options.Location
                : $"{options.Location}, bucket {options.Bucket}{(options.Prefix.Length > 0 ? $"/{options.Prefix}" : "")}").ConfigureAwait(false);

            if (!options.IsFolder)
            {
                usable = options.AccessKey.Length > 0 && options.SecretKey.Length > 0;

                healthy &= await Report(session, usable, "Key", usable
                    ? options.AccessKey
                    : options.AccessKey.Length == 0
                        ? "no access key is saved; run dbnf login"
                        : $"{options.AccessKey}, but no secret is saved; run dbnf login").ConfigureAwait(false);

                var skew = await ClockSkewAsync(options.Location, cancel).ConfigureAwait(false);

                healthy &= await Report(session, skew is null || skew.Value.Duration() < ClockTolerance, "Clock", skew is { } s
                    ? Text.Invariant($"{Math.Abs(s.TotalSeconds):0} s {(s > TimeSpan.Zero ? "ahead of" : "behind")} the archive's")
                    : "the archive sent no time to compare with").ConfigureAwait(false);
            }

            // Without a key and its secret the archive cannot be opened at all, so the checks that need it are left out.
            if (!usable)
                return Session.Failed;

            using var archive = DbnArchive.Open(options, Path.Combine(parse.GetValue(session.Cache)!, name));

            var layout = await archive.LayoutAsync(cancel).ConfigureAwait(false);

            healthy &= await Report(session, layout.IsSuccess, "Reading", layout.IsSuccess
                ? $"works; the files are in the {layout.Value} layout"
                : layout.Error!.Message).ConfigureAwait(false);

            if (archive.Cache is { } cache)
            {
                var size = cache.Files().Sum(f => f.Size);
                var free = FreeSpace(cache.Root);

                healthy &= await Report(session, free is null or > LeastFree, "Cache", free is { } bytes
                    ? $"{cache.Root}: {Text.Bytes(size)} used, {Text.Bytes(bytes)} free on the drive"
                    : $"{cache.Root}: {Text.Bytes(size)} used; the free space could not be read").ConfigureAwait(false);
            }

            return healthy ? Session.Ok : Session.Failed;
        });

        return command;
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
