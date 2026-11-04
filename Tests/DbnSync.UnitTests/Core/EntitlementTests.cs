using DbnSync.Core;
using Shouldly;
using static DbnSync.UnitTests.TestData;

namespace DbnSync.UnitTests.Core;

public sealed class EntitlementTests
{
    private static readonly DateOnly HistoryStart = new(2020, 1, 1);

    [Theory]
    [InlineData(Level.L0, 2020, 1, 1)]
    [InlineData(Level.L1, 2025, 11, 2)]
    [InlineData(Level.L2, 2026, 10, 1)]
    [InlineData(Level.L3, 2026, 10, 1)]
    public void Standard_windows_sit_a_day_inside_the_measured_limits(
        Level level, int year, int month, int day)
    {
        var earliest = GlbxMdp3.Instance.Included.EarliestFor(level, Today, HistoryStart);

        earliest.ShouldBe(new DateOnly(year, month, day));
    }

    [Fact]
    public void History_start_bounds_every_level()
    {
        var historyStart = Day(10, 20);

        var earliest = GlbxMdp3.Instance.Included.EarliestFor(Level.L1, Today, historyStart);

        earliest.ShouldBe(historyStart);
    }

    [Theory]
    [InlineData("2026-11-01T20:24:28Z", 2026, 10, 31)]
    [InlineData("2026-11-02T00:00:00Z", 2026, 11, 1)]
    public void Only_whole_days_are_complete(string end, int year, int month, int day)
    {
        var last = Entitlement.LastCompleteDay(DateTimeOffset.Parse(end, System.Globalization.CultureInfo.InvariantCulture));

        last.ShouldBe(new DateOnly(year, month, day));
    }
}
