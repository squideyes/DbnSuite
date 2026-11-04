
using DbnClient.Dbn;
using Shouldly;
using static DbnSync.UnitTests.TestData;

namespace DbnSync.UnitTests.ClientTests;

public sealed class DbnReaderTests
{
    private const string Front = "MCL.n.0";
    private const string Next = "MCL.n.1";
    private const uint FrontId = 42026453;
    private const uint NextId = 42001996;
    private const uint UnmappedId = 7;
    private const long Price = 102_950_000_000;
    private const decimal PriceInDollars = 102.95m;
    private const uint Quantity = 14;
    private const ulong OrderId = 8_068_565_965_852;
    private const ulong TsEvent = DbnBytes.Start + 46_806_234_844_395;
    private const byte UnknownType = 0x99;
    private const int UnknownLength = 24;
    private const string Contract = "MCLZ26";
    private const string RawSymbol = "MCLZ6";
    private const string Asset = "MCL";
    private const long TickNanos = 10_000_000;
    private const decimal Tick = 0.01m;
    private const ulong Activation = 1_700_000_000_000_000_000;
    private const ulong Expiration = 1_795_000_000_000_000_000;
    private const ushort MaturityYear = 2026;
    private const byte MaturityMonth = 12;

    private static readonly string[] Requested = [Front, Next];

    private static readonly Dictionary<string, uint> Mappings = new() { [Front] = FrontId, [Next] = NextId };

    private static MemoryStream File(params byte[][] records) =>
        new(DbnBytes.Compress([DbnBytes.Metadata(Dataset, Requested, Mappings), .. records]));

    [Fact]
    public async Task Metadata_names_what_was_asked_for_and_maps_ids_to_it()
    {
        await using var reader = await DbnReader.OpenAsync(File(), CancellationToken.None);

        reader.Metadata.Version.ShouldBe(DbnBytes.Version);
        reader.Metadata.Dataset.ShouldBe(Dataset);
        reader.Metadata.Schema.ShouldBe(Mbo);
        reader.Metadata.Requested.ShouldBe(Requested);
        reader.Metadata.SymbolOf(FrontId).ShouldBe(Front);
        reader.Metadata.SymbolOf(NextId).ShouldBe(Next);
    }

