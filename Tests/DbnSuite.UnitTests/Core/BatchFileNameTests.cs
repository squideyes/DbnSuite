using DbnSuite.Sync.Core;
using Shouldly;
using static DbnSuite.UnitTests.TestData;

namespace DbnSuite.UnitTests.Core;

public sealed class BatchFileNameTests
{
    [Fact]
    public void A_daily_file_names_its_day()
    {
        var name = FileName(Day(10, 28), Mbo);

        BatchFileName.DayOf(name).ShouldBe(Day(10, 28));
    }

    [Fact]
    public void A_range_file_has_no_single_day()
    {
        const string name = "glbx-mdp3-20260901-20261027.statistics.dbn.zst";

        BatchFileName.DayOf(name).ShouldBeNull();
    }

    [Theory]
    [InlineData("manifest.json", true)]
    [InlineData("condition.json", true)]
    [InlineData("glbx-mdp3-20261028.mbo.dbn.zst", false)]
    public void Json_files_are_support_documents(string name, bool support)
    {
        BatchFileName.IsSupport(name).ShouldBe(support);
    }
}
