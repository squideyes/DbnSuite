using Bunit;
using DbnSuite.Sync.Core;
using DbnSuite.Sync.Worker.Components.Shared;
using Shouldly;
using static DbnSuite.UnitTests.TestData;

namespace DbnSuite.UnitTests.Worker;

public sealed class HealthTests : BunitContext
{
    private const long FileBytes = 1_000;
    private static readonly IReadOnlyList<DayRange> NothingPending = [];

    [Fact]
    public void Lost_days_outrank_owed_ones()
    {
        IReadOnlyList<StoredDay> stored = [new(Day(9, 1), FileBytes, Empty: false)];
        var series = Corpus.Build(Root, Spec(Mbo), stored, NothingPending, Today, Day(10, 31), new DateOnly(2020, 1, 1));

        var chip = Render<Health>(p => p.Add(h => h.Series, series));

        chip.Find(".chip").ClassList.ShouldContain("critical");
        chip.Markup.ShouldContain("lost");
    }

    [Fact]
    public void Owed_days_outrank_pending_ones()
    {
        IReadOnlyList<DayRange> pending = [new(Day(10, 1), Day(10, 2))];
        var series = Corpus.Build(Root, Spec(Mbo), [], pending, Today, Day(10, 31), new DateOnly(2020, 1, 1));

        var chip = Render<Health>(p => p.Add(h => h.Series, series));

        chip.Find(".chip").ClassList.ShouldContain("warning");
        chip.Markup.ShouldContain("29 owed");
    }

    [Fact]
    public void With_only_pending_days_left_it_says_so()
    {
        IReadOnlyList<DayRange> pending = [new(Day(10, 1), Day(10, 31))];
        var series = Corpus.Build(Root, Spec(Mbo), [], pending, Today, Day(10, 31), new DateOnly(2020, 1, 1));

        var chip = Render<Health>(p => p.Add(h => h.Series, series));

        chip.Find(".chip").ClassList.ShouldContain("warning");
        chip.Markup.ShouldContain("31 pending");
    }

    [Fact]
    public void A_series_with_every_day_accounted_for_is_complete()
    {
        var window = new DayRange(Day(10, 1), Day(10, 31));
        IReadOnlyList<StoredDay> stored = [.. window.Each().Select(d => new StoredDay(d, FileBytes, Empty: false))];
        var series = Corpus.Build(Root, Spec(Mbo), stored, NothingPending, Today, Day(10, 31), new DateOnly(2020, 1, 1));

        var chip = Render<Health>(p => p.Add(h => h.Series, series));

        chip.Find(".chip").ClassList.ShouldContain("good");
        chip.Markup.ShouldContain("complete");
    }
}
