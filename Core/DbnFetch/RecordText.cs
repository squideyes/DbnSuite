using System.Globalization;
using DbnClient.Dbn;
using DbnClient.Time;

namespace DbnFetch;

/// <summary>One line of text per record, for reading at a terminal.</summary>
public static class RecordText
{
    private static readonly string[] StatTypes =
    [
        "none", "opening price", "indicative opening price", "settlement price", "session low",
        "session high", "cleared volume", "lowest offer", "highest bid", "open interest",
        "fixing price", "close price", "net change", "vwap", "volatility", "delta", "uncrossing price",
        "upper price limit", "lower price limit", "block volume", "venue-specific volume"
    ];

    private static readonly string[] StatusActions =
    [
        "none", "pre-open", "pre-cross", "quoting", "cross", "rotation", "new price indication",
        "trading", "halt", "pause", "suspend", "pre-close", "close", "post-close", "ssr change",
        "not available"
    ];

    /// <param name="symbol">The name to show for the record's instrument.</param>
    public static string Format(DbnRecord record, string symbol, TimeZoneInfo zone)
    {
        var head = $"{Zones.Format(record.TsEvent, zone)}  {symbol,-14}";

        return record switch
        {
            MboRecord m => Invariant($"{head} {Action(m.Action),-6} {Side(m.Side),-4} {m.Quantity,5} @ {Price(m.Price),-12} order {m.OrderId}"),
            OhlcvRecord b => Invariant($"{head} O {Price(b.Open)}  H {Price(b.High)}  L {Price(b.Low)}  C {Price(b.Close)}  V {b.Volume}"),
            DefinitionRecord d => Invariant($"{head} {d.RawSymbol,-24} tick {Price(d.TickSize)}  expires {Expiry(d.Expiration)}"),
            StatRecord s => Invariant($"{head} {Name(StatTypes, s.StatType),-24} {StatValue(s)}"),
            StatusRecord s => Invariant($"{head} {Name(StatusActions, s.Action),-20} trading {s.IsTrading}"),
            OtherRecord o => Invariant($"{head} rtype 0x{o.RType:X2}, {o.Length} bytes")
        };
    }

    private static string Expiry(ulong nanos) => nanos == DbnTime.Undefined ? "-" : DbnTime.Format(nanos)[..10];

    private static string StatValue(StatRecord s) =>
        s.Price is { } price
            ? Price(price)
            : s.Quantity == StatRecord.UndefinedQuantity ? "-" : s.Quantity.ToString("N0", CultureInfo.InvariantCulture);

    private static string Action(char action) => action switch
    {
        'A' => "add",
        'C' => "cancel",
        'M' => "modify",
        'R' => "clear",
        'T' => "trade",
        'F' => "fill",
        _ => "none"
    };

    private static string Side(char side) => side switch
    {
        'B' => "bid",
        'A' => "ask",
        _ => "-"
    };

    private static string Price(decimal? price) =>
        price is { } p ? p.ToString("0.#########", CultureInfo.InvariantCulture) : "-";

    private static string Name(string[] names, ushort value) =>
        value < names.Length ? names[value] : value.ToString(CultureInfo.InvariantCulture);

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
