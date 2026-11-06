using DbnClient.Tape;
using Shouldly;

namespace DbnSync.UnitTests.TapeFormatTests;

/// <summary>
/// These bytes pin format version 1. The bodies were worked out by hand from the
/// design's rules, and include its worked examples. If one fails, the format changed.
/// </summary>
public sealed class GoldenTapeTests
{
    /// <summary>
    /// B4 02 00: the design's first bar. 20: one lot at an unchanged price.
    /// 0F 02 04 01 00 64: a four-second gap, open +2, close -1, high 1 above, volume 100.
    /// </summary>
    private const string S1Body = "B40200200F0204010064";

    /// <summary>
    /// 3D E2 09 02: the design's trade. 02 14: the seller, 20 lots, same time and price.
    /// FC 01 01: no side, 15 lots, one microsecond later, one tick down.
    /// </summary>
    private const string TradeBody = "3DE209020214FC0101";

    /// <summary>
    /// 00 07: trading, at the open. 38 02 50 19: the opening quote.
    /// 57 A0 06 and 20 03: the design's trade and quote. 22 01 2D: the offer empties.
    /// </summary>
    private const string L1Body = "000738025019" + "57A006" + "2003" + "22012D";

    private const string S1Header = "544150450101000100CA9A3BA528350180B2E60E0000000000C06B6674D0E218002002C7C21BE3187A780000B1CB740003000000000000000A000000000000000A00000000000000B57255FFFCDE834D474C42582E4D4450330000000000000045535A36000000000000000000000000455300000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000005F677AC5";
    private const string TradeHeader = "5441504501020001E8030000A528350180B2E60E0000000000C06B6674D0E218002002C7C21BE3187A780000B1CB74000300000000000000090000000000000009000000000000009ECDD02F7D044531474C42582E4D4450330000000000000045535A3600000000000000000000000045530000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000237C6590";
    private const string L1Header = "5441504501030001E8030000A528350180B2E60E0000000000C06B6674D0E218002002C7C21BE3187A780000B1CB740005000000000000000E000000000000000E0000000000000045A0C66FCC383395474C42582E4D4450330000000000000045535A360000000000000000000000004553000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000035AED9EB";

    [Fact]
    public async Task AnS1TapeMatchesItsBytes()
    {
        var writer = new S1Writer(TapeFixture.Info());
        var open = TapeFixture.OpenMicros;

        writer.Add(open, 30_842, 30_843, 30_841, 30_843, 5);
        writer.Add(open + TapeFixture.MicrosPerSecond, 30_843, 30_843, 30_843, 30_843, 1);
        writer.Add(open + 5 * TapeFixture.MicrosPerSecond, 30_845, 30_846, 30_844, 30_844, 100);

        var bytes = await TapeFixture.ToBytesAsync(writer);

        TapeFixture.Hex(bytes.AsSpan(TapeHeader.Size)).ShouldBe(S1Body);
        TapeFixture.Hex(bytes.AsSpan(0, TapeHeader.Size)).ShouldBe(S1Header);
    }

    [Fact]
    public async Task ATradeTapeMatchesItsBytes()
    {
        var writer = new TradeWriter(TapeFixture.Info());
        var time = TapeFixture.OpenMicros + 1_250;

        writer.Add(time, 30_843, 3, TradeSide.Buyer);
        writer.Add(time, 30_843, 20, TradeSide.Seller);
        writer.Add(time + 1, 30_842, 15, TradeSide.None);

        var bytes = await TapeFixture.ToBytesAsync(writer);

        TapeFixture.Hex(bytes.AsSpan(TapeHeader.Size)).ShouldBe(TradeBody);
        TapeFixture.Hex(bytes.AsSpan(0, TapeHeader.Size)).ShouldBe(TradeHeader);
    }

    [Fact]
    public async Task AnL1TapeMatchesItsBytes()
    {
        var writer = new L1Writer(TapeFixture.Info());
        var open = TapeFixture.OpenMicros;

        writer.State(open, 7);
        writer.Quote(open, 30_842, 40, 30_843, 25);
        writer.Trade(open + 800, 30_843, 2, TradeSide.Buyer);
        writer.Quote(open + 800, 30_842, 40, 30_843, 23);
        writer.Quote(open + 801, 30_842, 40, 30_843, 0);

        var bytes = await TapeFixture.ToBytesAsync(writer);

        TapeFixture.Hex(bytes.AsSpan(TapeHeader.Size)).ShouldBe(L1Body);
        TapeFixture.Hex(bytes.AsSpan(0, TapeHeader.Size)).ShouldBe(L1Header);
    }

    [Fact]
    public async Task TheFixedBytesReadBack()
    {
        var bytes = TapeFixture.Bytes(L1Header + L1Body);

        var loaded = await TapeFixture.LoadAsync(bytes);
        var steps = L1CursorTests.Read(loaded.Value);

        steps.Count.ShouldBe(5);
        steps[^1].AskSize.ShouldBe(0u);
        steps[^1].AskPrice.ShouldBe(30_843);
        steps[2].TradePrice.ShouldBe(30_843);
        steps[2].Side.ShouldBe(TradeSide.Buyer);
    }
}
