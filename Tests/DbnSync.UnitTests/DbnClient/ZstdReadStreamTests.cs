using DbnClient;
using Shouldly;

namespace DbnSync.UnitTests.ClientTests;

public sealed class ZstdReadStreamTests
{
    private const int Small = 1_000;
    private const int Large = 600_000;
    private const int FirstSeed = 1;
    private const int SecondSeed = 2;
    private const int OneByte = 1;
    private const int TailCut = 3;

    [Fact]
    public async Task One_frame_reads_back_whole()
    {
        var plain = Bytes(Small, FirstSeed);

        (await ReadAllAsync(Zstd.Compress(plain))).ShouldBe(plain);
    }

    [Fact]
    public async Task Frames_back_to_back_read_as_one_stream()
    {
        var first = Bytes(Small, FirstSeed);
        var second = Bytes(Small, SecondSeed);

        (await ReadAllAsync([.. Zstd.Compress(first), .. Zstd.Compress(second)])).ShouldBe([.. first, .. second]);
    }

    [Fact]
    public async Task A_skippable_frame_between_frames_is_passed_over()
    {
        var first = Bytes(Small, FirstSeed);
        var second = Bytes(Small, SecondSeed);

        (await ReadAllAsync([.. Zstd.Compress(first), .. ZstdFrames.Skippable, .. Zstd.Compress(second)]))
            .ShouldBe([.. first, .. second]);
    }

    [Fact]
    public async Task An_empty_stream_reads_as_empty()
    {
        (await ReadAllAsync([])).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(4_096)]
    public async Task Bytes_after_the_last_frame_that_are_not_zstd_are_damage(int junk)
    {
        byte[] packed = [.. Zstd.Compress(Bytes(Small, FirstSeed)), .. ZstdFrames.Junk(junk)];

        await Should.ThrowAsync<InvalidDataException>(() => ReadAllAsync(packed));
        Should.Throw<InvalidDataException>(() => ReadAll(packed));
    }

    [Fact]
    public async Task Bytes_that_are_not_zstd_are_damage()
    {
        await Should.ThrowAsync<InvalidDataException>(() => ReadAllAsync(ZstdFrames.Junk(Small)));
    }

    [Fact]
    public async Task A_frame_cut_short_is_damage()
    {
        var packed = Zstd.Compress(Bytes(Small, FirstSeed));

        await Should.ThrowAsync<InvalidDataException>(() => ReadAllAsync(packed[..^TailCut]));
        Should.Throw<InvalidDataException>(() => ReadAll(packed[..^TailCut]));
    }

    [Fact]
    public async Task A_second_frame_cut_short_is_damage()
    {
        byte[] packed = [.. Zstd.Compress(Bytes(Small, FirstSeed)), .. Zstd.Compress(Bytes(Small, SecondSeed))];

        await Should.ThrowAsync<InvalidDataException>(() => ReadAllAsync(packed[..^TailCut]));
    }

    [Fact]
    public async Task Frames_larger_than_its_input_buffer_read_back_whole()
    {
        var first = ZstdFrames.Noise(Large, FirstSeed);
        var second = ZstdFrames.Noise(Large, SecondSeed);
        byte[] packed = [.. Zstd.Compress(first), .. Zstd.Compress(second)];

        (await ReadAllAsync(packed)).ShouldBe([.. first, .. second]);
        ReadAll(packed).ShouldBe([.. first, .. second]);
    }

    [Fact]
    public async Task Input_that_trickles_in_a_byte_at_a_time_reads_back_whole()
    {
        var first = Bytes(Small, FirstSeed);
        var second = Bytes(Small, SecondSeed);
        await using var stream = Zstd.Decompressing(new TrickleStream([.. Zstd.Compress(first), .. Zstd.Compress(second)]));
        using var target = new MemoryStream();

        await stream.CopyToAsync(target);

        target.ToArray().ShouldBe([.. first, .. second]);
    }

    [Fact]
    public async Task Reading_a_byte_at_a_time_reads_back_whole()
    {
        var plain = Bytes(Small, FirstSeed);
        await using var stream = Zstd.Decompressing(new MemoryStream(Zstd.Compress(plain)));
        var read = new List<byte>();
        var one = new byte[OneByte];

        while (await stream.ReadAsync(one) > 0)
            read.Add(one[0]);

        read.ShouldBe(plain);
    }

    [Fact]
    public async Task Disposing_it_closes_the_stream_it_reads()
    {
        var packed = new MemoryStream(Zstd.Compress(Bytes(Small, FirstSeed)));

        await Zstd.Decompressing(packed).DisposeAsync();

        packed.CanRead.ShouldBeFalse();
    }

    private static byte[] Bytes(int length, int seed) =>
        [.. Enumerable.Range(0, length).Select(i => (byte)(seed + i % 7))];

    private static async Task<byte[]> ReadAllAsync(byte[] packed)
    {
        await using var stream = Zstd.Decompressing(new MemoryStream(packed));
        using var target = new MemoryStream();

        await stream.CopyToAsync(target);

        return target.ToArray();
    }

    private static byte[] ReadAll(byte[] packed)
    {
        using var stream = Zstd.Decompressing(new MemoryStream(packed));
        using var target = new MemoryStream();

        stream.CopyTo(target);

        return target.ToArray();
    }

    /// <summary>Hands over at most one byte per read, as a slow network might.</summary>
    private sealed class TrickleStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, OneByte));

        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, OneByte)]);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, OneByte)], cancellationToken);
    }
}