    [Fact]
    public async Task An_unmapped_instrument_is_named_by_its_id()
    {
        await using var reader = await DbnReader.OpenAsync(File(), CancellationToken.None);

        reader.Metadata.SymbolOf(UnmappedId).ShouldBe(UnmappedId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task An_mbo_record_decodes_field_by_field()
    {
        await using var reader = await DbnReader.OpenAsync(
            File(DbnBytes.Mbo(FrontId, TsEvent, Price, Quantity, 'A', 'B', OrderId)), CancellationToken.None);

        var records = await reader.ReadAsync(CancellationToken.None).ToListAsync();

        var mbo = records.ShouldHaveSingleItem().ShouldBeOfType<MboRecord>();
        mbo.InstrumentId.ShouldBe(FrontId);
        mbo.TsEvent.ShouldBe(TsEvent);
        mbo.Price.ShouldBe(PriceInDollars);
        mbo.Quantity.ShouldBe(Quantity);
        mbo.Action.ShouldBe('A');
        mbo.Side.ShouldBe('B');
        mbo.OrderId.ShouldBe(OrderId);
    }

    [Fact]
    public async Task A_record_type_it_does_not_know_is_stepped_over_by_its_own_length()
    {
        await using var reader = await DbnReader.OpenAsync(File(
            DbnBytes.Unknown(UnknownType, UnknownLength, NextId, TsEvent),
            DbnBytes.Mbo(FrontId, TsEvent, Price, Quantity, 'T', 'A', OrderId)), CancellationToken.None);

        var records = await reader.ReadAsync(CancellationToken.None).ToListAsync();

        records.Count.ShouldBe(2);
        records[0].ShouldBeOfType<OtherRecord>().Length.ShouldBe(UnknownLength);
        records[1].ShouldBeOfType<MboRecord>().Price.ShouldBe(PriceInDollars);
    }

    [Fact]
    public async Task The_no_price_sentinel_reads_as_no_price()
    {
        await using var reader = await DbnReader.OpenAsync(
            File(DbnBytes.Mbo(FrontId, TsEvent, DbnPrice.Undefined, 0, 'R', 'N', 0)), CancellationToken.None);

        var records = await reader.ReadAsync(CancellationToken.None).ToListAsync();

        records.ShouldHaveSingleItem().ShouldBeOfType<MboRecord>().Price.ShouldBeNull();
    }

    [Fact]
    public async Task A_stream_that_is_not_DBN_is_refused()
    {
        var notDbn = new MemoryStream(DbnBytes.Compress("PK\u0003\u0004 not a dbn file"u8.ToArray()));

        await Should.ThrowAsync<InvalidDataException>(() => DbnReader.OpenAsync(notDbn, CancellationToken.None));
    }

    [Fact]
    public async Task Records_that_straddle_read_blocks_come_through_whole_and_in_order()
    {
        const int Records = 20_000;
        var records = Enumerable.Range(0, Records)
            .Select(i => DbnBytes.Mbo(FrontId, TsEvent, Price, Quantity, 'A', 'B', (ulong)i))
            .ToArray();
        await using var reader = await DbnReader.OpenAsync(File(records), CancellationToken.None);

        var orders = await reader.ReadAsync(CancellationToken.None).Select(r => ((MboRecord)r).OrderId).ToListAsync();

        orders.ShouldBe(Enumerable.Range(0, Records).Select(i => (ulong)i).ToList());
    }

    [Fact]
    public async Task Raw_records_are_the_bytes_as_stored()
    {
        var record = DbnBytes.Mbo(FrontId, TsEvent, Price, Quantity, 'A', 'B', OrderId);
        await using var reader = await DbnReader.OpenAsync(File(record), CancellationToken.None);

        var raw = await reader.ReadRawAsync(CancellationToken.None).Select(r => r.ToArray()).ToListAsync();

        raw.ShouldHaveSingleItem().ShouldBe(record);
    }

    [Fact]
    public async Task A_file_with_no_records_reads_as_none()
    {
        await using var reader = await DbnReader.OpenAsync(File(), CancellationToken.None);

        (await reader.ReadAsync(CancellationToken.None).ToListAsync()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task A_record_shorter_than_its_header_is_refused(byte words)
    {
        var record = DbnBytes.Unknown(UnknownType, UnknownLength, FrontId, TsEvent);
        record[0] = words;
        await using var reader = await DbnReader.OpenAsync(File(record), CancellationToken.None);

        await Should.ThrowAsync<InvalidDataException>(async () => await reader.ReadAsync(CancellationToken.None).ToListAsync());
    }

    [Fact]
    public async Task A_stream_that_ends_inside_a_record_is_refused()
    {
        var record = DbnBytes.Mbo(FrontId, TsEvent, Price, Quantity, 'A', 'B', OrderId);
        await using var reader = await DbnReader.OpenAsync(File(record[..30]), CancellationToken.None);

        var refused = await Should.ThrowAsync<InvalidDataException>(async () => await reader.ReadAsync(CancellationToken.None).ToListAsync());

        refused.Message.ShouldContain("30");
    }

    [Fact]
    public void Times_keep_all_nine_digits()
    {
        DbnTime.Format(TsEvent).ShouldBe("2026-11-01T13:00:06.234844395Z");
    }

    [Fact]
    public async Task A_printed_mbo_line_names_the_contract_and_the_order()
    {
        await using var reader = await DbnReader.OpenAsync(
            File(DbnBytes.Mbo(FrontId, TsEvent, Price, Quantity, 'A', 'B', OrderId)), CancellationToken.None);
        var record = (await reader.ReadAsync(CancellationToken.None).ToListAsync()).Single();

        var line = DbnFetch.RecordText.Format(record, Contract, TimeZoneInfo.Utc);

        line.ShouldContain(Contract);
        line.ShouldContain("add");
        line.ShouldContain("bid");
        line.ShouldContain("102.95");
    }

    [Fact]
    public async Task A_definition_decodes_what_names_and_dates_a_contract()
    {
        await using var reader = await DbnReader.OpenAsync(File(DbnBytes.Definition(FrontId, TsEvent, RawSymbol, Asset,
            DefinitionRecord.Future, TickNanos, Activation, Expiration, MaturityYear, MaturityMonth)), CancellationToken.None);

        var definition = (await reader.ReadAsync(CancellationToken.None).ToListAsync()).Single().ShouldBeOfType<DefinitionRecord>();

        definition.RawSymbol.ShouldBe(RawSymbol);
        definition.Asset.ShouldBe(Asset);
        definition.InstrumentClass.ShouldBe(DefinitionRecord.Future);
        definition.TickSize.ShouldBe(Tick);
        definition.Activation.ShouldBe(Activation);
        definition.Expiration.ShouldBe(Expiration);
        definition.MaturityYear.ShouldBe(MaturityYear);
        definition.MaturityMonth.ShouldBe(MaturityMonth);
    }

    [Fact]
    public async Task A_stream_that_is_not_zstd_is_refused_as_damaged()
    {
        var notZstd = new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8]);

        await Should.ThrowAsync<InvalidDataException>(() => DbnReader.OpenAsync(notZstd, CancellationToken.None));
    }

    [Fact]
    public async Task A_compressed_stream_cut_short_is_refused_as_damaged()
    {
        var whole = DbnBytes.Compress(DbnBytes.Metadata(Dataset, [], new Dictionary<string, uint>()));

        await Should.ThrowAsync<InvalidDataException>(() =>
            DbnReader.OpenAsync(new MemoryStream(whole[..^4]), CancellationToken.None));
    }

    [Fact]
    public async Task A_compressed_stream_damaged_after_the_metadata_is_refused_as_damaged()
    {
        var metadata = DbnBytes.Compress(DbnBytes.Metadata(Dataset, [], new Dictionary<string, uint>()));
        await using var reader = await DbnReader.OpenAsync(new MemoryStream([.. metadata, 1, 2, 3, 4, 5, 6, 7, 8]), CancellationToken.None);

        await Should.ThrowAsync<InvalidDataException>(async () => await reader.ReadAsync(CancellationToken.None).ToListAsync());
    }

    [Fact]
    public async Task An_io_failure_is_not_taken_for_damage()
    {
        var failing = new FailingStream(new IOException(DiskFailed), File());

        await Should.ThrowAsync<IOException>(() => DbnReader.OpenAsync(failing, CancellationToken.None));
    }

    private const string DiskFailed = "the disk went away";

    private sealed class FailingStream(Exception failure, Stream inner) : MemoryStream
    {
        public override int Read(byte[] buffer, int offset, int count) => throw failure;

        public override int Read(Span<byte> buffer) => throw failure;

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromException<int>(failure);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(failure);

        protected override void Dispose(bool disposing)
        {
            inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
