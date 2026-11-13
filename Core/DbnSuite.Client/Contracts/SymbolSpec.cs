using System.Text.RegularExpressions;

namespace DbnSuite.Client.Contracts;

public enum SymbolKind
{
    /// <summary>Every contract of a root: <c>ES</c>.</summary>
    Root,

    /// <summary>One contract or spread: <c>ESZ26</c>, <c>ESZ6</c>, <c>ESZ26-ESH27</c>.</summary>
    Contract,

    /// <summary>A continuous series: <c>ES.v.0</c>.</summary>
    Continuous
}

/// <summary>What a symbol typed by a user asks for, and the root it belongs to.</summary>
/// <param name="Rule">For a continuous name: <c>c</c>, <c>v</c> or <c>n</c>.</param>
/// <param name="Rank">For a continuous name: 0 for the front, 1 for the next.</param>
public sealed partial record SymbolSpec(string Text, SymbolKind Kind, string Root, char Rule = '\0', int Rank = 0)
{
    public static Result<SymbolSpec> Parse(string text)
    {
        var symbol = text.Trim().ToUpperInvariant();

        if (ContinuousName().Match(symbol) is { Success: true } continuous)
        {
            var root = continuous.Groups["root"].Value;
            var rule = char.ToLowerInvariant(continuous.Groups["rule"].Value[0]);
            var rank = continuous.Groups["rank"].Value[0] - '0';

            return new SymbolSpec($"{root}.{rule}.{rank}", SymbolKind.Continuous, root, rule, rank);
        }

        var first = symbol.Split('-')[0];

        if (ContractName().Match(first) is { Success: true } contract)
            return new SymbolSpec(symbol, SymbolKind.Contract, contract.Groups["root"].Value);

        if (RootName().IsMatch(symbol))
            return new SymbolSpec(symbol, SymbolKind.Root, symbol);

        return new Error(Errors.BadSymbol,
            $"\"{text}\" is not a symbol DbnClient understands: use a root (ES), a contract (ESZ26) or a continuous name (ES.v.0).");
    }

    [GeneratedRegex("^(?<root>[A-Z0-9]{1,6})(?<suffix>\\.(?<rule>[CVN])\\.(?<rank>[0-9]))$")]
    private static partial Regex ContinuousName();

    [GeneratedRegex("^(?<root>[A-Z0-9]{1,6}?)[FGHJKMNQUVXZ][0-9]{1,2}$")]
    private static partial Regex ContractName();

    [GeneratedRegex("^[A-Z0-9]{1,6}$")]
    private static partial Regex RootName();
}
