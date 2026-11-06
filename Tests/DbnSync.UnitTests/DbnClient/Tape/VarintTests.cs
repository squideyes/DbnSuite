using DbnClient.Tape;
using Shouldly;

namespace DbnSync.UnitTests.TapeFormatTests;

public sealed class VarintTests
{
    [Theory]
    [InlineData(0UL, 1)]
    [InlineData(127UL, 1)]
    [InlineData(128UL, 2)]
    [InlineData(16_383UL, 2)]
    [InlineData(16_384UL, 3)]
    [InlineData(2_097_151UL, 3)]
    [InlineData(2_097_152UL, 4)]
    [InlineData(268_435_455UL, 4)]
    [InlineData(268_435_456UL, 5)]
    [InlineData(34_359_738_367UL, 5)]
    [InlineData(34_359_738_368UL, 6)]
    [InlineData(4_398_046_511_103UL, 6)]
    [InlineData(4_398_046_511_104UL, 7)]
    [InlineData(562_949_953_421_311UL, 7)]
    [InlineData(562_949_953_421_312UL, 8)]
    [InlineData(72_057_594_037_927_935UL, 8)]
    [InlineData(72_057_594_037_927_936UL, 9)]
    [InlineData(9_223_372_036_854_775_807UL, 9)]
    [InlineData(9_223_372_036_854_775_808UL, 10)]
    [InlineData(ulong.MaxValue, 10)]
    public void EveryLengthBoundaryRoundTrips(ulong value, int length)
    {
        var buffer = new byte[Varint.MaxBytes];

        var written = Varint.Write(buffer, value);
        var position = 0;
        var read = Varint.Read(buffer, ref position);

        written.ShouldBe(length);
        position.ShouldBe(length);
        read.ShouldBe(value);
    }

    [Theory]
    [InlineData(1_250UL, "E209")]
    [InlineData(800UL, "A006")]
    public void BytesFollowLeb128(ulong value, string hex)
    {
        var buffer = new byte[Varint.MaxBytes];

        var written = Varint.Write(buffer, value);

        TapeFixture.Hex(buffer.AsSpan(0, written)).ShouldBe(hex);
    }

    [Theory]
    [InlineData(0L, 0UL)]
    [InlineData(-1L, 1UL)]
    [InlineData(1L, 2UL)]
    [InlineData(-2L, 3UL)]
    [InlineData(long.MaxValue, ulong.MaxValue - 1)]
    [InlineData(long.MinValue, ulong.MaxValue)]
    public void ZigzagKeepsSmallChangesSmall(long value, ulong zigzag)
    {
        var encoded = Varint.ToZigzag(value);

        var decoded = Varint.FromZigzag(encoded);

        encoded.ShouldBe(zigzag);
        decoded.ShouldBe(value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("80")]
    [InlineData("FFFFFFFF")]
    [InlineData("FFFFFFFFFFFFFFFFFF02")]
    [InlineData("FFFFFFFFFFFFFFFFFF80")]
    [InlineData("8080808080808080808000")]
    public void DamagedNumbersThrow(string hex)
    {
        var bytes = TapeFixture.Bytes(hex);

        Should.Throw<InvalidDataException>(() =>
        {
            var position = 0;

            Varint.Read(bytes, ref position);
        });
    }
}
