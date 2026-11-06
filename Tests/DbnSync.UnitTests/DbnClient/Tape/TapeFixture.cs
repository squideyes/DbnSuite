using DbnClient;
using DbnClient.Tape;
using Shouldly;

namespace DbnSync.UnitTests.TapeFormatTests;

/// <summary>Synthetic tape settings and helpers. No test here touches real data.</summary>
internal static class TapeFixture
{
    public const string Dataset = "GLBX.MDP3";
    public const string Symbol = "ESZ6";
    public const string Root = "ES";
    public const long TickRaw = 250_000_000;
    public const int BasePrice = 30_842;
    public const uint InstrumentId = 7_654_321;
    public const long NanosPerMicro = 1_000;
    public const long NanosPerSecond = 1_000_000_000;
    public const long MicrosPerSecond = 1_000_000;

    /// <summary>17:00 Chicago on 28 Oct 2026, and 16:00 the next day.</summary>
    public const long OpenSeconds = 1_793_224_800;
    public const long CloseSeconds = OpenSeconds + 23 * 3_600;

    public const long OpenMicros = OpenSeconds * MicrosPerSecond;
    public const long CloseMicros = CloseSeconds * MicrosPerSecond;

    public static readonly DateOnly TradeDate = new(2026, 10, 29);

    public static TapeInfo Info(bool compress = false, int basePrice = BasePrice) => new()
    {
        Dataset = Dataset,
        Symbol = Symbol,
        Root = Root,
        TradeDate = TradeDate,
        TickRaw = TickRaw,
        OpenTime = OpenSeconds * NanosPerSecond,
        CloseTime = CloseSeconds * NanosPerSecond,
        BasePrice = basePrice,
        InstrumentId = InstrumentId,
        Compress = compress
    };

    public static async Task<byte[]> ToBytesAsync(TapeWriter writer)
    {
        using var stream = new MemoryStream();

        var written = await writer.WriteAsync(stream, CancellationToken.None);

        written.IsSuccess.ShouldBeTrue(written.Error?.Message);

        return stream.ToArray();
    }

    public static async Task<Result<Tape>> LoadAsync(byte[] bytes, bool verifyHash = true)
    {
        using var stream = new MemoryStream(bytes);

        return await Tape.LoadAsync(stream, CancellationToken.None, verifyHash);
    }

    public static async Task<Tape> RoundTripAsync(TapeWriter writer)
    {
        var loaded = await LoadAsync(await ToBytesAsync(writer));

        loaded.IsSuccess.ShouldBeTrue(loaded.Error?.Message);

        return loaded.Value;
    }

    public static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes);

    public static byte[] Bytes(string hex) => Convert.FromHexString(hex);
}
