using System.CommandLine;
using DbnSuite.Client;
using DbnSuite.Client.Archive;
using DbnSuite.Client.Contracts;
using DbnSuite.Client.Dbn;
using DbnSuite.Client.Time;

namespace DbnSuite.Fetch.Commands;

/// <summary><c>dbnf show mbo ESZ26 2026-10-28</c>: one day's records, a line each.</summary>
public static class ShowCommand
{
    public static Command Create(Session session)
    {
        var schema = new Argument<string>("schema") { Description = "A schema, such as mbo or ohlcv-1s." };
        var symbol = new Argument<string>("symbol") { Description = "A root (ES), a contract (ESZ26), or for mbo ES.n.0 or ES.n.1." };
        var day = Dates.Argument("day", "A UTC day, as yyyy-MM-dd.");
        var limit = new Option<int>("--limit") { Description = "How many records to print.", DefaultValueFactory = _ => 20 };
        var zone = new Option<string>("--tz") { Description = "The time zone to show times in.", DefaultValueFactory = _ => Zones.Default };

        var command = new Command("show", "Print one day's records at the terminal.")
        {
            schema, symbol, day, limit, zone
        };

        command.SetAction((parse, cancel) => session.RunAsync(parse, async archive =>
        {
            var request = new ShowRequest(parse.GetValue(schema)!, parse.GetValue(symbol)!, parse.GetValue(day),
                parse.GetValue(limit), parse.GetValue(zone)!);

            var shown = await ReadAsync(archive, request,
                async heading =>
                {
                    await session.Out.WriteLineAsync(heading).ConfigureAwait(false);
                    await session.Out.WriteLineAsync().ConfigureAwait(false);
                },
                line => session.Out.WriteLineAsync(line),
                cancel).ConfigureAwait(false);

            if (!shown.IsSuccess)
                return await session.FailAsync(shown.Error!).ConfigureAwait(false);

            var s = shown.Value;

            await session.Out.WriteLineAsync(Text.Invariant(
                $"\nShowed {Text.Count(s.Shown)} of {Text.Count(s.Matched)} records for {s.Symbol} ({Text.Count(s.Total)} in the file).")).ConfigureAwait(false);

            return Session.Ok;
        }, cancel));

        return command;
    }

    /// <summary>What to show: a schema's records of a symbol on one UTC day, up to a limit, with times in a zone.</summary>
    internal sealed record ShowRequest(string Schema, string Symbol, DateOnly Day, int Limit, string Zone);

    /// <summary>How many records were shown, how many matched the symbol, and how many the file holds.</summary>
    internal sealed record ShowSummary(string Symbol, long Shown, long Matched, long Total);

    /// <summary>
    /// Reads one day's file, handing <paramref name="heading"/> a line about the file, then
    /// <paramref name="record"/> a line for each record of the symbol, up to the limit. Every record
    /// is read, so the summary counts them all.
    /// </summary>
    internal static async Task<Result<ShowSummary>> ReadAsync(DbnArchive archive, ShowRequest request,
        Func<string, Task> heading, Func<string, Task> record, CancellationToken cancel)
    {
        var spec = SymbolSpec.Parse(request.Symbol);

        if (!spec.IsSuccess)
            return spec.Error!;

        var tz = Zones.Find(request.Zone);

        if (!tz.IsSuccess)
            return tz.Error!;

        var checkedSchema = Session.Schema(request.Schema);

        if (!checkedSchema.IsSuccess)
            return checkedSchema.Error!;

        var name = checkedSchema.Value;
        var date = request.Day;
        var opened = await archive.OpenAsync(name, spec.Value.Root, date, cancel).ConfigureAwait(false);

        if (!opened.IsSuccess)
            return opened.Error!;

        await using var reader = opened.Value;

        // The definitions name each instrument; without them, the file's own mapping will do.
        var instruments = await archive.InstrumentsAsync(spec.Value.Root, date, cancel).ConfigureAwait(false);

        if (instruments.Error is { Code: not (Errors.NotStored or Errors.NoData) } unread)
            return unread;

        var contracts = instruments.IsSuccess ? instruments.Value : new Dictionary<uint, Contract>();

        string NameOf(uint id) => contracts.TryGetValue(id, out var c) ? c.Symbol : reader.Metadata.SymbolOf(id);

        var wanted = Filter(spec.Value, reader.Metadata, contracts);

        if (!wanted.IsSuccess)
            return wanted.Error!;

        var metadata = reader.Metadata;

        await heading(Text.Invariant(
            $"{metadata.Dataset} {metadata.Schema}, DBN v{metadata.Version}, {Text.Day(date)} UTC, requested as {string.Join(", ", metadata.Requested)}, times in {tz.Value.Id}")).ConfigureAwait(false);

        var shown = 0;
        var matched = 0L;
        var total = 0L;

        await foreach (var next in reader.ReadAsync(cancel).ConfigureAwait(false))
        {
            total++;

            if (!wanted.Value(next.InstrumentId))
                continue;

            matched++;

            if (shown < request.Limit)
            {
                await record(RecordText.Format(next, NameOf(next.InstrumentId), tz.Value)).ConfigureAwait(false);
                shown++;
            }
        }

        return new ShowSummary(spec.Value.Text, shown, matched, total);
    }

    /// <summary>Which instrument ids the symbol asks for.</summary>
    private static Result<Func<uint, bool>> Filter(SymbolSpec spec, DbnMetadata metadata,
        IReadOnlyDictionary<uint, Contract> contracts)
    {
        switch (spec.Kind)
        {
            case SymbolKind.Root:
                return (Result<Func<uint, bool>>)(Func<uint, bool>)(_ => true);

            case SymbolKind.Contract:
            {
                var ids = contracts.Values
                    .Where(c => string.Equals(c.Symbol, spec.Text, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(c.RawSymbol, spec.Text, StringComparison.OrdinalIgnoreCase))
                    .Select(c => c.InstrumentId)
                    .ToHashSet();

                return ids.Count > 0
                    ? (Result<Func<uint, bool>>)(Func<uint, bool>)ids.Contains
                    : new Error(Errors.BadSymbol, $"The day's definitions have no contract {spec.Text}.");
            }

            default:
            {
                // A continuous name is only in a file requested by it, as mbo is.
                var ids = metadata.Symbols
                    .Where(s => string.Equals(s.Value, spec.Text, StringComparison.OrdinalIgnoreCase))
                    .Select(s => s.Key)
                    .ToHashSet();

                return ids.Count > 0
                    ? (Result<Func<uint, bool>>)(Func<uint, bool>)ids.Contains
                    : new Error(Errors.BadSymbol,
                        $"This file was not requested as {spec.Text}; show a contract or the root instead. Continuous series of any schema come with export.");
            }
        }
    }
}
