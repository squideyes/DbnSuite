using DbnSuite.Client.Archive;
using DbnSuite.Sync.Core;
using Shouldly;
using static DbnSuite.UnitTests.TestData;

namespace DbnSuite.UnitTests.ClientTests;

/// <summary>The library reads the layout the worker writes; these keep the two from drifting apart.</summary>
public sealed class DbnSyncLayoutTests
{
    [Fact]
    public void Data_keys_match_the_workers()
    {
        DbnSyncLayout.Data(Dataset, Mbo, Root, Day(10, 28)).ShouldBe(ArchiveKeys.Data(Dataset, Mbo, Root, Day(10, 28)));
    }

    [Fact]
    public void Empty_keys_match_the_workers()
    {
        DbnSyncLayout.Empty(Dataset, Mbo, Root, Day(10, 31)).ShouldBe(ArchiveKeys.Empty(Dataset, Mbo, Root, Day(10, 31)));
    }

    [Fact]
    public void Folders_match_the_workers()
    {
        DbnSyncLayout.Folder(Dataset, Mbo, Root).ShouldBe(ArchiveKeys.Folder(Dataset, Mbo, Root));
    }

    [Fact]
    public void A_key_gives_back_its_day()
    {
        DbnSyncLayout.DayOf(DbnSyncLayout.Data(Dataset, Mbo, Root, Day(10, 28))).ShouldBe(Day(10, 28));
        DbnSyncLayout.DayOf(DbnSyncLayout.Empty(Dataset, Mbo, Root, Day(10, 31))).ShouldBe(Day(10, 31));
    }

    [Fact]
    public void Other_keys_have_no_day()
    {
        DbnSyncLayout.DayOf(DbnSyncLayout.LayoutKey).ShouldBeNull();
    }

    [Theory]
    [InlineData("GLBX.MDP3/mbo/MNQ/2026/MNQ.mbo.20261332.dbn.zst")]
    [InlineData("GLBX.MDP3/mbo/MNQ/2026/MNQ.mbo.latest.empty")]
    public void A_name_with_no_real_date_has_no_day(string key)
    {
        DbnSyncLayout.DayOf(key).ShouldBeNull();
    }

    [Fact]
    public void An_empty_marker_is_told_from_a_data_file()
    {
        DbnSyncLayout.IsEmpty(DbnSyncLayout.Empty(Dataset, Mbo, Root, Day(10, 31))).ShouldBeTrue();
        DbnSyncLayout.IsEmpty(DbnSyncLayout.Data(Dataset, Mbo, Root, Day(10, 31))).ShouldBeFalse();
    }
}
