using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace DbnClient.Dbn;

/// <summary>
/// The block a DBN file opens with: what was asked for, and which instrument
/// ids the request resolved to. Every record names its instrument only by id,
/// so <see cref="Symbols"/> is how a record finds its name.
/// </summary>
public sealed record DbnMetadata(
    byte Version,
    string Dataset,
    string Schema,
    DateTimeOffset Start,
    DateTimeOffset End,
    IReadOnlyList<string> Requested,
    IReadOnlyDictionary<uint, string> Symbols)
{
    private const byte SupportedVersion = 3;

    /// <summary>The metadata exactly as stored, prefix included, for writing a filtered copy of the file.</summary>
    public byte[] Raw { get; init; } = [];

    /// <summary>"DBN", a version byte, then the length of everything else.</summary>
    public const int PrefixSize = 8;

    /// <summary>Dataset to reserved padding, the same size in every version from 2.</summary>
    private const int FixedSize = 100;

    /// <summary>The <c>stype_out</c> that makes each mapping's symbol an instrument id.</summary>
    private const byte InstrumentIdSymbology = 0;

    /// <summary>Every schema name, in the order DBN numbers them.</summary>
    public static IReadOnlyList<string> Schemas => SchemaNames;

    private static readonly string[] SchemaNames =
    [
        "mbo", "mbp-1", "mbp-10", "tbbo", "trades", "ohlcv-1s", "ohlcv-1m", "ohlcv-1h", "ohlcv-1d",
        "definition", "statistics", "status", "imbalance", "ohlcv-eod", "cmbp-1", "cbbo-1s",
        "cbbo-1m", "tcbbo", "bbo-1s", "bbo-1m"
    ];

    /// <summary>The symbol a record's instrument was requested as, or its id when unmapped.</summary>
    public string SymbolOf(uint instrumentId) =>
        Symbols.TryGetValue(instrumentId, out var symbol)
            ? symbol
            : instrumentId.ToString(CultureInfo.InvariantCulture);

    /// <summary>Reads the metadata block, leaving the stream at the first record.</summary>
    public static async Task<DbnMetadata> ReadAsync(Stream stream, CancellationToken cancel)
    {
        var prefix = new byte[PrefixSize];

        await stream.ReadExactlyAsync(prefix, cancel).ConfigureAwait(false);

        if (!prefix.AsSpan(0, 3).SequenceEqual("DBN"u8))
            throw new InvalidDataException("Not a DBN stream: it does not start with \"DBN\".");

        var version = prefix[3];

        // Records are decoded as version 3 lays them out; any other's would read silently wrong.
        if (version != SupportedVersion)
            throw new InvalidDataException($"DBN version {version} is not supported; this reads version {SupportedVersion}.");

        var length = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(4));

        if (length is < FixedSize or > 64 * 1024 * 1024)
            throw new InvalidDataException($"The DBN metadata claims {length:N0} bytes, which is implausible.");

        var block = new byte[length];

        await stream.ReadExactlyAsync(block, cancel).ConfigureAwait(false);

        try
        {
            return Parse(version, block) with { Raw = [.. prefix, .. block] };
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new InvalidDataException("The DBN metadata ends before what it says it holds.");
        }
    }

    private static DbnMetadata Parse(byte version, ReadOnlySpan<byte> block)
    {
        var schema = BinaryPrimitives.ReadUInt16LittleEndian(block[16..]);
        var symbology = block[43];
        var width = BinaryPrimitives.ReadUInt16LittleEndian(block[45..]);

        var at = FixedSize;
        at += 4 + (int)BinaryPrimitives.ReadUInt32LittleEndian(block[at..]);

        var requested = Strings(block, ref at, width);

        Strings(block, ref at, width);
        Strings(block, ref at, width);

        var symbols = new Dictionary<uint, string>();
        var mappings = Count(block, ref at);

        for (var i = 0; i < mappings; i++)
        {
            var raw = Text(block.Slice(at, width));
            at += width;

            var intervals = Count(block, ref at);

            for (var j = 0; j < intervals; j++)
            {
                // Start and end dates (8 bytes) come before the mapped symbol.
                var mapped = Text(block.Slice(at + 8, width));
                at += 8 + width;

                if (symbology == InstrumentIdSymbology
                    && uint.TryParse(mapped, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                {
                    symbols[id] = raw;
                }
            }
        }

        return new DbnMetadata(
            version,
            Text(block[..16]),
            schema < SchemaNames.Length ? SchemaNames[schema] : "mixed",
            DbnTime.ToTime(BinaryPrimitives.ReadUInt64LittleEndian(block[18..])),
            DbnTime.ToTime(BinaryPrimitives.ReadUInt64LittleEndian(block[26..])),
            requested,
            symbols);
    }

    private static int Count(ReadOnlySpan<byte> block, ref int at)
    {
        var count = BinaryPrimitives.ReadUInt32LittleEndian(block[at..]);
        at += 4;

        // Each entry takes at least a byte, so a larger count can only be damage.
        return count <= block.Length
            ? (int)count
            : throw new InvalidDataException($"The DBN metadata claims {count:N0} entries, which is implausible.");
    }

    private static List<string> Strings(ReadOnlySpan<byte> block, ref int at, int width)
    {
        var count = Count(block, ref at);
        var strings = new List<string>(count);

        for (var i = 0; i < count; i++, at += width)
            strings.Add(Text(block.Slice(at, width)));

        return strings;
    }

    /// <summary>A fixed-width, NUL-padded ASCII field.</summary>
    internal static string Text(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);

        return Encoding.ASCII.GetString(end < 0 ? field : field[..end]);
    }
}
