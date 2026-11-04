using System.CommandLine;
using DbnClient;
using DbnClient.Archive;
using DbnClient.Contracts;
using DbnClient.Dbn;
using DbnClient.Time;

namespace DbnFetch.Commands;

/// <summary><c>dbnf show mbo ESZ26 2026-10-28</c>: one day's records, a line each.</summary>
public static class ShowCommand
{
    public static Command Create(Session session)
    {
        var schema = new Argument<string>("schema") { Description = "A schema, such as mbo or ohlcv-1s." };
        var symbol = new Argument<string>("symbol") { Description = "A root (ES), a contract (ESZ26), or for mbo ES.n.0 or ES.n.1." };
        var day = new Argument<DateOnly>("day") { Description = "A UTC day, as yyyy-MM-dd." };
        var limit = new Option<int>("--limit") { Description = "How many records to print.", DefaultValueFactory = _ => 20 };
        var zone = new Option<string>("--tz") { Description = "The time zone to show times in.", DefaultValueFactory = _ => Zones.Default };

        var command = new Command("show", "Print one day's records at the terminal.")
        {
            schema, symbol, day, limit, zone
        };

        command.SetAction((parse, cancel) => session.RunAsync(parse, async archive =>
        {
            var spec = SymbolSpec.Parse(parse.GetValue(symbol)!);

            if (!spec.IsSuccess)
                return await session.FailAsync(spec.Error!).ConfigureAwait(false);

            var tz = Zones.Find(parse.GetValue(zone)!);

            if (!tz.IsSuccess)
                return await session.FailAsync(tz.Error!).ConfigureAwait(false);

            var checkedSchema = Session.Schema(parse.GetValue(schema)!);

            if (!checkedSchema.IsSuccess)
                return await session.FailAsync(checkedSchema.Error!).ConfigureAwait(false);

            var name = checkedSchema.Value;
            var date = parse.GetValue(day);
            var opened = await archive.OpenAsync(name, spec.Value.Root, date, cancel).ConfigureAwait(false);

            if (!opened.IsSuccess)
                return await session.FailAsync(opened.Error!).ConfigureAwait(false);

            await using var reader = opened.Value;

            // The definitions name each instrument; without them, the file's own mapping will do.
            var instruments = await archive.InstrumentsAsync(spec.Value.Root, date, cancel).ConfigureAwait(false);

            if (instruments.Error is { Code: not (Errors.NotStored or Errors.NoData) } unread)
                return await session.FailAsync(unread).ConfigureAwait(false);

            var contracts = instruments.IsSuccess ? instruments.Value : new Dictionary<uint, Contract>();

            string NameOf(uint id) => contracts.TryGetValue(id, out var c) ? c.Symbol : reader.Metadata.SymbolOf(id);

            var wanted = Filter(spec.Value, reader.Metadata, contracts);

            if (!wanted.IsSuccess)
                return await session.FailAsync(wanted.Error!).ConfigureAwait(false);

            var metadata = reader.Metadata;

            await session.Out.WriteLineAsync(Text.Invariant(
                $"{metadata.Dataset} {metadata.Schema}, DBN v{metadata.Version}, {Text.Day(date)} UTC, requested as {string.Join(", ", metadata.Requested)}, times in {tz.Value.Id}")).ConfigureAwait(false);
            await session.Out.WriteLineAsync().ConfigureAwait(false);

            var max = parse.GetValue(limit);
            var shown = 0;
            var matched = 0L;
            var total = 0L;

            await foreach (var record in reader.ReadAsync(cancel).ConfigureAwait(false))
            {
                total++;

                if (!wanted.Value(record.InstrumentId))
                    continue;

                matched++;

                if (shown < max)
                {
                    await session.Out.WriteLineAsync(RecordText.Format(record, NameOf(record.InstrumentId), tz.Value)).ConfigureAwait(false);
                    shown++;
                }
            }

            await session.Out.WriteLineAsync(Text.Invariant(
                $"\nShowed {Text.Count(shown)} of {Text.Count(matched)} records for {spec.Value.Text} ({Text.Count(total)} in the file).")).ConfigureAwait(false);

            return Session.Ok;
        }, cancel));

        return command;
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
