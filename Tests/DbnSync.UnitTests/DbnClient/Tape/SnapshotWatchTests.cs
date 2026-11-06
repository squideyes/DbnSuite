using DbnClient.Tape;
using Shouldly;

namespace DbnSync.UnitTests.TapeFormatTests;

public sealed class SnapshotWatchTests
{
    private static readonly (decimal?, long?, decimal?, long?) Empty = default;
    private static readonly (decimal?, long?, decimal?, long?) Top = (100m, 1, 101m, 2);
    private static readonly (decimal?, long?, decimal?, long?) OtherTop = (100m, 5, 101m, 2);

    [Fact]
    public void TheFirstClearHasNothingToCompare()
    {
        var watch = new SnapshotWatch();

        Clear(watch, Empty);
        Order(watch, Empty);
        Live(watch, Top);

        watch.Changes.ShouldBe(0);
    }

    [Fact]
    public void ASnapshotThatKeepsTheTopIsNoChange()
    {
        var watch = Replayed();

        Clear(watch, Top);
        Order(watch, Empty);
        Live(watch, Top);

        watch.Changes.ShouldBe(0);
    }

    [Fact]
    public void ASnapshotThatMovesTheTopIsOneChange()
    {
        var watch = Replayed();

        Clear(watch, Top);
        Order(watch, Empty);
        Live(watch, OtherTop);

        watch.Changes.ShouldBe(1);
    }

    [Fact]
    public void AFileHoldingOnlyASnapshotStillGivesTheNextClearABook()
    {
        var watch = new SnapshotWatch();

        Clear(watch, Empty);
        Order(watch, Empty);
        Clear(watch, Top);
        Order(watch, Empty);
        Live(watch, Top);

        watch.Changes.ShouldBe(0);
    }

    [Fact]
    public void ASnapshotBeforeTheOpenIsNotCounted()
    {
        var watch = Replayed();

        Clear(watch, Top);
        Order(watch, Empty);
        watch.See(clear: false, snapshot: false, afterOpen: false, OtherTop);

        watch.Changes.ShouldBe(0);
    }

    private static SnapshotWatch Replayed()
    {
        var watch = new SnapshotWatch();

        Clear(watch, Empty);
        Order(watch, Empty);
        Live(watch, Top);

        return watch;
    }

    private static void Clear(SnapshotWatch watch, (decimal?, long?, decimal?, long?) top) =>
        watch.See(clear: true, snapshot: true, afterOpen: false, top);

    private static void Order(SnapshotWatch watch, (decimal?, long?, decimal?, long?) top) =>
        watch.See(clear: false, snapshot: true, afterOpen: false, top);

    private static void Live(SnapshotWatch watch, (decimal?, long?, decimal?, long?) top) =>
        watch.See(clear: false, snapshot: false, afterOpen: true, top);
}
