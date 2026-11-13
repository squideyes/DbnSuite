using DbnSuite.Sync.Core;
using Shouldly;

namespace DbnSuite.UnitTests.Core.Datasets;

public sealed class DatasetProfilesTests
{
    [Fact]
    public void Every_registered_profile_has_a_conformance_test()
    {
        var tested = typeof(DatasetProfileConformance).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && t.IsSubclassOf(typeof(DatasetProfileConformance)))
            .Select(t => ((IDatasetProfile)t.GetProperty("Profile",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(Activator.CreateInstance(t))!).Dataset)
            .ToHashSet(StringComparer.Ordinal);

        DatasetProfiles.All.Keys.ShouldBeSubsetOf(tested);
    }

    [Fact]
    public void A_dataset_is_found_by_its_exact_code()
    {
        DatasetProfiles.Find("GLBX.MDP3").ShouldBeSameAs(GlbxMdp3.Instance);
    }

    [Theory]
    [InlineData("glbx.mdp3")]
    [InlineData("XNAS.ITCH")]
    [InlineData("")]
    public void A_dataset_without_a_profile_is_not_found(string dataset)
    {
        DatasetProfiles.Find(dataset).ShouldBeNull();
    }

    [Fact]
    public void Options_for_a_dataset_without_a_profile_refuse_to_plan()
    {
        var options = new SyncOptions { Dataset = "XNAS.ITCH" };

        Should.Throw<InvalidOperationException>(() => options.Profile());
    }
}
