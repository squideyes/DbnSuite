using DbnClient.Export.Parquet;
using Shouldly;

namespace DbnSync.UnitTests.ClientTests;

/// <summary>Bytes worked out by hand from Thrift's compact protocol specification.</summary>
public sealed class ThriftTests
{
    private const byte End = 0x00;

    private static Thrift Struct() => new Thrift().BeginStruct();

    [Fact]
    public void An_int_is_a_short_header_and_a_zigzag_varint()
    {
        Struct().Int(1, 5).EndStruct().ToArray().ShouldBe([0x15, 0x0A, End]);
    }

    [Fact]
    public void A_negative_int_zigzags_to_a_small_number()
    {
        Struct().Int(1, -1).ToArray().ShouldBe([0x15, 0x01]);
    }

    [Fact]
    public void A_long_has_its_own_type()
    {
        Struct().Long(2, 1).ToArray().ShouldBe([0x26, 0x02]);
    }

    [Fact]
    public void A_value_past_seven_bits_takes_a_second_byte()
    {
        Struct().Int(1, 64).ToArray().ShouldBe([0x15, 0x80, 0x01]);
    }

    [Fact]
    public void A_bool_is_all_in_its_header()
    {
        Struct().Bool(1, true).ToArray().ShouldBe([0x11]);
        Struct().Bool(1, false).ToArray().ShouldBe([0x12]);
    }

    [Fact]
    public void A_string_is_its_length_then_its_utf8()
    {
        Struct().String(1, "é").ToArray().ShouldBe([0x18, 0x02, 0xC3, 0xA9]);
    }

    [Fact]
    public void Field_ids_are_deltas_from_the_last_field()
    {
        Struct().Int(3, 1).Int(4, 1).ToArray().ShouldBe([0x35, 0x02, 0x15, 0x02]);
    }

    [Fact]
    public void A_jump_of_more_than_fifteen_writes_the_field_id_in_full()
    {
        Struct().Int(20, 1).ToArray().ShouldBe([0x05, 0x28, 0x02]);
    }

    [Fact]
    public void A_field_going_backwards_writes_its_id_in_full()
    {
        Struct().Int(5, 1).Int(2, 1).ToArray().ShouldBe([0x55, 0x02, 0x05, 0x04, 0x02]);
    }

    [Fact]
    public void A_nested_struct_counts_fields_from_zero_and_restores_the_outer_count()
    {
        var bytes = Struct().Int(3, 1).StructField(4).Int(1, 1).EndStruct().Int(5, 1).ToArray();

        bytes.ShouldBe([0x35, 0x02, 0x1C, 0x15, 0x02, End, 0x15, 0x02]);
    }

    [Fact]
    public void A_short_list_holds_its_size_in_the_type_byte()
    {
        Struct().IntList(1, [1, 2]).ToArray().ShouldBe([0x19, 0x25, 0x02, 0x04]);
    }

    [Fact]
    public void A_string_list_holds_its_strings_in_order()
    {
        Struct().StringList(1, ["a", "b"]).ToArray().ShouldBe([0x19, 0x28, 0x01, 0x61, 0x01, 0x62]);
    }

    [Fact]
    public void A_list_of_fifteen_or_more_writes_its_size_after_the_type()
    {
        Struct().StructList(1, 15).ToArray().ShouldBe([0x19, 0xFC, 0x0F]);
    }

    [Fact]
    public void A_list_of_fourteen_still_fits_the_type_byte()
    {
        Struct().StructList(1, 14).ToArray().ShouldBe([0x19, 0xEC]);
    }
}
