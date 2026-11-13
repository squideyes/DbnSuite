using Bunit;
using DbnSuite.Sync.Core;
using DbnSuite.Sync.Worker;
using DbnSuite.Sync.Worker.Components.Shared;
using Shouldly;

namespace DbnSuite.UnitTests.Worker;

public sealed class StateLegendTests : BunitContext
{
    private const DayState Undefined = (DayState)99;

    [Fact]
    public void Names_and_explains_every_state()
    {
        var legend = Render<StateLegend>();

        var entries = legend.FindAll(".legend span");
        entries.Select(e => e.TextContent).ShouldBe(Enum.GetValues<DayState>().Select(Format.Name));
        entries.ShouldAllBe(e => !string.IsNullOrEmpty(e.GetAttribute("title")));
    }

    [Fact]
    public void A_state_it_has_no_words_for_is_left_blank()
    {
        StateLegend.Explain(Undefined).ShouldBeEmpty();
    }
}
