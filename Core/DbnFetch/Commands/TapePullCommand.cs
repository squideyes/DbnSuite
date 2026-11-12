using System.CommandLine;
using System.Globalization;
using DbnClient.Archive;
using DbnClient.Tape;

namespace DbnFetch.Commands;

/// <summary><c>dbnf tape pull ES --tapes D:\Tapes --profile tapes</c>: copies tapes DbnSpool made down to a folder.</summary>
internal static class TapePullCommand
{
    private const string PartSuffix = ".part";
    private const string DateFormat = "yyyyMMdd";
    private const int DatePart = 2;

    private static readonly string[] Kinds = ["s1", "trade", "l1"];

    public static Command Create(Session session)
    {
        var options = new TapeOptions();
        var from = Dates.Optional("--from", "The first trade date, as yyyy-MM-dd. Every date by default.");
        var to = Dates.Optional("--to", "The last trade date, included.");
        var kinds = new Option<string[]>("--kind") { Description = "Only these kinds: s1, trade or l1.", AllowMultipleArgumentsPerToken = true };
        var command = new Command("pull", "Copy a root's tapes and catalog down from a tape store, skipping those already here.") { from, to, kinds };

        options.AddTo(command);

        command.SetAction((parse, cancel) => session.RunAsync(parse, async archive =>
        {
            var root = options.RootOf(parse);
            var folder = parse.GetValue(options.Tapes)!;
            var wanted = parse.GetValue(kinds) is { Length: > 0 } named ? named.Select(k => k.ToLowerInvariant()).ToArray() : Kinds;

            if (wanted.FirstOrDefault(k => !Kinds.Contains(k)) is { } bad)
                return await session.FailAsync(new DbnClient.Error("input.bad_tape_kind", $"\"{bad}\" is not a kind. A kind is s1, trade or l1.")).ConfigureAwait(false);

            var catalog = await archive.Store.FindAsync($"{archive.Dataset}/catalog/{root}.catalog.json", cancel).ConfigureAwait(false);

            if (catalog is null)
                return await session.FailAsync(new DbnClient.Error("tape.no_catalog", $"The store holds no catalog for {root}.")).ConfigureAwait(false);

            var copied = await CopyAsync(archive.Store, catalog, folder, cancel).ConfigureAwait(false) ? 1 : 0;
            var skipped = 1 - copied;
            var bytes = copied * catalog.Size;
            var rules = await Catalog.LoadAsync(TargetOf(folder, catalog.Key), cancel).ConfigureAwait(false);

            if (!rules.IsSuccess)
                return await session.FailAsync(rules.Error!).ConfigureAwait(false);

            // Only full ETH days have tapes: anything else here, or still in the store, is left out.
            var removed = TapePaths.RemoveExcluded(folder, rules.Value).Count;

            foreach (var kind in wanted)
            {
                await foreach (var stored in archive.Store.ObjectsAsync($"{archive.Dataset}/{kind}/{root}/", cancel).ConfigureAwait(false))
                {
                    if (!InSpan(stored.Key, parse.GetValue(from), parse.GetValue(to)))
                        continue;

                    if (TapePaths.TradeDateOf(stored.Key) is { } date && rules.Value.Judges(date) && !rules.Value.IsFullDay(date))
                        continue;

                    if (await CopyAsync(archive.Store, stored, folder, cancel).ConfigureAwait(false))
                    {
                        copied++;
                        bytes += stored.Size;
                    }
                    else
                    {
                        skipped++;
                    }
                }
            }

            await session.Out.WriteLineAsync($"{root}: {Text.Count(copied)} files copied, {Text.Bytes(bytes)}, {Text.Count(skipped)} already here").ConfigureAwait(false);

            if (removed > 0)
                await session.Out.WriteLineAsync($"{root}: {Text.Count(removed)} tapes removed here, because their dates are not full ETH days").ConfigureAwait(false);

            return Session.Ok;
        }, cancel));

        return command;
    }

    /// <summary>True when a tape's trade date, which its name carries, is in the span.</summary>
    internal static bool InSpan(string key, DateOnly? from, DateOnly? to)
    {
        var parts = key[(key.LastIndexOf('/') + 1)..].Split('.');

        if (parts.Length <= DatePart || !DateOnly.TryParseExact(parts[DatePart], DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return false;

        return (from is null || date >= from) && (to is null || date <= to);
    }

    /// <summary>Where a key puts its file, which must be inside the folder: a key comes from the store, not from us.</summary>
    internal static string TargetOf(string folder, string key)
    {
        var root = Path.GetFullPath(folder);
        var target = Path.GetFullPath(Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar)));
        var inside = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;

        if (!target.StartsWith(inside, StringComparison.Ordinal))
            throw new ArchiveException(new DbnClient.Error(DbnClient.Errors.UnknownLayout, $"The object key {key} is outside the tape folder."));

        return target;
    }

    private static async Task<bool> CopyAsync(IObjectStore store, StoredObject stored, string folder, CancellationToken cancel)
    {
        var target = TargetOf(folder, stored.Key);
        var existing = new FileInfo(target);

        if (existing.Exists && existing.Length == stored.Size)
            return false;

        Directory.CreateDirectory(existing.DirectoryName!);

        var part = target + PartSuffix;
        var source = await store.OpenAsync(stored.Key, 0, cancel).ConfigureAwait(false);

        await using (source.ConfigureAwait(false))
        {
            var file = File.Create(part);

            await using (file.ConfigureAwait(false))
                await source.CopyToAsync(file, cancel).ConfigureAwait(false);
        }

        File.Move(part, target, overwrite: true);

        return true;
    }
}
