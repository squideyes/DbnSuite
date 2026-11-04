using System.CommandLine;

namespace DbnFetch.Commands;

/// <summary><c>dbnf cache</c>: what the local cache holds, and <c>dbnf cache clear</c> to free it.</summary>
public static class CacheCommand
{
    public static Command Create(Session session)
    {
        var command = new Command("cache", "Show what the local cache holds.");

        command.SetAction((parse, cancel) => session.RunAsync(parse, async archive =>
        {
            if (archive.Cache is not { } cache)
            {
                await session.Out.WriteLineAsync("This archive is a folder; nothing is cached for it.").ConfigureAwait(false);

                return Session.Ok;
            }

            var files = cache.Files();

            await session.Out.WriteLineAsync(Text.Invariant(
                $"{cache.Root}: {Text.Count(files.Count)} files, {Text.Bytes(files.Sum(f => f.Size))}")).ConfigureAwait(false);

            if (files.Count == 0)
                return Session.Ok;

            // Keys are dataset/schema/root/year/file: group by schema and root.
            var series = files
                .Select(f => (Parts: f.Key.Split('/'), f.Size))
                .Where(f => f.Parts.Length >= 3)
                .GroupBy(f => (Root: f.Parts[2], Schema: f.Parts[1]))
                .OrderBy(g => g.Key.Root, StringComparer.Ordinal).ThenBy(g => g.Key.Schema, StringComparer.Ordinal)
                .Select(g => new[] { g.Key.Root, g.Key.Schema, Text.Count(g.Count()), Text.Bytes(g.Sum(f => f.Size)) })
                .ToList();

            await session.Out.WriteLineAsync().ConfigureAwait(false);
            await session.WriteAsync(Text.Table(["Root", "Schema", "Files", "Size"], series, [false, false, true, true])).ConfigureAwait(false);

            return Session.Ok;
        }, cancel));

        command.Subcommands.Add(Clear(session));

        return command;
    }

    private static Command Clear(Session session)
    {
        var schemaName = new Argument<string?>("schema") { Description = "Only this schema.", Arity = ArgumentArity.ZeroOrOne };
        var rootName = new Argument<string?>("root") { Description = "Only this root of that schema.", Arity = ArgumentArity.ZeroOrOne };

        var command = new Command("clear", "Delete cached files: all of them, or one schema, or one schema of one root.")
        {
            schemaName, rootName
        };

        command.SetAction((parse, cancel) => session.RunAsync(parse, async archive =>
        {
            if (archive.Cache is not { } cache)
            {
                await session.Out.WriteLineAsync("This archive is a folder; nothing is cached for it.").ConfigureAwait(false);

                return Session.Ok;
            }

            var prefix = "";

            if (parse.GetValue(schemaName) is { } named)
            {
                // Anything else would free nothing and say so as if it had worked.
                var schema = Session.Schema(named);

                if (!schema.IsSuccess)
                    return await session.FailAsync(schema.Error!).ConfigureAwait(false);

                prefix = parse.GetValue(rootName) is { } r
                    ? $"{archive.Dataset}/{schema.Value}/{r.ToUpperInvariant()}/"
                    : $"{archive.Dataset}/{schema.Value}/";
            }

            var freed = cache.Remove(prefix);

            await session.Out.WriteLineAsync($"Freed {Text.Bytes(freed)}.").ConfigureAwait(false);

            return Session.Ok;
        }, cancel));

        return command;
    }
}
