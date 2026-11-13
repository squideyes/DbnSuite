namespace DbnSuite.Client.Contracts;

/// <summary>What a calendar roll counts back from.</summary>
public enum RollAnchor
{
    /// <summary>The contract's last trade, as for the equity indexes and crude oil.</summary>
    Expiration,

    /// <summary>
    /// The first day of the contract's delivery month, as for gold, whose traders
    /// leave before first notice, near the end of the month before.
    /// </summary>
    DeliveryMonth
}

/// <summary>
/// How a root rolls by the calendar (<c>.c.</c> names): the contract months it
/// holds, and how many days before its <see cref="RollAnchor"/> it leaves each.
/// </summary>
/// <param name="Months">The month codes held, such as <c>GJMQZ</c>.</param>
/// <param name="Days">How many days before <paramref name="Anchor"/> a contract is left.</param>
public sealed record CalendarRule(string Months, int Days, RollAnchor Anchor)
{
    private const string EveryMonth = "FGHJKMNQUVXZ";

    /// <summary>
    /// Every listed month, <see cref="RollSchedule.DefaultRollDays"/> days before
    /// expiry: right for the equity indexes and crude oil, and used for any root
    /// without a rule of its own.
    /// </summary>
    public static CalendarRule Default { get; } = new(EveryMonth, RollSchedule.DefaultRollDays, RollAnchor.Expiration);

    /// <summary>
    /// Gold lists every month but trades February, April, June, August and
    /// December. Checked against 2025's open interest, which left GC 5 to 11
    /// days before each delivery month.
    /// </summary>
    private static readonly CalendarRule Gold = new("GJMQZ", RollSchedule.DefaultRollDays, RollAnchor.DeliveryMonth);

    /// <summary>The rule for a root; a root without one of its own gets <see cref="Default"/>.</summary>
    public static CalendarRule For(string root) => root.ToUpperInvariant() switch
    {
        "GC" or "MGC" => Gold,
        _ => Default
    };

    /// <summary>This rule, rolling <paramref name="days"/> before its anchor when given.</summary>
    public CalendarRule WithDays(int? days) => days is { } d ? this with { Days = d } : this;

    /// <summary>Whether the rule ever holds <paramref name="contract"/>.</summary>
    public bool Holds(Contract contract) =>
        MonthCode(contract) is { } code && Months.Contains(code, StringComparison.Ordinal);

    /// <summary>The first calendar day <paramref name="contract"/> is no longer held.</summary>
    public DateOnly LeavesOn(Contract contract)
    {
        var anchor = Anchor == RollAnchor.DeliveryMonth && Delivery(contract) is { } month
            ? month
            : DateOnly.FromDateTime(contract.Expiration!.Value.UtcDateTime);

        return anchor.AddDays(-Days);
    }

    /// <summary>The month letter of an outright's name, such as <c>Z</c> in <c>GCZ25</c>.</summary>
    private static char? MonthCode(Contract contract) =>
        contract.Symbol.Length >= 4 && ContractSymbol.Month(contract.Symbol[^3]) > 0 ? char.ToUpperInvariant(contract.Symbol[^3]) : null;

    /// <summary>The first day of an outright's delivery month, from its name.</summary>
    private static DateOnly? Delivery(Contract contract)
    {
        if (MonthCode(contract) is not { } code || !int.TryParse(contract.Symbol.AsSpan(contract.Symbol.Length - 2),
                System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var year))
            return null;

        return new DateOnly(2000 + year, ContractSymbol.Month(code), 1);
    }
}
