using System.CommandLine;
using DbnSuite.Client.Contracts;

namespace DbnSuite.Fetch.Commands;

/// <summary><c>dbnf rolls ES.v.0 --last 1y</c>: when a continuous series changes contract, and by how much.</summary>
public static class RollsCommand
{
    public static Command Create(Session session)
    {
        var symbol = new Argument<string>("series") { Description = "A continuous name: ES.c.0 (calendar), ES.v.0 (volume) or ES.n.0 (open interest)." };
        var range = new RangeOptions();
        var rollDays = new Option<int?>("--roll-days") { Description = "For .c. series: roll this many days before expiry (before the delivery month for gold) instead of the root's own rule." };

        var command = new Command("rolls", "List a continuous series' rolls: the date, the contracts, and the settlement gap.")
        {
            symbol, rollDays
        };

        range.AddTo(command);

        command.SetAction((parse, cancel) => session.RunAsync(parse, async archive =>
        {
            var spec = SymbolSpec.Parse(parse.GetValue(symbol)!);

            if (!spec.IsSuccess)
                return await session.FailAsync(spec.Error!).ConfigureAwait(false);

            if (spec.Value.Kind != SymbolKind.Continuous)
                return await session.FailAsync(new DbnSuite.Client.Error("input.bad_series", $"{spec.Value.Text} is not a continuous name such as ES.v.0.")).ConfigureAwait(false);

            var span = range.Resolve(parse);

            if (!span.IsSuccess)
                return await session.FailAsync(span.Error!).ConfigureAwait(false);

            var schedule = await RollSchedule.BuildAsync(archive, spec.Value, span.Value, parse.GetValue(rollDays), null, cancel)
                .ConfigureAwait(false);

            if (!schedule.IsSuccess)
                return await session.FailAsync(schedule.Error!).ConfigureAwait(false);

            var rolls = schedule.Value.Rolls;
            var first = schedule.Value.On(span.Value.From);

            await session.Out.WriteLineAsync(first is null
                ? $"{spec.Value.Text} holds no contract on {Text.Day(span.Value.From)}."
                : $"{spec.Value.Text} holds {first.Symbol} on {Text.Day(span.Value.From)}.").ConfigureAwait(false);

            if (rolls.Count == 0)
            {
                await session.Out.WriteLineAsync("No rolls in the range.").ConfigureAwait(false);

                return Session.Ok;
            }

            await session.Out.WriteLineAsync().ConfigureAwait(false);
            await session.WriteAsync(Text.Table(
                ["From trade date", "Old", "New", "Old settle", "New settle", "Gap"],
                [.. rolls.Select(r => new[]
                {
                    Text.Day(r.TradeDate), r.From.Symbol, r.To.Symbol, Price(r.FromSettle), Price(r.ToSettle), Price(r.Gap)
                })],
                [false, false, false, true, true, true])).ConfigureAwait(false);

            return Session.Ok;
        }, cancel));

        return command;
    }

    private static string Price(decimal? price) =>
        price is { } p ? p.ToString("0.#########", System.Globalization.CultureInfo.InvariantCulture) : "-";
}
