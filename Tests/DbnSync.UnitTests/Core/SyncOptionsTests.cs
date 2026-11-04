using DbnSync.Core;
using Shouldly;
using static DbnSync.UnitTests.TestData;

namespace DbnSync.UnitTests.Core;

public sealed class SyncOptionsTests
{
    [Fact]
    public void Lists_are_trimmed_cased_and_deduplicated()
    {
        var options = new SyncOptions { Roots = " mnq, MCL ,MNQ", Schemas = "MBO, trades" };

        options.RootList.ShouldBe([Root, OtherRoot]);
        options.SchemaList.ShouldBe([Mbo, Trades]);
    }

    [Fact]
    public void RootsBySchema_narrows_one_schema_only()
    {
        var options = new SyncOptions
        {
            Roots = $"{Root},{OtherRoot}",
            Schemas = $"{Mbo},{Trades}",
            RootsBySchema = new() { [Mbo] = Root }
        };

        var wanted = options.Wanted().Select(w => $"{w.Schema.Name}:{w.Root}").ToList();

        wanted.ShouldBe([$"{Mbo}:{Root}", $"{Trades}:{Root}", $"{Trades}:{OtherRoot}"]);
    }

    [Fact]
    public void Without_ReachBackTo_the_book_keeps_to_the_plan()
    {
        var options = new SyncOptions();

        options.Window(Level.L3, Today).ShouldBe((Day(10, 1), Day(10, 1)));
    }

    [Fact]
    public void ReachBackTo_extends_the_book_before_the_plan()
    {
        var options = new SyncOptions { ReachBackTo = Day(6, 1) };

        options.Window(Level.L3, Today).ShouldBe((Day(6, 1), Day(10, 1)));
    }

    [Fact]
    public void ReachBackTo_never_moves_L0_or_reaches_past_HistoryStart()
    {
        var options = new SyncOptions { HistoryStart = Day(8, 1), ReachBackTo = Day(6, 1) };

        options.Window(Level.L0, Today).ShouldBe((Day(8, 1), Day(8, 1)));
        options.Window(Level.L3, Today).ShouldBe((Day(8, 1), Day(10, 1)));
    }

    [Fact]
    public void A_ReachBackTo_inside_the_plan_changes_nothing()
    {
        var options = new SyncOptions { ReachBackTo = Day(10, 15) };

        options.Window(Level.L3, Today).ShouldBe((Day(10, 1), Day(10, 1)));
    }

    [Fact]
    public void A_dataset_with_no_profile_cannot_give_one()
    {
        var options = new SyncOptions { Dataset = "XNAS.ITCH" };

        Should.Throw<InvalidOperationException>(options.Profile).Message.ShouldContain("XNAS.ITCH");
    }

    [Fact]
    public void A_schema_the_profile_does_not_keep_is_passed_over()
    {
        var options = new SyncOptions { Roots = Root, Schemas = $"imbalance,{Mbo}" };

        options.Wanted().Select(w => w.Schema.Name).ShouldBe([Mbo]);
    }
}
