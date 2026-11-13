using DbnSuite.Sync.Core;
using DbnSuite.Sync.Worker;
using Shouldly;
using static DbnSuite.UnitTests.TestData;

namespace DbnSuite.UnitTests.Worker;

public sealed class FormatTests
{
    private const DayState Undefined = (DayState)99;

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(999, "999 B")]
    [InlineData(1_000, "1 KB")]
    [InlineData(1_500, "1.5 KB")]
    [InlineData(2_500_000, "2.5 MB")]
    [InlineData(7_000_000_000, "7 GB")]
    [InlineData(3_000_000_000_000, "3 TB")]
    [InlineData(4_000_000_000_000_000, "4000 TB")]
    public void Bytes_scale_in_thousands_and_stop_at_terabytes(long bytes, string text)
    {
        Format.Bytes(bytes).ShouldBe(text);
    }

    [Fact]
    public void Counts_group_their_thousands_whatever_the_culture()
    {
        Format.Count(1_234_567).ShouldBe("1,234,567");
    }

    [Fact]
    public void Days_read_year_first()
    {
        Format.Day(Day(10, 3)).ShouldBe("2026-10-03");
    }

    [Fact]
    public void Times_are_shown_in_utc_whatever_their_offset()
    {
        var chicago = new DateTimeOffset(2026, 11, 1, 7, 0, 0, TimeSpan.FromHours(-5));

        Format.Time(chicago).ShouldBe("2026-11-01 12:00Z");
    }

    [Theory]
    [InlineData(0, "now")]
    [InlineData(20, "now")]
    [InlineData(-180, "3 min ago")]
    [InlineData(240, "in 4 min")]
    [InlineData(-5_340, "89 min ago")]
    [InlineData(5_400, "in 1 h")]
    [InlineData(-169_200, "47 h ago")]
    [InlineData(172_800, "in 2 d")]
    [InlineData(-864_000, "10 d ago")]
    public void Relative_times_grow_coarser_with_distance(int seconds, string text)
    {
        Format.Relative(Now.AddSeconds(seconds), Now).ShouldBe(text);
    }

    [Theory]
    [InlineData(DayState.Held, "Stored")]
    [InlineData(DayState.Empty, "No data")]
    [InlineData(DayState.Pending, "Pending")]
    [InlineData(DayState.Owed, "Owed")]
    [InlineData(DayState.Lost, "Lost")]
    [InlineData(Undefined, "99")]
    public void States_have_their_dashboard_names(DayState state, string name)
    {
        Format.Name(state).ShouldBe(name);
    }

    [Fact]
    public void A_state_s_css_class_is_its_name_in_lower_case()
    {
        Format.Css(DayState.Held).ShouldBe("state-held");
    }
}
