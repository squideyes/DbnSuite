using System.CommandLine;
using System.Globalization;
using DbnSuite.Client.Archive;
using DbnSuite.Client.Tape;

namespace DbnSuite.Fetch.Commands;

/// <summary><c>dbnf tape pull ES --tapes D:\Tapes --profile tapes</c>: copies tapes DbnSpool made down to a folder.</summary>
internal static class TapePullCommand
{
    private const string PartSuffix = ".part";
    private const string DateFormat = "yyyyMMdd";
    private const int DatePart = 2;

    internal static readonly string[] Kinds = ["s1", "trade", "l1"];

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
                return await session.FailAsync(new DbnSuite.Client.Error("input.bad_tape_kind", $"\"{bad}\" is not a kind. A kind is s1, trade or l1.")).ConfigureAwait(false);

            var planned = await PlanAsync(archive, root, folder, parse.GetValue(from), parse.GetValue(to), wanted, cancel).ConfigureAwait(false);

            if (!planned.IsSuccess)
                return await session.FailAsync(planned.Error!).ConfigureAwait(false);

            var plan = planned.Value;

            // Only full ETH days have tapes: anything else here, or still in the store, is left out.
            var removed = TapePaths.RemoveExcluded(folder, plan.Rules).Count;

            await CopyAllAsync(archive.Store, plan, folder, null, cancel).ConfigureAwait(false);

            var copied = (plan.CatalogCopied ? 1 : 0) + plan.Copy.Count;
            var skipped = (plan.CatalogCopied ? 0 : 1) + plan.Skipped;
            var bytes = (plan.CatalogCopied ? plan.CatalogSize : 0) + plan.Bytes;

            await session.Out.WriteLineAsync($"{root}: {Text.Count(copied)} files copied, {Text.Bytes(bytes)}, {Text.Count(skipped)} already here").ConfigureAwait(false);

            if (removed > 0)
                await session.Out.WriteLineAsync($"{root}: {Text.Count(removed)} tapes removed here, because their dates are not full ETH days").ConfigureAwait(false);

            return Session.Ok;
        }, cancel));

        return command;
    }

    /// <summary>
    /// What a tape pull would copy. The catalog comes down first, since its rules say which dates have
    /// tapes; the tapes wait for <see cref="CopyAllAsync"/>.
    /// </summary>
    /// <param name="Copy">The tapes the folder lacks, or holds at another size.</param>
    /// <param name="Skipped">The tapes the folder already holds.</param>
    internal sealed record TapePullPlan(Catalog Rules, bool CatalogCopied, long CatalogSize, IReadOnlyList<StoredObject> Copy, int Skipped)
    {
        /// <summary>How much there is to copy, besides the catalog.</summary>
        public long Bytes => Copy.Sum(s => s.Size);
    }

    /// <summary>Copies a root's catalog into the folder, then works out which of its tapes in the span to copy.</summary>
    /// <param name="kinds">The kinds wanted, each s1, trade or l1.</param>
    internal static async Task<DbnSuite.Client.Result<TapePullPlan>> PlanAsync(DbnArchive archive, string root, string folder,
        DateOnly? from, DateOnly? to, IReadOnlyList<string> kinds, CancellationToken cancel)
    {
        var catalog = await archive.Store.FindAsync($"{archive.Dataset}/catalog/{root}.catalog.json", cancel).ConfigureAwait(false);

        if (catalog is null)
            return new DbnSuite.Client.Error("tape.no_catalog", $"The store holds no catalog for {root}.");

        var copied = await CopyAsync(archive.Store, catalog, folder, cancel).ConfigureAwait(false);
        var rules = await Catalog.LoadAsync(TargetOf(folder, catalog.Key), cancel).ConfigureAwait(false);

        if (!rules.IsSuccess)
            return rules.Error!;

        var copy = new List<StoredObject>();
        var skipped = 0;

        foreach (var kind in kinds)
        {
            await foreach (var stored in archive.Store.ObjectsAsync($"{archive.Dataset}/{kind}/{root}/", cancel).ConfigureAwait(false))
            {
                if (!InSpan(stored.Key, from, to))
                    continue;

                if (TapePaths.TradeDateOf(stored.Key) is { } date && rules.Value.Judges(date) && !rules.Value.IsFullDay(date))
                    continue;

                var existing = new FileInfo(TargetOf(folder, stored.Key));

                if (existing.Exists && existing.Length == stored.Size)
                    skipped++;
                else
                    copy.Add(stored);
            }
        }

        return new TapePullPlan(rules.Value, copied, catalog.Size, copy, skipped);
    }

    /// <summary>Copies a plan's tapes into the folder, telling <paramref name="progress"/> each one as it lands.</summary>
    internal static async Task CopyAllAsync(IObjectStore store, TapePullPlan plan, string folder, IProgress<StoredObject>? progress,
        CancellationToken cancel)
    {
        foreach (var stored in plan.Copy)
        {
            await CopyAsync(store, stored, folder, cancel).ConfigureAwait(false);
            progress?.Report(stored);
        }
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
            throw new ArchiveException(new DbnSuite.Client.Error(DbnSuite.Client.Errors.UnknownLayout, $"The object key {key} is outside the tape folder."));

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
