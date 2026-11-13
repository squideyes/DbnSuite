using DbnSuite.Client;
using DbnSuite.Client.Tape;
using Shouldly;

namespace DbnSuite.UnitTests.TapeFormatTests;

public sealed class TapeTests
{
    private const int Seed = 5;
    private const int Events = 2_000;
    private const int MagicByte = 0;
    private const int VersionByte = 4;
    private const int BitsInAByte = 8;

    [Fact]
    public async Task EveryFlippedBitIsCaught()
    {
        var bytes = await SmallTapeAsync(compress: false);

        for (var index = 0; index < bytes.Length; index++)
        {
            for (var bit = 0; bit < BitsInAByte; bit++)
            {
                bytes[index] ^= (byte)(1 << bit);

                var loaded = await TapeFixture.LoadAsync(bytes);

                loaded.IsSuccess.ShouldBeFalse($"byte {index}, bit {bit}");

                bytes[index] ^= (byte)(1 << bit);
            }
        }
    }

    [Fact]
    public async Task EveryTruncationIsCaught()
    {
        var bytes = await SmallTapeAsync(compress: false);

        for (var length = 0; length < bytes.Length; length++)
        {
            var loaded = await TapeFixture.LoadAsync(bytes[..length]);

            loaded.IsSuccess.ShouldBeFalse($"length {length}");
        }
    }

    [Fact]
    public async Task EveryTruncationIsCaughtOnAForwardOnlyStream()
    {
        var bytes = await SmallTapeAsync(compress: false);

        for (var length = 0; length < bytes.Length; length++)
        {
            using var stream = new ForwardOnlyStream(bytes[..length]);

            var loaded = await Tape.LoadAsync(stream, CancellationToken.None);

            loaded.IsSuccess.ShouldBeFalse($"length {length}");
        }
    }

    [Fact]
    public async Task ExtraBytesAreCaught()
    {
        var bytes = await SmallTapeAsync(compress: false);

        var loaded = await TapeFixture.LoadAsync([.. bytes, 0]);

        loaded.Error!.Code.ShouldBe(TapeErrors.Corrupt);
    }

    [Fact]
    public async Task ExtraBytesAreCaughtOnAForwardOnlyStream()
    {
        var bytes = await SmallTapeAsync(compress: false);
        using var stream = new ForwardOnlyStream([.. bytes, 0]);

        var loaded = await Tape.LoadAsync(stream, CancellationToken.None);

        loaded.Error!.Code.ShouldBe(TapeErrors.Corrupt);
    }

    [Fact]
    public async Task AWrongMagicIsNotATape()
    {
        var bytes = await SmallTapeAsync(compress: false);

        bytes[MagicByte] = (byte)'X';

        var loaded = await TapeFixture.LoadAsync(bytes);

        loaded.Error!.Code.ShouldBe(TapeErrors.NotATape);
    }

    [Fact]
    public async Task AnEmptyStreamIsNotATape()
    {
        var loaded = await TapeFixture.LoadAsync([]);

        loaded.Error!.Code.ShouldBe(TapeErrors.NotATape);
    }

    [Fact]
    public async Task AWrongVersionIsUnsupported()
    {
        var bytes = await SmallTapeAsync(compress: false);

        bytes[VersionByte] = TapeHeader.CurrentVersion + 1;

        var loaded = await TapeFixture.LoadAsync(bytes);

        loaded.Error!.Code.ShouldBe(TapeErrors.Unsupported);
    }

    [Fact]
    public async Task AFlippedBodyBitFailsTheHash()
    {
        var bytes = await SmallTapeAsync(compress: false);

        bytes[^1] ^= 1;

        var loaded = await TapeFixture.LoadAsync(bytes);

        loaded.Error!.Code.ShouldBe(TapeErrors.Corrupt);
    }

    [Fact]
    public async Task ZstdReplaysTheSameEvents()
    {
        var plain = new L1Writer(TapeFixture.Info());
        var packed = new L1Writer(TapeFixture.Info(compress: true));

        var steps = L1CursorTests.FillRandom(plain, Seed, Events);

        L1CursorTests.FillRandom(packed, Seed, Events);

        var plainTape = await TapeFixture.RoundTripAsync(plain);
        var packedTape = await TapeFixture.RoundTripAsync(packed);

        packedTape.Header.IsCompressed.ShouldBeTrue();
        packedTape.Header.StoredLength.ShouldBeLessThan(packedTape.Header.BodyLength);
        packedTape.Header.BodyLength.ShouldBe(plainTape.Header.BodyLength);
        packedTape.Body.SequenceEqual(plainTape.Body).ShouldBeTrue();
        L1CursorTests.Read(packedTape).ShouldBe(steps);
    }

    [Fact]
    public async Task ADamagedZstdBodyFailsTheHash()
    {
        var bytes = await SmallTapeAsync(compress: true);

        bytes[^3] ^= 0x40;

        var loaded = await TapeFixture.LoadAsync(bytes);

        loaded.Error!.Code.ShouldBe(TapeErrors.Corrupt);
    }

    [Fact]
    public async Task ADamagedZstdBodyIsAFailureEvenUnhashed()
    {
        var bytes = await SmallTapeAsync(compress: true);

        Array.Fill(bytes, (byte)0xA5, TapeHeader.Size, 16);

        var loaded = await TapeFixture.LoadAsync(bytes, verifyHash: false);

        loaded.Error!.Code.ShouldBe(TapeErrors.Corrupt);
    }

    [Fact]
    public async Task AnUnhashedLoadStillCannotReadStrayMemory()
    {
        var bytes = await SmallTapeAsync(compress: false);

        Array.Fill(bytes, (byte)0xFF, bytes.Length - 8, 8);

        var loaded = await TapeFixture.LoadAsync(bytes, verifyHash: false);

        loaded.IsSuccess.ShouldBeTrue();
        Should.Throw<InvalidDataException>(() => WalkAll(loaded.Value));
    }

    [Fact]
    public async Task TheWrongCursorIsAProgrammingError()
    {
        var bytes = await SmallTapeAsync(compress: false);

        var loaded = await TapeFixture.LoadAsync(bytes);

        Should.Throw<InvalidOperationException>(() => loaded.Value.S1Cursor());
        Should.Throw<InvalidOperationException>(() => loaded.Value.TradeCursor());
    }

    [Fact]
    public async Task AMissingFileIsAnIoFailure()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.tape");

        var loaded = await Tape.LoadAsync(path, CancellationToken.None);

        loaded.Error!.Code.ShouldBe(TapeErrors.Io);
    }

    private static void WalkAll(Tape tape)
    {
        var cursor = tape.L1Cursor();

        while (cursor.MoveNext())
        {
        }
    }

    private static async Task<byte[]> SmallTapeAsync(bool compress)
    {
        var writer = new L1Writer(TapeFixture.Info(compress));

        L1CursorTests.FillRandom(writer, Seed, compress ? Events : 20);

        return await TapeFixture.ToBytesAsync(writer);
    }

    private sealed class ForwardOnlyStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
    }
}
