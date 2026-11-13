using System.CommandLine;
using System.Text;
using DbnSuite.Client;
using DbnSuite.Client.Archive;

namespace DbnSuite.Fetch.Commands;

/// <summary><c>dbnf login</c>: saves where the archive is and the key that reads it, after testing them.</summary>
public static class LoginCommand
{
    public static Command Create(Session session)
    {
        var location = new Option<string>("--location")
        {
            Description = "The archive's S3 endpoint, such as https://s3.example.com, or a folder.",
            Required = true
        };

        var accessKey = new Option<string>("--access-key") { Description = "Your access key, such as dbn-reader." };
        var bucket = new Option<string>("--bucket") { Description = "The bucket.", DefaultValueFactory = _ => "dbn" };
        var prefix = new Option<string>("--prefix") { Description = "A folder in the bucket holding the archive.", DefaultValueFactory = _ => "" };
        var secretStdin = new Option<bool>("--secret-stdin") { Description = "Read the secret key from standard input instead of asking." };

        var command = new Command("login", "Save the archive's address and your key, after checking they work.")
        {
            location, accessKey, bucket, prefix, secretStdin
        };

        command.SetAction(async (parse, cancel) =>
        {
            var profile = new Profile
            {
                Location = parse.GetValue(location)!,
                AccessKey = parse.GetValue(accessKey) ?? "",
                Bucket = parse.GetValue(bucket)!,
                Prefix = parse.GetValue(prefix)!
            };

            var isFolder = new ArchiveOptions { Location = profile.Location }.IsFolder;

            if (isFolder && !Directory.Exists(profile.Location))
            {
                return await session.FailAsync(new Error("input.no_folder",
                    $"There is no folder {Path.GetFullPath(profile.Location)}. An S3 address starts with https:// or http://.")).ConfigureAwait(false);
            }

            if (isFolder)
                profile = profile with { Location = Path.GetFullPath(profile.Location) };
            else if (profile.AccessKey.Length == 0)
                return await session.FailAsync(new Error("input.no_key", "An S3 archive needs --access-key.")).ConfigureAwait(false);

            var secret = isFolder ? "" : await SecretAsync(parse.GetValue(secretStdin), session).ConfigureAwait(false);

            if (secret is null)
                return await session.FailAsync(new Error("input.no_secret",
                    "No secret key given. Run this in a terminal to be asked for it, or pipe it in with --secret-stdin.")).ConfigureAwait(false);

            using var archive = DbnArchive.Open(profile.ToOptions(secret), cacheRoot: null);

            var layout = await archive.LayoutAsync(cancel).ConfigureAwait(false);

            if (!layout.IsSuccess)
                return await session.FailAsync(layout.Error!).ConfigureAwait(false);

            var name = parse.GetValue(session.Profile)!;

            session.Profiles.Save(name, profile, secret);

            await session.Out.WriteLineAsync(name == Profiles.DefaultName
                ? $"Logged in to {profile.Location}{(isFolder ? "" : $" as {profile.AccessKey}")}."
                : $"Logged in to {profile.Location}{(isFolder ? "" : $" as {profile.AccessKey}")}, saved as {name}.").ConfigureAwait(false);

            return Session.Ok;
        });

        return command;
    }

    private static async Task<string?> SecretAsync(bool fromStdin, Session session)
    {
        if (fromStdin)
            return (await session.In.ReadLineAsync().ConfigureAwait(false))?.Trim() is { Length: > 0 } piped ? piped : null;

        if (session.Keyboard is not { } keyboard)
            return null;

        await session.Out.WriteAsync("Secret key: ").ConfigureAwait(false);

        var secret = new StringBuilder();

        while (true)
        {
            var key = keyboard();

            if (key.Key == ConsoleKey.Enter)
                break;

            if (key.Key == ConsoleKey.Backspace)
            {
                if (secret.Length > 0)
                    secret.Length--;
            }
            else if (!char.IsControl(key.KeyChar))
            {
                secret.Append(key.KeyChar);
            }
        }

        await session.Out.WriteLineAsync().ConfigureAwait(false);

        return secret.Length > 0 ? secret.ToString() : null;
    }
}
