using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

namespace DbnClient.Tape;

/// <summary>The fixed 192 bytes at the front of every tape.</summary>
public sealed record TapeHeader
{
    /// <summary>Bytes in a header.</summary>
    public const int Size = 192;

    /// <summary>The only format version so far.</summary>
    public const byte CurrentVersion = 1;

    internal const uint MicrosecondUnit = 1_000;
    internal const uint SecondUnit = 1_000_000_000;

    private const byte EventClock = 1;
    private const byte ZstdFlag = 1;
    private const byte RthFlag = 2;
    private const int CrcOffset = 188;
    private const int DatasetLength = 16;
    private const int SymbolLength = 16;
    private const int RootLength = 8;

    private static ReadOnlySpan<byte> Magic => "TAPE"u8;

    public required TapeKind Kind { get; init; }

    public required string Dataset { get; init; }

    public required string Symbol { get; init; }

    public required string Root { get; init; }

    public required DateOnly TradeDate { get; init; }

    public required long TickRaw { get; init; }

    public required long OpenTime { get; init; }

    public required long CloseTime { get; init; }

    public required int BasePrice { get; init; }

    public required uint InstrumentId { get; init; }

    public required long EventCount { get; init; }

    public required long BodyLength { get; init; }

    public required long StoredLength { get; init; }

    public required ulong BodyHash { get; init; }

    public bool IsCompressed { get; init; }

    public bool HasRth { get; init; }

    public RthMark RthStart { get; init; }

    public long RthEndOffset { get; init; }

    public long RthEndIndex { get; init; }

    /// <summary>Nanoseconds per stored time unit.</summary>
    public uint TimeUnit => TimeUnitOf(Kind);

    internal static uint TimeUnitOf(TapeKind kind) => kind == TapeKind.S1 ? SecondUnit : MicrosecondUnit;

