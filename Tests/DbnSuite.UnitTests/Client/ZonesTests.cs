using DbnSuite.Client.Time;
using Shouldly;

namespace DbnSuite.UnitTests.ClientTests;

public sealed class ZonesTests
{
    /// <summary>2026-10-28 13:30:00.123456789 UTC, during US daylight time.</summary>
    private const ulong Summer = 1_793_194_200_123_456_789;

    /// <summary>2026-12-01 14:30:00.000000001 UTC, after the clocks went back.</summary>
    private const ulong Winter = 1_796_135_400_000_000_001;

    [Fact]
    public void Chicago_is_the_default()
    {
        Zones.Default.ShouldBe("America/Chicago");
    }

    [Fact]
    public void A_time_keeps_nine_digits_and_its_offset()
    {
        var chicago = Zones.Find(Zones.Default).Value;

        Zones.Format(Summer, chicago).ShouldBe("2026-10-28T08:30:00.123456789-05:00");
    }

    [Fact]
    public void Daylight_saving_is_handled_per_time()
    {
        var chicago = Zones.Find(Zones.Default).Value;

        Zones.Format(Winter, chicago).ShouldBe("2026-12-01T08:30:00.000000001-06:00");
    }

    [Fact]
    public void Utc_ends_in_z()
    {
        Zones.Format(Summer, Zones.Find("utc").Value).ShouldBe("2026-10-28T13:30:00.123456789Z");
    }

    [Fact]
    public void An_unknown_zone_is_refused()
    {
        Zones.Find("Mars/Olympus_Mons").Error!.Code.ShouldBe("input.bad_zone");
    }

    [Fact]
    public void Local_is_this_machines_own_zone()
    {
        Zones.Find("LOCAL").Value.ShouldBe(TimeZoneInfo.Local);
    }

    [Fact]
    public void A_zone_east_of_greenwich_has_a_plus_and_its_minutes()
    {
        Zones.Format(Summer, Zones.Find("Asia/Kolkata").Value).ShouldBe("2026-10-28T19:00:00.123456789+05:30");
    }

    [Fact]
    public void A_zone_at_zero_offset_that_is_not_utc_keeps_its_offset()
    {
        Zones.Format(Winter, Zones.Find("Europe/London").Value).ShouldBe("2026-12-01T14:30:00.000000001+00:00");
    }
}
