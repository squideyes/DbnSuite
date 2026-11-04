using System.Buffers.Binary;
using DbnClient.Archive;
using DbnClient.Dbn;
using DbnSync.UnitTests.ClientTests;
using static DbnSync.UnitTests.TestData;

namespace DbnSync.UnitTests.FetchTests;

/// <summary>
/// An archive folder of generated DBN files, laid out as the worker writes
/// them. Nothing here comes from Databento: every record is made up.
/// </summary>
internal sealed class SampleArchive(string folder)
{
    public const string Ohlcv = "ohlcv-1s";
    public const string Statistics = "statistics";
    public const string Status = "status";

    public const uint FrontId = 1001;
    public const uint NextId = 1002;
    public const uint SpreadId = 1003;

    public const string Front = "MNQZ26";
    public const string FrontRaw = "MNQZ6";
    public const string Next = "MNQH27";
    public const string NextRaw = "MNQH7";
    public const string Spread = "MNQZ26-MNQH27";
    public const string SpreadRaw = "MNQZ6-MNQH7";

    public const ushort Settlement = 3;
    public const ushort OpenInterest = 9;

    public const long QuarterPoint = 250_000_000;

    private const long PriceScale = 1_000_000_000;
    private const int MboRecvOffset = 40;
    private const int MboFlagsOffset = 36;
    private const int SchemaOffset = 24;
    private const int StatPriceOffset = 32;
    private const int StatQuantityOffset = 40;
    private const int StatTypeOffset = 56;
    private const int StatSize = 80;
    private const int StatusSize = 40;
    private const int StatusActionOffset = 24;
    private const int StatusTradingOffset = 30;

    public static readonly DateOnly FrontExpiry = new(2026, 12, 18);
    public static readonly DateOnly NextExpiry = new(2027, 3, 19);

    private static readonly ulong Listed = At(new DateOnly(2026, 1, 2));

    /// <summary>A moment of a UTC day, as DBN writes it.</summary>
    public static ulong At(DateOnly day, int hour = 0, int minute = 0, int second = 0) =>
        (ulong)(new DateTimeOffset(day, new TimeOnly(hour, minute, second), TimeSpan.Zero) - DateTimeOffset.UnixEpoch).Ticks * 100;

    public static long Price(decimal price) => (long)(price * PriceScale);

    public static byte[] FrontDefinition(string root = Root) => Contract(FrontId, FrontRaw, root, FrontExpiry);

    public static byte[] NextDefinition(string root = Root) => Contract(NextId, NextRaw, root, NextExpiry);

    public static byte[] SpreadDefinition(string root = Root) =>
        DbnBytes.Definition(SpreadId, Listed, SpreadRaw, root, DefinitionRecord.Spread, QuarterPoint, Listed, At(FrontExpiry),
            (ushort)FrontExpiry.Year, (byte)FrontExpiry.Month);

    public static byte[] Contract(uint id, string raw, string root, DateOnly expiry) =>
        DbnBytes.Definition(id, Listed, raw, root, DefinitionRecord.Future, QuarterPoint, Listed, At(expiry),
            (ushort)expiry.Year, (byte)expiry.Month);

    /// <summary>A flat 1-second bar at one price.</summary>
    public static byte[] Bar(uint id, ulong time, decimal price, ulong volume = 10) =>
        Bar(id, time, price, price, price, price, volume);

    public static byte[] Bar(uint id, ulong time, decimal open, decimal high, decimal low, decimal close, ulong volume) =>
        DbnBytes.Ohlcv(id, time, Price(open), Price(high), Price(low), Price(close), volume);

    /// <summary>An mbo event, received the moment it happened.</summary>
    public static byte[] Order(uint id, ulong time, decimal price, uint size, char action, char side, ulong orderId,
        byte flags = MboRecord.LastFlag)
    {
        var record = DbnBytes.Mbo(id, time, Price(price), size, action, side, orderId);

        record[MboFlagsOffset] = flags;
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(MboRecvOffset), time);

        return record;
    }

    public static byte[] Stat(uint id, ulong time, ushort type, long price = DbnPrice.Undefined,
        long quantity = StatRecord.UndefinedQuantity)
    {
        var record = DbnBytes.Unknown(StatRecord.Type, StatSize, id, time);

        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(StatPriceOffset), price);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(StatQuantityOffset), quantity);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(StatTypeOffset), type);

        return record;
    }

    public static byte[] StatusChange(uint id, ulong time, ushort action, char trading)
    {
        var record = DbnBytes.Unknown(StatusRecord.Type, StatusSize, id, time);

        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(StatusActionOffset), action);
        record[StatusTradingOffset] = (byte)trading;

        return record;
    }

    /// <summary>A day's file of one schema of <see cref="TestData.Root"/>.</summary>
    public string Add(string schema, DateOnly day, params byte[][] records) => Add(schema, Root, day, records);

    public string Add(string schema, string root, DateOnly day, params byte[][] records) =>
        Add(schema, root, day, new Dictionary<string, uint>(), records);

    /// <param name="mappings">What the file says each instrument was requested as.</param>
    public string Add(string schema, string root, DateOnly day, IReadOnlyDictionary<string, uint> mappings,
        params byte[][] records)
    {
        var metadata = DbnBytes.Metadata(Dataset, [$"{root}.FUT"], mappings);

        metadata[SchemaOffset] = (byte)DbnMetadata.Schemas.ToList().IndexOf(schema);

        return Write(DbnSyncLayout.Data(Dataset, schema, root, day), DbnBytes.Compress([metadata, .. records]));
    }

    /// <summary>The marker for a day that was asked for and had nothing.</summary>
    public string AddEmpty(string schema, DateOnly day, string root = Root) =>
        Write(DbnSyncLayout.Empty(Dataset, schema, root, day), []);

    /// <summary>The day's definitions: both outrights and their spread.</summary>
    public string AddDefinitions(DateOnly day) =>
        Add(Definition, day, FrontDefinition(), NextDefinition(), SpreadDefinition());

    public string Write(string key, byte[] bytes)
    {
        var path = PathOf(key);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);

        return path;
    }

    public string PathOf(string key) => Path.Combine(folder, key.Replace('/', Path.DirectorySeparatorChar));
}
