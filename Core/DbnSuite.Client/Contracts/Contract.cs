using DbnSuite.Client.Dbn;

namespace DbnSuite.Client.Contracts;

/// <summary>One instrument, as its <c>definition</c> describes it.</summary>
/// <param name="Symbol">The name with a two-digit year, such as <c>ESZ26</c>.</param>
/// <param name="RawSymbol">CME's own name, such as <c>ESZ6</c>.</param>
/// <param name="FirstTrade">When it was first listed, UTC.</param>
/// <param name="Expiration">When it last trades, UTC.</param>
/// <param name="TickSize">The smallest price step.</param>
public sealed record Contract(
    uint InstrumentId,
    string Symbol,
    string RawSymbol,
    string Root,
    bool IsSpread,
    DateTimeOffset? FirstTrade,
    DateTimeOffset? Expiration,
    decimal? TickSize)
{
    public static Contract From(DefinitionRecord d) =>
        new(d.InstrumentId,
            ContractSymbol.Expand(d.RawSymbol, d.MaturityYear > 0 ? d.MaturityYear : YearOf(d.Expiration)),
            d.RawSymbol,
            d.Asset,
            d.InstrumentClass == DefinitionRecord.Spread,
            Time(d.Activation),
            Time(d.Expiration),
            d.TickSize);

    private static int YearOf(ulong nanos) => Time(nanos)?.Year ?? DateTime.UtcNow.Year;

    private static DateTimeOffset? Time(ulong nanos) => nanos is 0 or DbnTime.Undefined ? null : DbnTime.ToTime(nanos);
}
