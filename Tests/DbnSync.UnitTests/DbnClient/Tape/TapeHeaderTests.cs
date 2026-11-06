using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using DbnClient.Tape;
using Shouldly;

namespace DbnSync.UnitTests.TapeFormatTests;

public sealed class TapeHeaderTests
{
    private const int CrcOffset = 188;
    private const long EventCount = 1_000;
    private const long BodyLength = 3_000;
    private const long StoredLength = 2_000;
    private const ulong BodyHash = 0x1122334455667788;
    private const long MarkOffset = 1_200;
    private const long MarkIndex = 400;
    private const long MarkTime = 1_793_260_000_000_000;
    private const int MarkBid = 30_850;
    private const int MarkAsk = 30_851;
    private const uint MarkBidSize = 12;
    private const uint MarkAskSize = 34;
    private const int MarkLast = 30_849;
    private const byte MarkState = 7;
    private const long EndOffset = 2_500;
    private const long EndIndex = 900;

    [Fact]
    public void EveryFieldSitsAtItsDocumentedOffset()
    {
        var bytes = Written(Sample());

        Encoding.ASCII.GetString(bytes, 0, 4).ShouldBe("TAPE");
        bytes[4].ShouldBe((byte)1);
        bytes[5].ShouldBe((byte)3);
        bytes[6].ShouldBe((byte)3);
        bytes[7].ShouldBe((byte)1);
        Read32(bytes, 8).ShouldBe(1_000u);
        Read32(bytes, 12).ShouldBe(20261029u);
        Read64(bytes, 16).ShouldBe(TapeFixture.TickRaw);
        Read64(bytes, 24).ShouldBe(TapeFixture.OpenSeconds * TapeFixture.NanosPerSecond);
        Read64(bytes, 32).ShouldBe(TapeFixture.CloseSeconds * TapeFixture.NanosPerSecond);
        BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(40)).ShouldBe(TapeFixture.BasePrice);
        Read32(bytes, 44).ShouldBe(TapeFixture.InstrumentId);
        Read64(bytes, 48).ShouldBe(EventCount);
        Read64(bytes, 56).ShouldBe(BodyLength);
        Read64(bytes, 64).ShouldBe(StoredLength);
        BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(72)).ShouldBe(BodyHash);
        Text(bytes, 80, 16).ShouldBe(TapeFixture.Dataset);
        Text(bytes, 96, 16).ShouldBe(TapeFixture.Symbol);
        Text(bytes, 112, 8).ShouldBe(TapeFixture.Root);
        Read64(bytes, 120).ShouldBe(MarkOffset);
        Read64(bytes, 128).ShouldBe(MarkIndex);
        Read64(bytes, 136).ShouldBe(MarkTime);
        BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(144)).ShouldBe(MarkBid);
        BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(148)).ShouldBe(MarkAsk);
        Read32(bytes, 152).ShouldBe(MarkBidSize);
        Read32(bytes, 156).ShouldBe(MarkAskSize);
        BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(160)).ShouldBe(MarkLast);
        bytes[164].ShouldBe(MarkState);
        Read64(bytes, 168).ShouldBe(EndOffset);
        Read64(bytes, 176).ShouldBe(EndIndex);
        Read32(bytes, 184).ShouldBe(0u);
        Read32(bytes, CrcOffset).ShouldBe(Crc32.HashToUInt32(bytes.AsSpan(0, CrcOffset)));
    }

    [Fact]
    public void AnS1HeaderStatesSeconds()
    {
        var bytes = Written(Sample() with { Kind = TapeKind.S1 });

        Read32(bytes, 8).ShouldBe(1_000_000_000u);
    }

    [Fact]
    public void TheHashesAreTheStandardOnes()
    {
        var crc = Crc32.HashToUInt32("123456789"u8);
        var hash = XxHash3.HashToUInt64([]);

        crc.ShouldBe(0xCBF43926u);
        hash.ShouldBe(0x2D06800538D394C2UL);
    }

    [Fact]
    public void AHeaderRoundTrips()
    {
        var header = Sample();

        var parsed = TapeHeader.Parse(Written(header));

        parsed.Value.ShouldBe(header);
    }

    [Fact]
    public void AHeaderWithoutMarksRoundTrips()
    {
        var header = Sample() with { HasRth = false, RthStart = default, RthEndOffset = 0, RthEndIndex = 0 };

        var parsed = TapeHeader.Parse(Written(header));

        parsed.Value.ShouldBe(header);
    }

    [Theory]
    [InlineData(5, 4, TapeErrors.Unsupported)]
    [InlineData(5, 0, TapeErrors.Unsupported)]
    [InlineData(7, 0, TapeErrors.Unsupported)]
    [InlineData(7, 2, TapeErrors.Unsupported)]
    [InlineData(6, 7, TapeErrors.Corrupt)]
    [InlineData(8, 0, TapeErrors.Corrupt)]
    [InlineData(15, 0xFF, TapeErrors.Corrupt)]
    [InlineData(84, 0xFF, TapeErrors.Corrupt)]
    [InlineData(95, 0x41, TapeErrors.Corrupt)]
    [InlineData(165, 1, TapeErrors.Corrupt)]
    [InlineData(184, 1, TapeErrors.Corrupt)]
    public void ABadFieldUnderAGoodCrcIsRefused(int offset, byte value, string code)
    {
        var bytes = Written(Sample());

        bytes[offset] = value;
        Reseal(bytes);

        var parsed = TapeHeader.Parse(bytes);

        parsed.Error!.Code.ShouldBe(code);
    }

    [Fact]
    public void AnOrderTapeIsNotReadYet()
    {
        var bytes = Written(Sample());

        bytes[5] = 4;
        Reseal(bytes);

        var parsed = TapeHeader.Parse(bytes);

        parsed.Error!.Code.ShouldBe(TapeErrors.Unsupported);
    }

    [Fact]
    public void AVersionIsReadBeforeTheCrc()
    {
        var bytes = Written(Sample());

        bytes[4] = 2;

        var parsed = TapeHeader.Parse(bytes);

        parsed.Error!.Code.ShouldBe(TapeErrors.Unsupported);
    }

    [Fact]
    public void MarksPastTheBodyAreRefused()
    {
        var bytes = Written(Sample() with { RthEndOffset = BodyLength + 1 });

        var parsed = TapeHeader.Parse(bytes);

        parsed.Error!.Code.ShouldBe(TapeErrors.Corrupt);
    }

    [Fact]
    public void MarksWithoutTheirFlagAreRefused()
    {
        var bytes = Written(Sample());

        bytes[6] = 1;
        Reseal(bytes);

        var parsed = TapeHeader.Parse(bytes);

        parsed.Error!.Code.ShouldBe(TapeErrors.Corrupt);
    }

    [Fact]
    public void AnUncompressedBodyMustStoreItsOwnLength()
    {
        var bytes = Written(Sample() with { IsCompressed = false });

        var parsed = TapeHeader.Parse(bytes);

        parsed.Error!.Code.ShouldBe(TapeErrors.Corrupt);
    }

    [Fact]
    public void ASessionMustOpenBeforeItCloses()
    {
        var header = Sample();
        var bytes = Written(header with { OpenTime = header.CloseTime + 1 });

        var parsed = TapeHeader.Parse(bytes);

        parsed.Error!.Code.ShouldBe(TapeErrors.Corrupt);
    }

    private static TapeHeader Sample() => new()
    {
        Kind = TapeKind.L1,
        Dataset = TapeFixture.Dataset,
        Symbol = TapeFixture.Symbol,
        Root = TapeFixture.Root,
        TradeDate = TapeFixture.TradeDate,
        TickRaw = TapeFixture.TickRaw,
        OpenTime = TapeFixture.OpenSeconds * TapeFixture.NanosPerSecond,
        CloseTime = TapeFixture.CloseSeconds * TapeFixture.NanosPerSecond,
        BasePrice = TapeFixture.BasePrice,
        InstrumentId = TapeFixture.InstrumentId,
        EventCount = EventCount,
        BodyLength = BodyLength,
        StoredLength = StoredLength,
        BodyHash = BodyHash,
        IsCompressed = true,
        HasRth = true,
        RthStart = new RthMark
        {
            BodyOffset = MarkOffset,
            EventIndex = MarkIndex,
            Time = MarkTime,
            BidPrice = MarkBid,
            AskPrice = MarkAsk,
            BidSize = MarkBidSize,
            AskSize = MarkAskSize,
            LastPrice = MarkLast,
            MarketState = MarkState
        },
        RthEndOffset = EndOffset,
        RthEndIndex = EndIndex
    };

    private static byte[] Written(TapeHeader header)
    {
        var bytes = new byte[TapeHeader.Size];

        header.Write(bytes);

        return bytes;
    }

    private static void Reseal(byte[] bytes) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(CrcOffset), Crc32.HashToUInt32(bytes.AsSpan(0, CrcOffset)));

    private static uint Read32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));

    private static long Read64(byte[] bytes, int offset) => BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset));

    private static string Text(byte[] bytes, int offset, int length) =>
        Encoding.ASCII.GetString(bytes, offset, length).TrimEnd('\0');
}
