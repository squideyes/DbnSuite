using DbnClient.Export;
using Shouldly;

namespace DbnSync.UnitTests.ClientTests;

public sealed class ColumnsTests
{
    private const string Text = "ESZ26";

    [Fact]
    public void A_number_is_held_as_a_number()
    {
        Cell.Of(7L).ShouldBe(new Cell(7, null, null));
    }

    [Fact]
    public void A_time_keeps_all_its_bits()
    {
        Cell.Of(1_793_194_200_123_456_789UL).Number.ShouldBe(1_793_194_200_123_456_789);
    }

    [Fact]
    public void A_price_and_text_are_held_as_themselves()
    {
        Cell.Of(100.25m).Price.ShouldBe(100.25m);
        Cell.Of(Text).Text.ShouldBe(Text);
    }

    [Fact]
    public void Empty_is_the_only_empty_cell()
    {
        Cell.Empty.IsEmpty.ShouldBeTrue();
        Cell.Of(0L).IsEmpty.ShouldBeFalse();
        Cell.Of(long.MinValue).IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void A_value_beside_the_empty_number_is_not_empty()
    {
        new Cell(long.MinValue, 1m, null).IsEmpty.ShouldBeFalse();
        new Cell(long.MinValue, null, Text).IsEmpty.ShouldBeFalse();
    }
}
