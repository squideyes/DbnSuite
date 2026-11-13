using System.Buffers.Binary;
using DbnSuite.Client.Dbn;
using Shouldly;
using static DbnSuite.UnitTests.TestData;

namespace DbnSuite.UnitTests.ClientTests;

public sealed class DbnMetadataTests
{
    private const string Front = "ES.n.0";
    private const uint FrontId = 42;

    // Offsets into a whole metadata block, its eight-byte prefix included.
    private const int VersionAt = 3;
    private const int LengthAt = 4;
    private const int SchemaAt = 8 + 16;
    private const int SymbologyAt = 8 + 43;

    /// <summary>The mapped symbol of the first mapping when nothing was requested.</summary>
    private const int MappedSymbolAt = 8 + 100 + 4 + 4 + 4 + 4 + 4 + DbnBytes.SymbolWidth + 4 + 8;

    private static readonly Dictionary<string, uint> Mappings = new() { [Front] = FrontId };

    private static byte[] Block() => DbnBytes.Metadata(Dataset, [], Mappings);

    private static Task<DbnMetadata> Read(byte[] bytes) => DbnMetadata.ReadAsync(new MemoryStream(bytes), CancellationToken.None);

    [Fact]
    public async Task Start_and_end_are_read_as_utc_times()
    {
        var metadata = await Read(Block());

        metadata.Start.ShouldBe(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        metadata.End.ShouldBe(new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task Raw_is_the_block_exactly_as_stored()
    {
        var block = Block();

        var metadata = await Read(block);

        metadata.Raw.ShouldBe(block);
    }

    [Fact]
    public async Task The_stream_is_left_at_the_first_record()
    {
        byte[] record = [1, 2, 3];
        var stream = new MemoryStream([.. Block(), .. record]);

        await DbnMetadata.ReadAsync(stream, CancellationToken.None);

        stream.Position.ShouldBe(Block().Length);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task Versions_with_another_layout_are_refused(byte version)
    {
        var block = Block();
        block[VersionAt] = version;

        var refused = await Should.ThrowAsync<InvalidDataException>(() => Read(block));

        refused.Message.ShouldContain($"version {version}");
    }

    [Theory]
    [InlineData(99)]
    [InlineData(64 * 1024 * 1024 + 1)]
    public async Task An_implausible_length_is_refused(int length)
    {
        var block = Block();
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(LengthAt), (uint)length);

        await Should.ThrowAsync<InvalidDataException>(() => Read(block));
    }

    [Fact]
    public async Task A_block_cut_short_is_refused()
    {
        await Should.ThrowAsync<EndOfStreamException>(() => Read(Block()[..50]));
    }

    [Theory]
    [InlineData(0, "mbo")]
    [InlineData(1, "mbp-1")]
    [InlineData(5, "ohlcv-1s")]
    [InlineData(9, "definition")]
    [InlineData(10, "statistics")]
    [InlineData(11, "status")]
    [InlineData(19, "bbo-1m")]
    [InlineData(20, "mixed")]
    [InlineData(0xFFFF, "mixed")]
    public async Task A_schema_number_names_its_schema_or_says_mixed(int number, string schema)
    {
        var block = Block();
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(SchemaAt), (ushort)number);

        (await Read(block)).Schema.ShouldBe(schema);
    }

    [Fact]
    public void Every_schema_is_listed_in_order()
    {
        DbnMetadata.Schemas.Count.ShouldBe(20);
        DbnMetadata.Schemas[0].ShouldBe("mbo");
        DbnMetadata.Schemas[^1].ShouldBe("bbo-1m");
    }

    [Fact]
    public async Task Mappings_to_anything_but_instrument_ids_map_nothing()
    {
        const byte RawSymbolSymbology = 1;
        var block = Block();
        block[SymbologyAt] = RawSymbolSymbology;

        (await Read(block)).Symbols.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_mapped_symbol_that_is_not_a_number_is_skipped()
    {
        var block = Block();
        block[MappedSymbolAt] = (byte)'X';

        (await Read(block)).Symbols.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_symbol_is_found_by_its_id_or_named_by_it()
    {
        var metadata = await Read(Block());

        metadata.SymbolOf(FrontId).ShouldBe(Front);
        metadata.SymbolOf(FrontId + 1).ShouldBe("43");
    }

    [Fact]
    public void A_field_with_no_padding_is_read_to_its_end()
    {
        DbnMetadata.Text("ESZ6"u8).ShouldBe("ESZ6");
    }

    [Fact]
    public void A_padded_field_stops_at_the_first_nul()
    {
        DbnMetadata.Text("ES\0Z6"u8).ShouldBe("ES");
    }

    [Fact]
    public async Task A_count_no_block_could_hold_is_refused()
    {
        var block = Block();
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(RequestedCountAt), uint.MaxValue);

        await Should.ThrowAsync<InvalidDataException>(() => Read(block));
    }

    [Fact]
    public async Task A_block_that_ends_before_what_it_says_it_holds_is_refused()
    {
        var block = Block();
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(RequestedCountAt), (uint)(block.Length - DbnMetadata.PrefixSize));

        await Should.ThrowAsync<InvalidDataException>(() => Read(block));
    }

    private const int RequestedCountAt = 8 + 100 + 4;
}