    /// <summary>Writes the header, with its CRC, into the first 192 bytes of the target.</summary>
    internal void Write(Span<byte> target)
    {
        target = target[..Size];
        target.Clear();

        Magic.CopyTo(target);
        target[4] = CurrentVersion;
        target[5] = (byte)Kind;
        target[6] = (byte)((IsCompressed ? ZstdFlag : 0) | (HasRth ? RthFlag : 0));
        target[7] = EventClock;

        BinaryPrimitives.WriteUInt32LittleEndian(target[8..], TimeUnit);
        BinaryPrimitives.WriteUInt32LittleEndian(target[12..], (uint)(TradeDate.Year * 10_000 + TradeDate.Month * 100 + TradeDate.Day));
        BinaryPrimitives.WriteInt64LittleEndian(target[16..], TickRaw);
        BinaryPrimitives.WriteInt64LittleEndian(target[24..], OpenTime);
        BinaryPrimitives.WriteInt64LittleEndian(target[32..], CloseTime);
        BinaryPrimitives.WriteInt32LittleEndian(target[40..], BasePrice);
        BinaryPrimitives.WriteUInt32LittleEndian(target[44..], InstrumentId);
        BinaryPrimitives.WriteInt64LittleEndian(target[48..], EventCount);
        BinaryPrimitives.WriteInt64LittleEndian(target[56..], BodyLength);
        BinaryPrimitives.WriteInt64LittleEndian(target[64..], StoredLength);
        BinaryPrimitives.WriteUInt64LittleEndian(target[72..], BodyHash);

        Encoding.ASCII.GetBytes(Dataset, target[80..]);
        Encoding.ASCII.GetBytes(Symbol, target[96..]);
        Encoding.ASCII.GetBytes(Root, target[112..]);

        if (HasRth)
        {
            WriteMark(target[120..], RthStart);
            BinaryPrimitives.WriteInt64LittleEndian(target[168..], RthEndOffset);
            BinaryPrimitives.WriteInt64LittleEndian(target[176..], RthEndIndex);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(target[CrcOffset..], Crc32.HashToUInt32(target[..CrcOffset]));
    }

    /// <summary>Reads and checks a header. Anything wrong is an <see cref="Error"/>, never an exception.</summary>
    internal static Result<TapeHeader> Parse(ReadOnlySpan<byte> source)
    {
        if (source.Length < Magic.Length || !source[..Magic.Length].SequenceEqual(Magic))
            return new Error(TapeErrors.NotATape, "Not a tape: the file does not start with TAPE.");

        if (source.Length < Size)
            return Corrupt("The file is shorter than a tape header.");

        if (source[4] != CurrentVersion)
            return new Error(TapeErrors.Unsupported, $"Tape version {source[4]} is not supported.");

        if (Crc32.HashToUInt32(source[..CrcOffset]) != BinaryPrimitives.ReadUInt32LittleEndian(source[CrcOffset..]))
            return Corrupt("The header's CRC does not match.");

        var kind = (TapeKind)source[5];

        if (kind is not (TapeKind.S1 or TapeKind.Trade or TapeKind.L1))
            return new Error(TapeErrors.Unsupported, $"Tape kind {source[5]} is not supported.");

        if (source[7] != EventClock)
            return new Error(TapeErrors.Unsupported, $"Tape clock {source[7]} is not supported.");

        var flags = source[6];

        if ((flags & ~(ZstdFlag | RthFlag)) != 0)
            return Corrupt("The header sets an unknown flag.");

        if (BinaryPrimitives.ReadUInt32LittleEndian(source[8..]) != TimeUnitOf(kind))
            return Corrupt("The header's time unit does not suit its kind.");

        if (!TryReadDate(BinaryPrimitives.ReadUInt32LittleEndian(source[12..]), out var tradeDate))
            return Corrupt("The header's trade date is not a date.");

        if (!TryReadText(source.Slice(80, DatasetLength), out var dataset)
            || !TryReadText(source.Slice(96, SymbolLength), out var symbol)
            || !TryReadText(source.Slice(112, RootLength), out var root))
        {
            return Corrupt("The header's names are not zero-padded ASCII.");
        }

        var header = new TapeHeader
        {
            Kind = kind,
            Dataset = dataset,
            Symbol = symbol,
            Root = root,
            TradeDate = tradeDate,
            TickRaw = BinaryPrimitives.ReadInt64LittleEndian(source[16..]),
            OpenTime = BinaryPrimitives.ReadInt64LittleEndian(source[24..]),
            CloseTime = BinaryPrimitives.ReadInt64LittleEndian(source[32..]),
            BasePrice = BinaryPrimitives.ReadInt32LittleEndian(source[40..]),
            InstrumentId = BinaryPrimitives.ReadUInt32LittleEndian(source[44..]),
            EventCount = BinaryPrimitives.ReadInt64LittleEndian(source[48..]),
            BodyLength = BinaryPrimitives.ReadInt64LittleEndian(source[56..]),
            StoredLength = BinaryPrimitives.ReadInt64LittleEndian(source[64..]),
            BodyHash = BinaryPrimitives.ReadUInt64LittleEndian(source[72..]),
            IsCompressed = (flags & ZstdFlag) != 0,
            HasRth = (flags & RthFlag) != 0,
            RthStart = ReadMark(source[120..]),
            RthEndOffset = BinaryPrimitives.ReadInt64LittleEndian(source[168..]),
            RthEndIndex = BinaryPrimitives.ReadInt64LittleEndian(source[176..])
        };

        if (header.TickRaw <= 0 || header.OpenTime < 0 || header.CloseTime < header.OpenTime)
            return Corrupt("The header's tick or session times make no sense.");

        if (header.EventCount <= 0 || header.BodyLength < header.EventCount || header.StoredLength <= 0)
            return Corrupt("The header's lengths make no sense.");

        if (header.BodyLength > Array.MaxLength || header.StoredLength > Array.MaxLength)
            return Corrupt("The header states a body too large to load.");

        if (!header.IsCompressed && header.StoredLength != header.BodyLength)
            return Corrupt("An uncompressed body must store exactly its own length.");

        if (BinaryPrimitives.ReadUInt32LittleEndian(source[184..]) != 0 || !source.Slice(165, 3).SequenceEqual(stackalloc byte[3]))
            return Corrupt("The header's reserved bytes are not zero.");

        if (header.HasRth ? !header.MarksFit() : source[120..184].ContainsAnyExcept((byte)0))
            return Corrupt("The header's RTH marks do not fit the body.");

        return header;
    }

    private bool MarksFit() =>
        RthStart.BodyOffset >= 0
        && RthStart.EventIndex >= 0
        && RthStart.BodyOffset <= RthEndOffset
        && RthStart.EventIndex <= RthEndIndex
        && RthEndOffset <= BodyLength
        && RthEndIndex <= EventCount;

    private static void WriteMark(Span<byte> target, RthMark mark)
    {
        BinaryPrimitives.WriteInt64LittleEndian(target, mark.BodyOffset);
        BinaryPrimitives.WriteInt64LittleEndian(target[8..], mark.EventIndex);
        BinaryPrimitives.WriteInt64LittleEndian(target[16..], mark.Time);
        BinaryPrimitives.WriteInt32LittleEndian(target[24..], mark.BidPrice);
        BinaryPrimitives.WriteInt32LittleEndian(target[28..], mark.AskPrice);
        BinaryPrimitives.WriteUInt32LittleEndian(target[32..], mark.BidSize);
        BinaryPrimitives.WriteUInt32LittleEndian(target[36..], mark.AskSize);
        BinaryPrimitives.WriteInt32LittleEndian(target[40..], mark.LastPrice);
        target[44] = mark.MarketState;
    }

    private static RthMark ReadMark(ReadOnlySpan<byte> source) => new()
    {
        BodyOffset = BinaryPrimitives.ReadInt64LittleEndian(source),
        EventIndex = BinaryPrimitives.ReadInt64LittleEndian(source[8..]),
        Time = BinaryPrimitives.ReadInt64LittleEndian(source[16..]),
        BidPrice = BinaryPrimitives.ReadInt32LittleEndian(source[24..]),
        AskPrice = BinaryPrimitives.ReadInt32LittleEndian(source[28..]),
        BidSize = BinaryPrimitives.ReadUInt32LittleEndian(source[32..]),
        AskSize = BinaryPrimitives.ReadUInt32LittleEndian(source[36..]),
        LastPrice = BinaryPrimitives.ReadInt32LittleEndian(source[40..]),
        MarketState = source[44]
    };

    private static bool TryReadDate(uint value, out DateOnly date)
    {
        var year = (int)(value / 10_000);
        var month = (int)(value / 100 % 100);
        var day = (int)(value % 100);

        date = default;

        if (year is < 1 or > 9999 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
            return false;

        date = new DateOnly(year, month, day);

        return true;
    }

    private static bool TryReadText(ReadOnlySpan<byte> source, out string text)
    {
        var length = source.IndexOf((byte)0);

        if (length < 0)
            length = source.Length;

        var value = source[..length];

        text = Encoding.ASCII.GetString(value);

        return length > 0
            && !value.ContainsAnyExceptInRange((byte)0x21, (byte)0x7E)
            && !source[length..].ContainsAnyExcept((byte)0);
    }

    internal static Error? CheckNames(TapeInfo info)
    {
        if (!Fits(info.Dataset, DatasetLength) || !Fits(info.Symbol, SymbolLength) || !Fits(info.Root, RootLength))
            return new Error(TapeErrors.Refused, "Dataset, symbol and root must be printable ASCII that fits the header.");

        return null;
    }

    private static bool Fits(string text, int length) =>
        text.Length > 0 && text.Length <= length && text.All(c => c is >= '!' and <= '~');

    private static Error Corrupt(string message) => new(TapeErrors.Corrupt, message);
}
