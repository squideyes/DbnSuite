using System.Buffers.Binary;
using System.Globalization;

namespace DbnClient.Dbn;

/// <summary>
/// One DBN record. Every record opens with the same 16-byte header: its own
/// length in 32-bit words, a type, the publisher, the instrument id, and the
/// exchange's event time in nanoseconds since the UNIX epoch.
/// </summary>
public abstract record DbnRecord(byte RType, ushort PublisherId, uint InstrumentId, ulong TsEvent)
{
    public const int HeaderSize = 16;

    /// <summary>A record's instrument id, read from its header without decoding it.</summary>
    public static uint InstrumentOf(ReadOnlySpan<byte> record) => BinaryPrimitives.ReadUInt32LittleEndian(record[4..]);

    /// <summary>
    /// The time a record is filed under, read without decoding it: when Databento received it for
    /// <c>mbo</c> and for a definition, else its event time. A day's definition file is a snapshot
    /// stamped at that day's midnight, while each definition's event time is when the exchange last
    /// changed it, often days before, so only the receive time says which day it belongs to.
    /// </summary>
    public static ulong IndexTimeOf(ReadOnlySpan<byte> record) =>
        BinaryPrimitives.ReadUInt64LittleEndian(record[1] switch
        {
            MboRecord.Type when record.Length >= MboRecord.Size => record[40..],
            DefinitionRecord.Type when record.Length >= DefinitionRecord.Size => record[ReceivedAt..],
            _ => record[8..]
        });

    /// <summary>Where a record that has one keeps its receive time, straight after the header.</summary>
    private const int ReceivedAt = HeaderSize;

    /// <summary>Decodes one whole record, its header included.</summary>
    public static DbnRecord Decode(ReadOnlySpan<byte> record)
    {
        var rtype = record[1];
        var publisher = BinaryPrimitives.ReadUInt16LittleEndian(record[2..]);
        var instrument = BinaryPrimitives.ReadUInt32LittleEndian(record[4..]);
        var tsEvent = BinaryPrimitives.ReadUInt64LittleEndian(record[8..]);

        return (rtype, record.Length) switch
        {
            (MboRecord.Type, MboRecord.Size) => MboRecord.Read(publisher, instrument, tsEvent, record),
            ( >= OhlcvRecord.FirstType and <= OhlcvRecord.LastType, OhlcvRecord.Size) =>
                OhlcvRecord.Read(rtype, publisher, instrument, tsEvent, record),
            (DefinitionRecord.Type, DefinitionRecord.Size) =>
                DefinitionRecord.Read(publisher, instrument, tsEvent, record),
            (StatRecord.Type, StatRecord.Size) => StatRecord.Read(publisher, instrument, tsEvent, record),
            (StatusRecord.Type, StatusRecord.Size) => StatusRecord.Read(publisher, instrument, tsEvent, record),
            _ => new OtherRecord(rtype, publisher, instrument, tsEvent, record.Length)
        };
    }
}

/// <summary>An <c>mbo</c> event: one order added, modified, cancelled, filled or traded.</summary>
public sealed record MboRecord(
    ushort PublisherId, uint InstrumentId, ulong TsEvent,
    ulong OrderId, decimal? Price, uint Quantity, byte Flags, char Action, char Side, ulong TsRecv,
    byte ChannelId = 0, int TsInDelta = 0, uint Sequence = 0)
    : DbnRecord(Type, PublisherId, InstrumentId, TsEvent)
{
    public const byte Type = 0xA0;
    public const int Size = 56;

    /// <summary>Marks the records of a book snapshot, as each day's file opens with.</summary>
    public const byte SnapshotFlag = 1 << 5;

    /// <summary>Marks the last record of an event: the book is consistent after it.</summary>
    public const byte LastFlag = 1 << 7;

    internal static MboRecord Read(ushort publisher, uint instrument, ulong tsEvent, ReadOnlySpan<byte> r) =>
        new(publisher, instrument, tsEvent,
            BinaryPrimitives.ReadUInt64LittleEndian(r[16..]),
            DbnPrice.From(BinaryPrimitives.ReadInt64LittleEndian(r[24..])),
            BinaryPrimitives.ReadUInt32LittleEndian(r[32..]),
            r[36],
            (char)r[38],
            (char)r[39],
            BinaryPrimitives.ReadUInt64LittleEndian(r[40..]),
            r[37],
            BinaryPrimitives.ReadInt32LittleEndian(r[48..]),
            BinaryPrimitives.ReadUInt32LittleEndian(r[52..]));
}

