using DbnClient;
using Shouldly;

namespace DbnSync.UnitTests.ClientTests;

public sealed class ZstdTests
{
    private const int Length = 5_000;
    private const int Seed = 3;
    private const int Junk = 8;
    private const int TailCut = 3;

    private static readonly byte[] Plain = ZstdFrames.Noise(Length, Seed);

    [Fact]
    public void A_frame_decompresses_into_a_buffer_of_its_length()
    {
        var plain = new byte[Length];

        Zstd.TryDecompress(Zstd.Compress(Plain), plain, out var written).ShouldBeTrue();

        written.ShouldBe(Length);
        plain.ShouldBe(Plain);
    }

    [Fact]
    public void Frames_back_to_back_with_a_skippable_frame_decompress_together()
    {
        var plain = new byte[Length * 2];
        byte[] packed = [.. Zstd.Compress(Plain), .. ZstdFrames.Skippable, .. Zstd.Compress(Plain)];

        Zstd.TryDecompress(packed, plain, out var written).ShouldBeTrue();

        written.ShouldBe(Length * 2);
        plain.ShouldBe([.. Plain, .. Plain]);
    }

    [Fact]
    public void A_buffer_too_small_is_reported_not_thrown()
    {
        Zstd.TryDecompress(Zstd.Compress(Plain), new byte[Length - 1], out _).ShouldBeFalse();
    }

    [Fact]
    public void A_buffer_too_large_shows_in_what_was_written()
    {
        Zstd.TryDecompress(Zstd.Compress(Plain), new byte[Length + 1], out var written).ShouldBeTrue();

        written.ShouldBe(Length);
    }

    [Fact]
    public void Bytes_after_the_frame_that_are_not_zstd_are_damage()
    {
        byte[] packed = [.. Zstd.Compress(Plain), .. ZstdFrames.Junk(Junk)];

        Should.Throw<InvalidDataException>(() => Zstd.TryDecompress(packed, new byte[Length], out _));
    }

    [Fact]
    public void A_frame_cut_short_is_damage()
    {
        var packed = Zstd.Compress(Plain)[..^TailCut];

        Should.Throw<InvalidDataException>(() => Zstd.TryDecompress(packed, new byte[Length], out _));
    }

    [Fact]
    public void Bytes_that_are_not_zstd_are_damage()
    {
        Should.Throw<InvalidDataException>(() => Zstd.TryDecompress(ZstdFrames.Junk(Junk), new byte[Length], out _));
    }
}
