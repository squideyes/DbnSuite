namespace DbnSync.UnitTests.ClientTests;

/// <summary>Bytes for testing zstd readers: skippable frames, junk that is not zstd, and data that will not compress.</summary>
internal static class ZstdFrames
{
    private const byte JunkByte = 0x01;

    /// <summary>A skippable frame (magic 0x184D2A50) holding four bytes, which a reader must pass over.</summary>
    public static readonly byte[] Skippable = [0x50, 0x2A, 0x4D, 0x18, 4, 0, 0, 0, 9, 9, 9, 9];

    /// <summary>Bytes that start neither a zstd frame nor a skippable one.</summary>
    public static byte[] Junk(int length) => Enumerable.Repeat(JunkByte, length).ToArray();

    /// <summary>Random bytes, so they compress to about their own size.</summary>
    public static byte[] Noise(int length, int seed)
    {
        var bytes = new byte[length];

        new Random(seed).NextBytes(bytes);

        return bytes;
    }
}