/// <summary>An <c>ohlcv-*</c> bar.</summary>
public sealed record OhlcvRecord(
    byte RType, ushort PublisherId, uint InstrumentId, ulong TsEvent,
    decimal? Open, decimal? High, decimal? Low, decimal? Close, ulong Volume)
    : DbnRecord(RType, PublisherId, InstrumentId, TsEvent)
{
    /// <summary>1-second bars; 1-minute, 1-hour, 1-day and end-of-day follow in order.</summary>
    public const byte FirstType = 0x20;
    public const byte LastType = 0x24;
    public const int Size = 56;

    internal static OhlcvRecord Read(byte rtype, ushort publisher, uint instrument, ulong tsEvent,
        ReadOnlySpan<byte> r) =>
        new(rtype, publisher, instrument, tsEvent,
            DbnPrice.From(BinaryPrimitives.ReadInt64LittleEndian(r[16..])),
            DbnPrice.From(BinaryPrimitives.ReadInt64LittleEndian(r[24..])),
            DbnPrice.From(BinaryPrimitives.ReadInt64LittleEndian(r[32..])),
            DbnPrice.From(BinaryPrimitives.ReadInt64LittleEndian(r[40..])),
            BinaryPrimitives.ReadUInt64LittleEndian(r[48..]));
}

/// <summary>
/// A <c>definition</c>: what an instrument id is. Only the fields DbnSync uses
/// are decoded; the record has dozens more. Laid out as DBN version 3, which is
/// what Databento delivers today.
/// </summary>
/// <param name="TickSize">The smallest price step, such as 0.25 for ES.</param>
/// <param name="Expiration">The last moment it trades, in DBN nanoseconds.</param>
/// <param name="Activation">The first moment it trades, in DBN nanoseconds.</param>
/// <param name="RawSymbol">The exchange's name, such as <c>ESZ6</c> or <c>ESZ6-ESH7</c>.</param>
/// <param name="Asset">The product it belongs to, such as <c>ES</c>.</param>
/// <param name="InstrumentClass"><c>F</c> for an outright future, <c>S</c> for a spread.</param>
/// <param name="MaturityYear">The contract's year in full, such as 2026.</param>
/// <param name="MaturityMonth">The contract's month, 1 to 12.</param>
public sealed record DefinitionRecord(
    ushort PublisherId, uint InstrumentId, ulong TsEvent,
    decimal? TickSize, ulong Expiration, ulong Activation, string RawSymbol, string Asset,
    char InstrumentClass, ushort MaturityYear, byte MaturityMonth)
    : DbnRecord(Type, PublisherId, InstrumentId, TsEvent)
{
    public const byte Type = 0x13;
    public const int Size = 520;

    public const char Future = 'F';
    public const char Spread = 'S';

    internal static DefinitionRecord Read(ushort publisher, uint instrument, ulong tsEvent, ReadOnlySpan<byte> r) =>
        new(publisher, instrument, tsEvent,
            DbnPrice.From(BinaryPrimitives.ReadInt64LittleEndian(r[24..])),
            BinaryPrimitives.ReadUInt64LittleEndian(r[40..]),
            BinaryPrimitives.ReadUInt64LittleEndian(r[48..]),
            DbnMetadata.Text(r.Slice(238, 71)),
            DbnMetadata.Text(r.Slice(335, 11)),
            (char)r[487],
            BinaryPrimitives.ReadUInt16LittleEndian(r[214..]),
            r[494]);
}

