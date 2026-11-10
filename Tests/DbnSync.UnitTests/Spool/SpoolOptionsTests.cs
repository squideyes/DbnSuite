using DbnClient.Archive;
using DbnSpool;
using Shouldly;

namespace DbnSync.UnitTests.SpoolTests;

public sealed class SpoolOptionsTests
{
    private const string Endpoint = "https://nyc3.digitaloceanspaces.com";
    private const string ArchiveBucket = "dbn";
    private const string TapesBucket = "dbn-tapes";

    private static ArchiveOptions Store(string location, string bucket = ArchiveBucket, string prefix = "") =>
        new() { Location = location, Bucket = bucket, Prefix = prefix };

    [Fact]
    public void The_defaults_are_sound()
    {
        new SpoolOptions().Problem().ShouldBeNull();
    }

    [Fact]
    public void No_roots_is_a_problem()
    {
        new SpoolOptions { Roots = " , " }.Problem()!.ShouldContain("Roots");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(31 * 24 * 3600)]
    public void An_interval_under_a_minute_or_over_a_month_is_a_problem(int seconds)
    {
        new SpoolOptions { Interval = TimeSpan.FromSeconds(seconds) }.Problem()!.ShouldContain("Interval");
    }

    [Fact]
    public void No_attempts_at_all_is_a_problem()
    {
        new SpoolOptions { MostAttempts = 0 }.Problem()!.ShouldContain("MostAttempts");
    }

    [Fact]
    public void No_work_folder_is_a_problem()
    {
        new SpoolOptions { WorkPath = "" }.Problem()!.ShouldContain("WorkPath");
    }

    [Fact]
    public void The_same_bucket_at_the_same_address_is_the_same_place()
    {
        SpoolOptions.SamePlace(Store(Endpoint), Store(Endpoint + "/")).ShouldBeTrue();
    }

    [Fact]
    public void Another_bucket_is_another_place()
    {
        SpoolOptions.SamePlace(Store(Endpoint), Store(Endpoint, TapesBucket)).ShouldBeFalse();
    }

    [Fact]
    public void Another_prefix_is_another_place()
    {
        SpoolOptions.SamePlace(Store(Endpoint), Store(Endpoint, prefix: "tapes")).ShouldBeFalse();
    }

    [Fact]
    public void The_same_folder_is_the_same_place_whatever_the_bucket()
    {
        var folder = Path.Combine(Path.GetTempPath(), "dbn-archive");

        SpoolOptions.SamePlace(Store(folder), Store(folder + Path.DirectorySeparatorChar, TapesBucket)).ShouldBeTrue();
    }

    [Fact]
    public void A_folder_and_an_address_are_different_places()
    {
        SpoolOptions.SamePlace(Store(Path.GetTempPath()), Store(Endpoint)).ShouldBeFalse();
    }
}
