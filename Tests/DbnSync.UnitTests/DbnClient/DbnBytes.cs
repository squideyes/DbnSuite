using System.Buffers.Binary;
using System.Text;
using ZstdSharp;

namespace DbnSync.UnitTests.ClientTests;

/// <summary>Builds DBN version 3 streams byte by byte, the way Databento lays them out.</summary>
internal static class DbnBytes
{
    public const byte Version = 3;
    public const ushort MboSchema = 0;
    public const int SymbolWidth = 71;
    public const ulong Start = 1_793_491_200_000_000_000;

    private const int FixedSize = 100;
    private const byte Continuous = 3;
    private const byte InstrumentId = 0;
    private const uint IntervalStart = 20261101;
    private const uint IntervalEnd = 20261102;

    public static byte[] Metadata(string dataset, IReadOnlyList<string> requested,
        IReadOnlyDictionary<string, uint> mappings)
    {
        var block = new List<byte>();
        var fixedPart = new byte[FixedSize];

        Encoding.ASCII.GetBytes(dataset).CopyTo(fixedPart, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(fixedPart.AsSpan(16), MboSchema);
        BinaryPrimitives.WriteUInt64LittleEndian(fixedPart.AsSpan(18), Start);
        BinaryPrimitives.WriteUInt64LittleEndian(fixedPart.AsSpan(26), Start + 86_400_000_000_000);
        fixedPart[42] = Continuous;
        fixedPart[43] = InstrumentId;
        BinaryPrimitives.WriteUInt16LittleEndian(fixedPart.AsSpan(45), SymbolWidth);

        block.AddRange(fixedPart);
        block.AddRange(UInt32(0));
        block.AddRange(Strings(requested));
        block.AddRange(Strings([]));
        block.AddRange(Strings([]));
        block.AddRange(UInt32((uint)mappings.Count));

        foreach (var (raw, id) in mappings)
        {
            block.AddRange(Symbol(raw));
            block.AddRange(UInt32(1));
            block.AddRange(UInt32(IntervalStart));
            block.AddRange(UInt32(IntervalEnd));
            block.AddRange(Symbol(id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        return [.. "DBN"u8, Version, .. UInt32((uint)block.Count), .. block];
    }

    public static byte[] Mbo(uint instrument, ulong tsEvent, long price, uint quantity, char action, char side,
        ulong orderId)
    {
        var record = Header(0xA0, 56, instrument, tsEvent);

        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(16), orderId);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(24), price);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(32), quantity);
        record[38] = (byte)action;
        record[39] = (byte)side;

        return record;
    }

    /// <summary>An <c>mbo</c> record with every field the library reads, the time it was received among them.</summary>
    public static byte[] Mbo(uint instrument, ulong tsEvent, ulong tsRecv, long price, uint quantity, char action, char side,
        ulong orderId, byte flags, byte channel = 0, int tsInDelta = 0, uint sequence = 0)
    {
        var record = Mbo(instrument, tsEvent, price, quantity, action, side, orderId);

        record[36] = flags;
        record[37] = channel;
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(40), tsRecv);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(48), tsInDelta);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(52), sequence);

        return record;
    }

    /// <summary>A version 3 <c>statistics</c> record.</summary>
    public static byte[] Statistic(uint instrument, ulong tsEvent, ulong tsRef, long price, long quantity, ushort statType,
        byte updateAction = 1)
    {
        var record = Header(0x18, 80, instrument, tsEvent);

        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(24), tsRef);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(32), price);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(40), quantity);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(56), statType);
        record[60] = updateAction;

        return record;
    }

    /// <summary>A <c>status</c> record.</summary>
    public static byte[] Status(uint instrument, ulong tsEvent, ushort action, ushort reason, ushort tradingEvent, char isTrading)
    {
        var record = Header(0x12, 40, instrument, tsEvent);

        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(24), action);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(26), reason);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(28), tradingEvent);
        record[30] = (byte)isTrading;

        return record;
    }

    /// <summary>A DBN time from a UTC clock reading, worked out without the library's own time code.</summary>
    public static ulong Utc(int year, int month, int day, int hour, int minute = 0, int second = 0) =>
        (ulong)(new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero) - DateTimeOffset.UnixEpoch).Ticks * 100;

    /// <summary>A version 3 <c>definition</c> with the fields DbnSync reads; the rest are zeros.</summary>
    public static byte[] Definition(uint instrument, ulong tsEvent, string rawSymbol, string asset, char instrumentClass,
        long tickSize, ulong activation, ulong expiration, ushort maturityYear, byte maturityMonth)
    {
        var record = Header(0x13, 520, instrument, tsEvent);

        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(24), tickSize);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(40), expiration);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(48), activation);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(214), maturityYear);
        Encoding.ASCII.GetBytes(rawSymbol).CopyTo(record, 238);
        Encoding.ASCII.GetBytes(asset).CopyTo(record, 335);
        record[487] = (byte)instrumentClass;
        record[494] = maturityMonth;

        return record;
    }

    /// <summary>A 1-second bar; prices in units of 1e-9.</summary>
    public static byte[] Ohlcv(uint instrument, ulong tsEvent, long open, long high, long low, long close, ulong volume,
        byte rtype = 0x20)
    {
        var record = Header(rtype, 56, instrument, tsEvent);

        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(16), open);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(24), high);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(32), low);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(40), close);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(48), volume);

        return record;
    }

    /// <summary>A record of a type the reader does not decode: a header and zeros.</summary>
    public static byte[] Unknown(byte rtype, int length, uint instrument, ulong tsEvent) =>
        Header(rtype, length, instrument, tsEvent);

    public static byte[] Compress(params byte[][] parts)
    {
        using var compressor = new Compressor();

        return compressor.Wrap(parts.SelectMany(p => p).ToArray()).ToArray();
    }

    private static byte[] Header(byte rtype, int length, uint instrument, ulong tsEvent)
    {
        var record = new byte[length];

        record[0] = (byte)(length / 4);
        record[1] = rtype;
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4), instrument);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8), tsEvent);

        return record;
    }

    private static byte[] Strings(IReadOnlyList<string> strings) =>
        [.. UInt32((uint)strings.Count), .. strings.SelectMany(Symbol)];

    private static byte[] Symbol(string symbol)
    {
        var field = new byte[SymbolWidth];

        Encoding.ASCII.GetBytes(symbol).CopyTo(field, 0);

        return field;
    }

    private static byte[] UInt32(uint value)
    {
        var bytes = new byte[4];

        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);

        return bytes;
    }
}
