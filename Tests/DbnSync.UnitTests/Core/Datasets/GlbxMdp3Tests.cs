using DbnSync.Core;
using Shouldly;
using static DbnSync.UnitTests.TestData;

namespace DbnSync.UnitTests.Core.Datasets;

public sealed class GlbxMdp3Tests : DatasetProfileConformance
{
    private static readonly DayRange Week = new(Day(10, 26), Day(10, 30));

    protected override IDatasetProfile Profile => GlbxMdp3.Instance;

    protected override IReadOnlyList<string> Groups => ["ES", "MNQ", "6E", "ZN", "CL"];

    protected override IReadOnlyList<string> NotGroups => ["es", "ES.FUT", "TOOLONG"];

    [Fact]
    public void Is_verified()
    {
        Profile.Status.ShouldBe(ProfileStatus.Verified);
    }

    [Fact]
    public void Asks_for_a_parent_schema_by_the_root_and_its_spreads()
    {
        var request = Profile.Request(new JobRequest(Root, Spec(Trades), Week), depth: 2);

        request.StypeIn.ShouldBe("parent");
        request.Symbols.ShouldBe([$"{Root}.FUT"]);
    }

    [Fact]
    public void Asks_for_a_book_schema_by_the_leading_outrights()
    {
        var request = Profile.Request(new JobRequest(Root, Spec(Mbo), Week), depth: 3);

        request.StypeIn.ShouldBe("continuous");
        request.Symbols.ShouldBe([$"{Root}.n.0", $"{Root}.n.1", $"{Root}.n.2"]);
    }

    [Fact]
    public void Includes_a_year_of_L1_and_a_month_of_book_less_a_day_of_margin()
    {
        Profile.Included.ShouldBe(new Entitlement(364, 31));
    }

    [Fact]
    public void Starts_when_Databento_s_CME_history_does()
    {
        Profile.Start.ShouldBe(new DateOnly(2010, 6, 6));
    }
}
