using System.Buffers.Binary;
using System.Globalization;
using DbnSuite.Client.Dbn;
using DbnSuite.UnitTests.ClientTests;

namespace DbnSuite.IntegrationTests.Support;

/// <summary>Generates the day files a batch job delivers. None of it is market data.</summary>
public static class BatchFactory
{
    public const uint FrontId = 101;
    public const uint BackId = 102;
    public const uint SpreadId = 103;

    /// <summary>Trades written per day of mbo, and bars for the front month of ohlcv-1s.</summary>
    public const int TradesPerDay = 2;
    public const int BarsPerDay = 3;

    public const decimal OpenPrice = 21000m;

    private const long Nano = 1_000_000_000;
    private const ulong Second = 1_000_000_000;
    private const long Tick = Nano / 4;
    private const ulong Expires = 1_797_000_000_000_000_000;
    private const int MboReceivedOffset = 40;
    private const byte StatusType = 0x12;
    private const byte StatisticsType = 0x18;
    private const int OpaqueSize = 80;
    private const string Status = "status";

    /// <summary>The session's middle, so a file's records sit inside its own trade date.</summary>
    private static readonly TimeOnly Midday = new(14, 30);

    public static string FileName(string schema, DateOnly day) =>
        string.Create(CultureInfo.InvariantCulture, $"glbx-mdp3-{day:yyyyMMdd}.{schema}.dbn.zst");

    /// <summary>The generated front month, as DbnClient names it.</summary>
    public static string FrontContract(string root) => $"{root}Z26";

    public static byte[] Day(string schema, string root, DateOnly day)
    {
        var metadata = DbnBytes.Metadata(Sample.Dataset, Requested(schema, root), Mappings(schema, root));
        var start = Start(day);

        return DbnBytes.Compress([metadata, .. schema switch
        {
            Sample.Mbo => Orders(start),
            Sample.Definition => Definitions(root, start),
            Sample.Ohlcv => Bars(start),
            _ => Opaque(schema, start)
        }]);
    }

    private static string[] Requested(string schema, string root) =>
        schema == Sample.Mbo ? [$"{root}.n.0", $"{root}.n.1"] : [$"{root}.FUT"];

    private static Dictionary<string, uint> Mappings(string schema, string root) => schema == Sample.Mbo
        ? new() { [$"{root}.n.0"] = FrontId, [$"{root}.n.1"] = BackId }
        : [];

    private static ulong Start(DateOnly day) =>
        (ulong)((new DateTimeOffset(day.ToDateTime(Midday), TimeSpan.Zero) - DateTimeOffset.UnixEpoch).Ticks * 100);

    private static byte[][] Orders(ulong start)
    {
        var bid = (long)(OpenPrice * Nano);
        var ask = bid + Tick;

        return
        [
            Order(FrontId, start, bid, 5, 'A', 'B', 1),
            Order(FrontId, start + Second, ask, 5, 'A', 'A', 2),
            Order(FrontId, start + 2 * Second, ask, 1, 'T', 'A', 3),
            Order(FrontId, start + 3 * Second, bid, 2, 'T', 'B', 4),
            Order(BackId, start + 4 * Second, bid, 3, 'A', 'B', 5)
        ];
    }

    private static byte[] Order(uint instrument, ulong at, long price, uint quantity, char action, char side, ulong id)
    {
        var record = DbnBytes.Mbo(instrument, at, price, quantity, action, side, id);

        // Exports file mbo under when it was received, which the shared builder leaves at zero.
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(MboReceivedOffset), at);

        return record;
    }

    private static byte[][] Bars(ulong start)
    {
        var open = (long)(OpenPrice * Nano);

        return
        [
            .. Enumerable.Range(0, BarsPerDay).Select(i => DbnBytes.Ohlcv(FrontId, start + (ulong)i * Second,
                open, open + Tick, open - Tick, open + Tick, 10)),
            DbnBytes.Ohlcv(SpreadId, start, Nano, Nano, Nano, Nano, 1)
        ];
    }

    private static byte[][] Definitions(string root, ulong start) =>
    [
        DbnBytes.Definition(FrontId, start, $"{root}Z6", root, DefinitionRecord.Future, Tick, 1, Expires, 2026, 12),
        DbnBytes.Definition(BackId, start, $"{root}H7", root, DefinitionRecord.Future, Tick, 1, Expires, 2027, 3),
        DbnBytes.Definition(SpreadId, start, $"{root}Z6-{root}H7", root, DefinitionRecord.Spread, Tick, 1, Expires, 2026, 12)
    ];

    private static byte[][] Opaque(string schema, ulong start) =>
        [DbnBytes.Unknown(schema == Status ? StatusType : StatisticsType, OpaqueSize, FrontId, start)];
}
