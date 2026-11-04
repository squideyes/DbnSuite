using DbnClient.Export;
using DbnClient.Time;
using Shouldly;

namespace DbnSync.UnitTests.ClientTests;

public sealed class CellFormatTests
{
    private const string Chicago = "America/Chicago";
    private const string NewYork = "America/New_York";

    /// <summary>2026-10-28 13:30:00.123456789 UTC, which is 08:30 in Chicago, with its offset of -05:00.</summary>
    private const ulong Summer = 1_793_194_200_123_456_789;

    private static readonly Column TimeColumn = new("ts_event", CellKind.Time);
    private static readonly Column PriceColumn = new("price", CellKind.Price);
    private static readonly Column NumberColumn = new("size", CellKind.Integer);
    private static readonly Column TextColumn = new("symbol", CellKind.Text);

    private static TimeZoneInfo Zone(string name) => Zones.Find(name).Value;

    private static List<string> Written(CellFormat format, Column column, Cell cell)
    {
        var values = new List<string>();

        format.Write(column, cell, values);

        return values;
    }

    [Fact]
    public void Iso_is_in_the_zone_with_nine_digits_and_the_offset()
    {
        var format = new CellFormat(Zone(Chicago), TimeMode.Iso, splitTime: false);

        format.TimeText(Summer).ShouldBe("2026-10-28T08:30:00.123456789-05:00");
    }

    [Fact]
    public void Iso_follows_the_zone_asked_for()
    {
        var format = new CellFormat(Zone(NewYork), TimeMode.Iso, splitTime: false);

        format.TimeText(Summer).ShouldBe("2026-10-28T09:30:00.123456789-04:00");
    }

    [Fact]
    public void Unix_nanoseconds_are_the_raw_time_in_any_zone()
    {
        var format = new CellFormat(Zone(Chicago), TimeMode.UnixNanos, splitTime: false);

        format.TimeText(Summer).ShouldBe("1793194200123456789");
    }

    [Fact]
    public void Unix_milliseconds_are_truncated_not_rounded()
    {
        var format = new CellFormat(Zone(Chicago), TimeMode.UnixMillis, splitTime: false);

        format.TimeText(1_793_194_200_999_999_999).ShouldBe("1793194200999");
    }

    [Fact]
    public void Excel_counts_days_from_1899_12_30_in_the_zone()
    {
        var chicago = new CellFormat(Zone(Chicago), TimeMode.Excel, splitTime: false);
        var utc = new CellFormat(TimeZoneInfo.Utc, TimeMode.Excel, splitTime: false);
        var noonUtc = DbnBytes.Utc(2026, 10, 28, 12);
        var sixPmUtc = DbnBytes.Utc(2026, 10, 28, 23);

        utc.TimeText(noonUtc).ShouldBe("46323.5");
        chicago.TimeText(sixPmUtc).ShouldBe("46323.75");
    }

    [Theory]
    [InlineData(TimeMode.Iso, false)]
    [InlineData(TimeMode.UnixNanos, true)]
    [InlineData(TimeMode.UnixMillis, true)]
    [InlineData(TimeMode.Excel, true)]
    public void Only_iso_is_not_a_number(TimeMode time, bool number)
    {
        new CellFormat(TimeZoneInfo.Utc, time, splitTime: false).TimeIsNumber.ShouldBe(number);
    }

    [Fact]
    public void A_split_time_is_two_columns_without_the_offset()
    {
        var format = new CellFormat(Zone(Chicago), TimeMode.Iso, splitTime: true);

        Written(format, TimeColumn, Cell.Of(Summer)).ShouldBe(["2026-10-28", "08:30:00.123456789"]);
    }

    [Fact]
    public void An_empty_split_time_is_two_empty_fields()
    {
        var format = new CellFormat(Zone(Chicago), TimeMode.Iso, splitTime: true);

        Written(format, TimeColumn, Cell.Empty).ShouldBe(["", ""]);
    }

    [Fact]
    public void An_empty_time_is_one_empty_field()
    {
        var format = new CellFormat(Zone(Chicago), TimeMode.Iso, splitTime: false);

        Written(format, TimeColumn, Cell.Empty).ShouldBe([""]);
    }

    [Theory]
    [InlineData(TimeMode.UnixNanos)]
    [InlineData(TimeMode.UnixMillis)]
    [InlineData(TimeMode.Excel)]
    public void Only_iso_can_split(TimeMode time)
    {
        var format = new CellFormat(Zone(Chicago), time, splitTime: true);

        format.SplitTime.ShouldBeFalse();
        Written(format, TimeColumn, Cell.Of(Summer)).Count.ShouldBe(1);
    }

    [Fact]
    public void Split_names_are_date_and_time_and_only_times_split()
    {
        var format = new CellFormat(Zone(Chicago), TimeMode.Iso, splitTime: true);

        format.Names([TimeColumn, TextColumn]).ShouldBe(["ts_event_date", "ts_event_time", "symbol"]);
    }

    [Fact]
    public void Names_stay_as_they_are_without_a_split()
    {
        var format = new CellFormat(Zone(Chicago), TimeMode.Iso, splitTime: false);

        format.Names([TimeColumn, TextColumn]).ShouldBe(["ts_event", "symbol"]);
    }

    [Theory]
    [InlineData("6876.25", "6876.25")]
    [InlineData("6876.2500", "6876.25")]
    [InlineData("0.000000001", "0.000000001")]
    [InlineData("100", "100")]
    [InlineData("-37.63", "-37.63")]
    public void Prices_are_plain_decimals_with_up_to_nine_places(string price, string written)
    {
        CellFormat.Price(decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture)).ShouldBe(written);
    }

    [Fact]
    public void A_very_large_price_has_no_exponent()
    {
        CellFormat.Price(6_876_250_000_000m).ShouldBe("6876250000000");
    }

    [Fact]
    public void No_price_is_an_empty_field()
    {
        var format = new CellFormat(TimeZoneInfo.Utc, TimeMode.Iso, splitTime: false);

        Written(format, PriceColumn, Cell.Of((decimal?)null)).ShouldBe([""]);
    }

    [Fact]
    public void Integers_are_plain_numbers()
    {
        var format = new CellFormat(TimeZoneInfo.Utc, TimeMode.Iso, splitTime: false);

        Written(format, NumberColumn, Cell.Of(-1234567L)).ShouldBe(["-1234567"]);
    }

    [Fact]
    public void Text_is_written_as_it_is_and_none_as_an_empty_field()
    {
        var format = new CellFormat(TimeZoneInfo.Utc, TimeMode.Iso, splitTime: false);

        Written(format, TextColumn, Cell.Of("ESZ26")).ShouldBe(["ESZ26"]);
        Written(format, TextColumn, Cell.Of((string?)null)).ShouldBe([""]);
    }

    [Fact]
    public void The_zone_and_style_are_kept()
    {
        var zone = Zone(Chicago);

        var format = new CellFormat(zone, TimeMode.Excel, splitTime: false);

        format.Zone.ShouldBe(zone);
        format.Time.ShouldBe(TimeMode.Excel);
    }
}
