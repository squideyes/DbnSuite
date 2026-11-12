using System.Buffers;
using System.IO.Compression;

namespace DbnClient;

/// <summary>Zstandard, from .NET itself, at the level everything DbnClient writes uses.</summary>
internal static class Zstd
{
    /// <summary>zstd's own default: fast, and close to the best size for market data.</summary>
    public const int Level = 3;

    /// <summary>Compresses a whole buffer as one zstd frame.</summary>
    public static byte[] Compress(ReadOnlySpan<byte> plain)
    {
        var packed = GC.AllocateUninitializedArray<byte>((int)ZstandardEncoder.GetMaxCompressedLength(plain.Length));

        if (!ZstandardEncoder.TryCompress(plain, packed, out var written, Level, ZstandardCompressionOptions.DefaultWindowLog2))
            throw new InvalidOperationException("zstd could not compress within its own worst-case size.");

        return packed.AsSpan(0, written).ToArray();
    }

    /// <summary>A stream that compresses what is written to it into <paramref name="file"/>, which it closes.</summary>
    public static Stream Compressing(Stream file) =>
        new ZstandardStream(file, new ZstandardCompressionOptions { Quality = Level }, leaveOpen: false);

    /// <summary>A stream that reads <paramref name="packed"/> decompressed, strictly (see <see cref="ZstdReadStream"/>), and closes it.</summary>
    public static Stream Decompressing(Stream packed) => new ZstdReadStream(packed);

    /// <summary>
    /// Decompresses every frame of <paramref name="packed"/> into <paramref name="plain"/>.
    /// Returns false when it would not fit. Throws <see cref="InvalidDataException"/> when
    /// <paramref name="packed"/> is not wholly zstd: damaged, cut short, or followed by other bytes.
    /// </summary>
    public static bool TryDecompress(ReadOnlySpan<byte> packed, Span<byte> plain, out int written)
    {
        using var decoder = new ZstandardDecoder();
        Span<byte> overflow = stackalloc byte[1];
        var inFrame = false;

        written = 0;

        while (true)
        {
            if (packed.IsEmpty && !inFrame)
                return true;

            // Once plain is full, decoding goes on into a byte to spare, so that what is left
            // is still checked: junk is damage, and only more output means it would not fit.
            var full = written == plain.Length;
            var status = decoder.Decompress(packed, full ? overflow : plain[written..], out var consumed, out var produced);

            if (full && produced > 0)
                return false;

            packed = packed[consumed..];
            written += produced;

            if (consumed > 0)
                inFrame = true;

            switch (status)
            {
                case OperationStatus.Done:
                    decoder.Reset();
                    inFrame = false;
                    break;

                case OperationStatus.DestinationTooSmall:
                    break;

                case OperationStatus.NeedMoreData:
                    throw new InvalidDataException("The zstd data ends partway through a frame.");

                default:
                    throw new InvalidDataException("The data is not zstd, is damaged, or is followed by bytes that are not zstd.");
            }
        }
    }
}