/// <summary>
/// A <c>statistics</c> value: a settlement price, open interest, cleared volume
/// and so on, told apart by <see cref="StatType"/>. Laid out as DBN version 3.
/// </summary>
public sealed record StatRecord(
    ushort PublisherId, uint InstrumentId, ulong TsEvent,
    ulong TsRef, decimal? Price, long Quantity, ushort StatType, byte UpdateAction)
    : DbnRecord(Type, PublisherId, InstrumentId, TsEvent)
{
    public const byte Type = 0x18;
    public const int Size = 80;

    /// <summary>The 64-bit "no quantity" sentinel.</summary>
    public const long UndefinedQuantity = long.MaxValue;

    internal static StatRecord Read(ushort publisher, uint instrument, ulong tsEvent, ReadOnlySpan<byte> r) =>
        new(publisher, instrument, tsEvent,
            BinaryPrimitives.ReadUInt64LittleEndian(r[24..]),
            DbnPrice.From(BinaryPrimitives.ReadInt64LittleEndian(r[32..])),
            BinaryPrimitives.ReadInt64LittleEndian(r[40..]),
            BinaryPrimitives.ReadUInt16LittleEndian(r[56..]),
            r[60]);
}

/// <summary>A <c>status</c> change: a halt, a pause, the open or the close.</summary>
public sealed record StatusRecord(
    ushort PublisherId, uint InstrumentId, ulong TsEvent,
    ushort Action, ushort Reason, ushort TradingEvent, char IsTrading)
    : DbnRecord(Type, PublisherId, InstrumentId, TsEvent)
{
    public const byte Type = 0x12;
    public const int Size = 40;

    internal static StatusRecord Read(ushort publisher, uint instrument, ulong tsEvent, ReadOnlySpan<byte> r) =>
        new(publisher, instrument, tsEvent,
            BinaryPrimitives.ReadUInt16LittleEndian(r[24..]),
            BinaryPrimitives.ReadUInt16LittleEndian(r[26..]),
            BinaryPrimitives.ReadUInt16LittleEndian(r[28..]),
            (char)r[30]);
}

/// <summary>Any record this demo does not decode: its header and size only.</summary>
public sealed record OtherRecord(byte RType, ushort PublisherId, uint InstrumentId, ulong TsEvent, int Length)
    : DbnRecord(RType, PublisherId, InstrumentId, TsEvent);

/// <summary>DBN prices are fixed point: one unit is 1e-9.</summary>
public static class DbnPrice
{
    /// <summary>The "no price" sentinel.</summary>
    public const long Undefined = long.MaxValue;

    public static decimal? From(long raw) => raw == Undefined ? null : raw / 1_000_000_000m;
}

/// <summary>DBN times are nanoseconds since the UNIX epoch, UTC.</summary>
public static class DbnTime
{
    /// <summary>The "no time" sentinel.</summary>
    public const ulong Undefined = ulong.MaxValue;

    /// <summary>As a <see cref="DateTimeOffset"/>, which keeps 100 ns of the 1 ns precision.</summary>
    public static DateTimeOffset ToTime(ulong nanos) =>
        DateTimeOffset.UnixEpoch.AddTicks((long)(nanos / 100));

    /// <summary>ISO 8601 with all nine fractional digits.</summary>
    public static string Format(ulong nanos) =>
        nanos == Undefined
            ? "-"
            : string.Create(CultureInfo.InvariantCulture,
                $"{DateTimeOffset.UnixEpoch.AddSeconds(nanos / 1_000_000_000):yyyy-MM-ddTHH:mm:ss}.{nanos % 1_000_000_000:D9}Z");
}
