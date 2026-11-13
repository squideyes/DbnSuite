using Bunit;
using DbnSuite.Sync.Worker.Components.Shared;
using Shouldly;

namespace DbnSuite.UnitTests.Worker;

public sealed class StatTileTests : BunitContext
{
    private const string Label = "Stored";
    private const string Value = "4 KB";
    private const string Note = "3 day files";
    private const string Href = "activity";

    [Fact]
    public void Shows_its_label_value_and_note()
    {
        var tile = Render<StatTile>(p => p.Add(t => t.Label, Label).Add(t => t.Value, Value).Add(t => t.Note, Note));

        tile.Find("div.tile .label").TextContent.ShouldBe(Label);
        tile.Find(".value").TextContent.ShouldBe(Value);
        tile.Find(".note").TextContent.ShouldBe(Note);
    }

    [Fact]
    public void Leaves_the_note_out_when_there_is_none()
    {
        var tile = Render<StatTile>(p => p.Add(t => t.Label, Label).Add(t => t.Value, Value));

        tile.FindAll(".note").ShouldBeEmpty();
    }

    [Fact]
    public void Is_a_link_when_it_has_somewhere_to_go()
    {
        var tile = Render<StatTile>(p => p.Add(t => t.Label, Label).Add(t => t.Value, Value).Add(t => t.Href, Href));

        tile.Find("a.tile-link").GetAttribute("href").ShouldBe(Href);
    }
}
