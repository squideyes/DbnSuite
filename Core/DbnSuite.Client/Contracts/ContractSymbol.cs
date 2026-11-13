using System.Globalization;
using System.Text;

namespace DbnSuite.Client.Contracts;

/// <summary>
/// Contract names with a two-digit year. CME names a contract with one digit
/// (<c>ESZ6</c>), which repeats every ten years; DbnSync shows <c>ESZ26</c>.
/// </summary>
public static class ContractSymbol
{
    private const string MonthCodes = "FGHJKMNQUVXZ";

    /// <summary>
    /// The two-digit-year form of a CME name, legs and all: <c>ESZ6</c> becomes
    /// <c>ESZ26</c>, <c>ESZ6-ESH7</c> becomes <c>ESZ26-ESH27</c>.
    /// <paramref name="year"/> is the contract's maturity year, which is its
    /// first leg's; each later leg is the first year from there ending in its digit.
    /// </summary>
    public static string Expand(string raw, int year)
    {
        var legs = raw.Split('-');
        var text = new StringBuilder(raw.Length + legs.Length);
        var from = year;

        for (var i = 0; i < legs.Length; i++)
        {
            if (i > 0)
                text.Append('-');

            text.Append(ExpandLeg(legs[i], ref from));
        }

        return text.ToString();
    }

    /// <summary>The month a code letter stands for, 1 to 12, or 0 for none.</summary>
    public static int Month(char code) => MonthCodes.IndexOf(char.ToUpperInvariant(code)) + 1;

    /// <summary>
    /// Whether <paramref name="text"/> names the contract whose CME name is
    /// <paramref name="raw"/>, in either form: <c>ESZ26</c> or <c>ESZ6</c>.
    /// </summary>
    public static bool Matches(string text, string raw, int year) =>
        string.Equals(text, raw, StringComparison.OrdinalIgnoreCase)
        || string.Equals(text, Expand(raw, year), StringComparison.OrdinalIgnoreCase);

    private static string ExpandLeg(string leg, ref int from)
    {
        // A leg ends in a month letter and one year digit; anything else is left alone.
        if (leg.Length < 3 || !char.IsAsciiDigit(leg[^1]) || char.IsAsciiDigit(leg[^2]) || Month(leg[^2]) == 0)
            return leg;

        var digit = leg[^1] - '0';
        var full = from + (digit - from % 10 + 10) % 10;

        from = full;

        return string.Create(CultureInfo.InvariantCulture, $"{leg[..^1]}{full % 100:D2}");
    }
}
