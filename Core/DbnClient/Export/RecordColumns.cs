using DbnClient.Dbn;

namespace DbnClient.Export;

/// <summary>
/// The columns each schema exports as, named as Databento names its fields,
/// and how a record fills them. <c>symbol</c> is the contract; <c>series</c>,
/// when there is one, the continuous name it was picked for.
/// </summary>
public static class RecordColumns
{
    private static readonly string[] StatTypes =
    [
        "none", "opening_price", "indicative_opening_price", "settlement_price", "trading_session_low_price",
        "trading_session_high_price", "cleared_volume", "lowest_offer", "highest_bid", "open_interest",
        "fixing_price", "close_price", "net_change", "vwap", "volatility", "delta", "uncrossing_price",
        "upper_price_limit", "lower_price_limit", "block_volume"
    ];

    /// <summary>DBN numbers a venue's own statistics from 10001, apart from the common ones.</summary>
    private const ushort VenueSpecificVolume1 = 10001;

    /// <summary>A statistic's name as Databento spells it, or its number when this version does not know it.</summary>
    internal static string StatTypeName(ushort type) =>
        type == VenueSpecificVolume1 ? "venue_specific_volume_1"
        : type < StatTypes.Length ? StatTypes[type]
        : type.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static IReadOnlyList<Column> For(string schema, bool series) => Add(schema switch
    {
        "mbo" =>
        [
            Time("ts_recv"), Time("ts_event"), Text("symbol"), Integer("instrument_id"), Text("action"), Text("side"),
            Price("price"), Integer("size"), Integer("order_id"), Integer("flags"), Integer("channel_id"), Integer("sequence")
        ],
        "ohlcv-1s" or "ohlcv-1m" or "ohlcv-1h" or "ohlcv-1d" or "ohlcv-eod" =>
        [
            Time("ts_event"), Text("symbol"), Integer("instrument_id"),
            Price("open"), Price("high"), Price("low"), Price("close"), Integer("volume")
        ],
        "statistics" =>
        [
            Time("ts_event"), Time("ts_ref"), Text("symbol"), Integer("instrument_id"), Text("stat_type"),
            Price("price"), Integer("quantity"), Integer("update_action")
        ],
        "status" =>
        [
            Time("ts_event"), Text("symbol"), Integer("instrument_id"), Integer("action"), Integer("reason"),
            Integer("trading_event"), Text("is_trading")
        ],
        "definition" =>
        [
            Time("ts_event"), Text("symbol"), Text("raw_symbol"), Integer("instrument_id"), Text("asset"),
            Text("instrument_class"), Price("min_price_increment"), Time("activation"), Time("expiration")
        ],
        _ => [Time("ts_event"), Text("symbol"), Integer("instrument_id"), Integer("rtype")]
    }, series);

    /// <summary>Fills <paramref name="cells"/>, which is as long as <see cref="For"/>'s columns.</summary>
    /// <param name="price">Turns a price into the units asked for, adjusted if it is a continuous series.</param>
    public static void Fill(DbnRecord record, string symbol, string? series, Func<decimal?, decimal?> price, Span<Cell> cells)
    {
        var i = 0;

        switch (record)
        {
            case MboRecord m:
                cells[i++] = Cell.Of(m.TsRecv);
                cells[i++] = Cell.Of(m.TsEvent);
                cells[i++] = Cell.Of(symbol);
                cells[i++] = Cell.Of(m.InstrumentId);
                cells[i++] = Cell.Of(m.Action.ToString());
                cells[i++] = Cell.Of(m.Side.ToString());
                cells[i++] = Cell.Of(price(m.Price));
                cells[i++] = Cell.Of(m.Quantity);
                cells[i++] = Cell.Of((long)m.OrderId);
                cells[i++] = Cell.Of(m.Flags);
                cells[i++] = Cell.Of(m.ChannelId);
                cells[i++] = Cell.Of(m.Sequence);
                break;

            case OhlcvRecord b:
                cells[i++] = Cell.Of(b.TsEvent);
                cells[i++] = Cell.Of(symbol);
                cells[i++] = Cell.Of(b.InstrumentId);
                cells[i++] = Cell.Of(price(b.Open));
                cells[i++] = Cell.Of(price(b.High));
                cells[i++] = Cell.Of(price(b.Low));
                cells[i++] = Cell.Of(price(b.Close));
                cells[i++] = Cell.Of((long)b.Volume);
                break;

            case StatRecord s:
                cells[i++] = Cell.Of(s.TsEvent);
                cells[i++] = s.TsRef == DbnTime.Undefined ? Cell.Empty : Cell.Of(s.TsRef);
                cells[i++] = Cell.Of(symbol);
                cells[i++] = Cell.Of(s.InstrumentId);
                cells[i++] = Cell.Of(StatTypeName(s.StatType));
                cells[i++] = Cell.Of(price(s.Price));
                cells[i++] = s.Quantity == StatRecord.UndefinedQuantity ? Cell.Empty : Cell.Of(s.Quantity);
                cells[i++] = Cell.Of(s.UpdateAction);
                break;

            case StatusRecord s:
                cells[i++] = Cell.Of(s.TsEvent);
                cells[i++] = Cell.Of(symbol);
                cells[i++] = Cell.Of(s.InstrumentId);
                cells[i++] = Cell.Of(s.Action);
                cells[i++] = Cell.Of(s.Reason);
                cells[i++] = Cell.Of(s.TradingEvent);
                cells[i++] = Cell.Of(s.IsTrading.ToString());
                break;

            case DefinitionRecord d:
                cells[i++] = Cell.Of(d.TsEvent);
                cells[i++] = Cell.Of(symbol);
                cells[i++] = Cell.Of(d.RawSymbol);
                cells[i++] = Cell.Of(d.InstrumentId);
                cells[i++] = Cell.Of(d.Asset);
                cells[i++] = Cell.Of(d.InstrumentClass.ToString());
                cells[i++] = Cell.Of(d.TickSize);
                cells[i++] = d.Activation is 0 or DbnTime.Undefined ? Cell.Empty : Cell.Of(d.Activation);
                cells[i++] = d.Expiration is 0 or DbnTime.Undefined ? Cell.Empty : Cell.Of(d.Expiration);
                break;

            default:
                cells[i++] = Cell.Of(record.TsEvent);
                cells[i++] = Cell.Of(symbol);
                cells[i++] = Cell.Of(record.InstrumentId);
                cells[i++] = Cell.Of(record.RType);
                break;
        }

        if (series is not null)
            cells[i] = Cell.Of(series);
    }

    /// <summary>The time a record is filed under: when Databento received it for <c>mbo</c>, else its event time.</summary>
    public static ulong IndexTime(DbnRecord record) => record is MboRecord m ? m.TsRecv : record.TsEvent;

    private static IReadOnlyList<Column> Add(Column[] columns, bool series) =>
        series ? [.. columns, Text("series")] : columns;

    private static Column Time(string name) => new(name, CellKind.Time);
    private static Column Price(string name) => new(name, CellKind.Price);
    private static Column Integer(string name) => new(name, CellKind.Integer);
    private static Column Text(string name) => new(name, CellKind.Text);
}
