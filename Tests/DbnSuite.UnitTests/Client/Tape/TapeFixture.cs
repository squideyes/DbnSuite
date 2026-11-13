using DbnSuite.Client;
using DbnSuite.Client.Tape;
using Shouldly;

namespace DbnSuite.UnitTests.TapeFormatTests;

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

    /// <summary>A full ETH session: 17:00 Chicago the evening before, to 16:00 on the date.</summary>
    public static CatalogSession FullSession(DateOnly tradeDate) =>
        new(Nanos(DbnSuite.Client.Time.Sessions.SessionStart(tradeDate)), Nanos(DbnSuite.Client.Time.Sessions.SessionEnd(tradeDate)), []);

    /// <summary>A catalog day with one full ETH session, so it is a day tapes are made for.</summary>
    public static CatalogDay FullDay(DateOnly tradeDate, IReadOnlyList<CatalogEntry>? contracts = null) =>
        new() { TradeDate = tradeDate, Sessions = [FullSession(tradeDate)], Contracts = contracts ?? [] };

    /// <summary>A catalog day whose session closed at 12:00, as on a holiday, so it has no tapes.</summary>
    public static CatalogDay ShortDay(DateOnly tradeDate) => FullDay(tradeDate) with
    {
        Sessions = [FullSession(tradeDate) with { Close = Nanos(DbnSuite.Client.Time.Sessions.SessionEnd(tradeDate).AddHours(-4)) }]
    };

    /// <summary>A catalog's JSON, as a store holds it.</summary>
    public static async Task<byte[]> JsonOfAsync(Catalog catalog)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"catalog-{Guid.NewGuid():N}");

        try
        {
            var saved = await catalog.SaveAsync(folder, CancellationToken.None);

            return await File.ReadAllBytesAsync(saved.Value);
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    public static long Nanos(DateTimeOffset time) => (time - DateTimeOffset.UnixEpoch).Ticks * 100;

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
