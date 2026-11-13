using System.Globalization;
using DbnSuite.Fetch;
using Shouldly;
using static DbnSuite.UnitTests.TestData;

namespace DbnSuite.UnitTests.FetchTests;

public sealed class TextTests
{
    private const string NoDay = "-";
    private const string German = "de-DE";
    private const string FirstHeader = "Name";
    private const string SecondHeader = "Size";
    private const string LongName = "A-long-name";
    private const string ShortName = "B";
    private const string Wide = "1,000";
    private const string Narrow = "7";

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(999, "999 B")]
    [InlineData(1_000, "1.0 kB")]
    [InlineData(999_900, "999.9 kB")]
    [InlineData(1_000_000, "1.0 MB")]
    [InlineData(1_500_000_000, "1.5 GB")]
    [InlineData(2_340_000_000_000, "2.34 TB")]
    public void Sizes_are_in_decimal_units(long bytes, string expected)
    {
        Text.Bytes(bytes).ShouldBe(expected);
    }

    [Fact]
    public void A_count_has_thousands_separators()
    {
        Text.Count(1_234_567).ShouldBe("1,234,567");
    }

    [Fact]
    public void A_day_is_written_year_first()
    {
        Text.Day(Day(10, 2)).ShouldBe("2026-10-02");
    }

    [Fact]
    public void A_time_is_written_as_its_utc_day()
    {
        var evening = new DateTimeOffset(2026, 10, 2, 22, 0, 0, TimeSpan.FromHours(-5));

        Text.Day(evening).ShouldBe("2026-10-03");
    }

    [Fact]
    public void No_time_is_a_dash()
    {
        Text.Day((DateTimeOffset?)null).ShouldBe(NoDay);
    }

    [Fact]
    public void Invariant_ignores_the_machines_culture()
    {
        var culture = CultureInfo.CurrentCulture;

        CultureInfo.CurrentCulture = new CultureInfo(German);

        try
        {
            Text.Invariant($"{1.5m}").ShouldBe("1.5");
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void Columns_are_as_wide_as_their_widest_cell()
    {
        var lines = Text.Table([FirstHeader, SecondHeader], [[LongName, Wide], [ShortName, Narrow]]).ToList();

        lines.ShouldBe(["Name         Size", "A-long-name  1,000", "B            7"]);
    }

    [Fact]
    public void Right_aligned_columns_line_up_on_the_right()
    {
        var lines = Text.Table([FirstHeader, SecondHeader], [[LongName, Wide], [ShortName, Narrow]], [false, true]).ToList();

        lines.ShouldBe(["Name          Size", "A-long-name  1,000", "B                7"]);
    }

    [Fact]
    public void A_table_with_no_rows_is_its_header()
    {
        Text.Table([FirstHeader, SecondHeader], []).ShouldBe(["Name  Size"]);
    }
}
