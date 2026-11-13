using DbnSuite.Sync.Core;
using Shouldly;
using static DbnSuite.UnitTests.TestData;

namespace DbnSuite.UnitTests.Core;

public sealed class ArchiveKeysTests
{
    [Fact]
    public void Data_key_is_dataset_schema_root_year_then_day()
    {
        var key = ArchiveKeys.Data(Dataset, Mbo, Root, Day(10, 28));

        key.ShouldBe("GLBX.MDP3/mbo/MNQ/2026/MNQ.mbo.20261028.dbn.zst");
    }

    [Fact]
    public void Data_and_empty_keys_read_back_as_their_day()
    {
        var data = ArchiveKeys.Data(Dataset, Mbo, Root, Day(10, 28));
        var empty = ArchiveKeys.Empty(Dataset, Mbo, Root, Day(10, 31));

        ArchiveKeys.DayOf(data).ShouldBe(Day(10, 28));
        ArchiveKeys.DayOf(empty).ShouldBe(Day(10, 31));
    }

    [Fact]
    public void A_pass_lands_in_its_utc_day_folder()
    {
        var key = ArchiveKeys.Pass(new DateTimeOffset(2026, 11, 1, 23, 30, 0, TimeSpan.FromHours(-5)));

        key.ShouldBe("_dbnsync/passes/2026-11-02/043000000.json");
    }

    [Fact]
    public void A_job_key_is_not_a_day()
    {
        var key = ArchiveKeys.Job(JobId);

        ArchiveKeys.DayOf(key).ShouldBeNull();
    }
}
