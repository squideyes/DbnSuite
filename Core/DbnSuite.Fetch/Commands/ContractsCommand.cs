using System.CommandLine;
using DbnSuite.Client.Contracts;

namespace DbnSuite.Fetch.Commands;

/// <summary><c>dbnf contracts ES</c>: every contract of a root, from the archive's definitions.</summary>
public static class ContractsCommand
{
    public static Command Create(Session session)
    {
        var root = new Argument<string>("root") { Description = "A root, such as ES." };
        var spreads = new Option<bool>("--spreads") { Description = "List spreads as well as outright contracts." };
        var active = new Option<bool>("--active") { Description = "Only contracts that have not expired." };

        var command = new Command("contracts", "List a root's contracts with their expiry and tick size.")
        {
            root, spreads, active
        };

        command.SetAction((parse, cancel) => session.RunAsync(parse, async archive =>
        {
            var name = parse.GetValue(root)!.ToUpperInvariant();
            var found = await archive.ContractsAsync(name, null, cancel).ConfigureAwait(false);

            if (!found.IsSuccess)
                return await session.FailAsync(found.Error!).ConfigureAwait(false);

            var now = DateTimeOffset.UtcNow;
            var contracts = found.Value
                .Where(c => parse.GetValue(spreads) || !c.IsSpread)
                .Where(c => !parse.GetValue(active) || c.Expiration is null || c.Expiration > now)
                .ToList();

            if (contracts.Count == 0)
            {
                await session.Out.WriteLineAsync($"No {name} contracts in the archive's definitions.").ConfigureAwait(false);

                return Session.Ok;
            }

            await session.WriteAsync(Text.Table(
                ["Contract", "CME", "Listed", "Expires", "Tick"],
                [.. contracts.Select(c => new[]
                {
                    c.Symbol, c.RawSymbol, Text.Day(c.FirstTrade), Text.Day(c.Expiration), Tick(c)
                })],
                [false, false, false, false, true])).ConfigureAwait(false);

            await session.Out.WriteLineAsync(Text.Invariant($"\n{contracts.Count} contracts.")).ConfigureAwait(false);

            return Session.Ok;
        }, cancel));

        return command;
    }

    private static string Tick(Contract c) =>
        c.TickSize is { } t ? t.ToString("0.#########", System.Globalization.CultureInfo.InvariantCulture) : "-";
}
