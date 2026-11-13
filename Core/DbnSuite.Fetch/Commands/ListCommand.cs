using System.CommandLine;

namespace DbnSuite.Fetch.Commands;

/// <summary><c>dbnf ls</c>: every series in the archive, with its days and size.</summary>
public static class ListCommand
{
    public static Command Create(Session session)
    {
        var schema = new Option<string?>("--schema") { Description = "Only this schema, such as mbo." };
        var root = new Option<string?>("--root") { Description = "Only this root, such as ES." };

        var command = new Command("ls", "List the series in the archive: schema, root, days, size, first and last day.")
        {
            schema, root
        };

        command.SetAction((parse, cancel) => session.RunAsync(parse, async archive =>
        {
            var listed = await archive.ListSeriesAsync(cancel).ConfigureAwait(false);

            if (!listed.IsSuccess)
                return await session.FailAsync(listed.Error!).ConfigureAwait(false);

            var series = listed.Value
                .Where(s => parse.GetValue(schema) is not { } wanted || string.Equals(s.Schema, wanted, StringComparison.OrdinalIgnoreCase))
                .Where(s => parse.GetValue(root) is not { } wanted || string.Equals(s.Root, wanted, StringComparison.OrdinalIgnoreCase))
                .OrderBy(s => s.Root, StringComparer.Ordinal).ThenBy(s => s.Schema, StringComparer.Ordinal)
                .ToList();

            if (series.Count == 0)
            {
                await session.Out.WriteLineAsync("Nothing matches in the archive.").ConfigureAwait(false);

                return Session.Ok;
            }

            await session.WriteAsync(Text.Table(
                ["Root", "Schema", "Days", "No data", "Size", "First", "Last"],
                [.. series.Select(s => new[]
                {
                    s.Root, s.Schema, Text.Count(s.Files), Text.Count(s.EmptyDays), Text.Bytes(s.Bytes),
                    s.First is { } f ? Text.Day(f) : "-", s.Last is { } l ? Text.Day(l) : "-"
                })],
                [false, false, true, true, true, false, false])).ConfigureAwait(false);

            await session.Out.WriteLineAsync(Text.Invariant(
                $"\n{series.Count} series, {Text.Bytes(series.Sum(s => s.Bytes))}.")).ConfigureAwait(false);

            return Session.Ok;
        }, cancel));

        return command;
    }
}
