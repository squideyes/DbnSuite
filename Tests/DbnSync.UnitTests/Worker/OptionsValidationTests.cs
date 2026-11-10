using DbnSync.Core;
using DbnSync.Data;
using DbnSync.Worker;
using NSubstitute;
using Shouldly;
using static DbnSync.UnitTests.TestData;

namespace DbnSync.UnitTests.Worker;

public sealed class OptionsValidationTests
{
    private static SyncOptions Options(SpendPolicy spend) =>
        new() { Roots = Root, Schemas = Mbo, Spend = spend };

    private static bool Valid(SpendPolicy spend) =>
        new SyncOptionsValidator().Validate(null, Options(spend)).Succeeded;

    [Fact]
    public void The_default_spend_policy_is_valid()
    {
        Valid(new SpendPolicy()).ShouldBeTrue();
    }

    [Fact]
    public void Paid_jobs_with_a_limit_missing_are_refused()
    {
        Valid(new SpendPolicy { AllowPaidJobs = true, MaxPerJob = 5m, MaxPerDay = 20m }).ShouldBeFalse();
    }

    [Fact]
    public void Limits_that_do_not_nest_are_refused()
    {
        Valid(new SpendPolicy { AllowPaidJobs = true, MaxPerJob = 50m, MaxPerDay = 20m, MaxPerMonth = 100m })
            .ShouldBeFalse();
    }

    [Fact]
    public void Negative_limits_are_refused_even_with_paid_jobs_off()
    {
        Valid(new SpendPolicy { MaxPerDay = -1m }).ShouldBeFalse();
    }

    [Fact]
    public void A_complete_nested_policy_is_valid()
    {
        Valid(new SpendPolicy { AllowPaidJobs = true, MaxPerJob = 5m, MaxPerDay = 20m, MaxPerMonth = 100m })
            .ShouldBeTrue();
    }

    [Fact]
    public void A_dataset_without_a_profile_is_refused_by_name()
    {
        var result = new SyncOptionsValidator().Validate(null,
            new SyncOptions { Dataset = OtherDataset, Roots = Root, Schemas = Mbo });

        result.Failed.ShouldBeTrue();
        result.FailureMessage!.ShouldContain(OtherDataset);
        result.FailureMessage!.ShouldContain(Dataset);
    }

    [Fact]
    public void A_contributed_profile_is_refused_unless_allowed()
    {
        var options = new SyncOptions { Dataset = OtherDataset, Roots = Root, Schemas = Mbo };

        var result = SyncOptionsValidator.Validate(options, Contributed());

        result.Failed.ShouldBeTrue();
        result.FailureMessage!.ShouldContain("AllowContributedProfile");
    }

    [Fact]
    public void A_contributed_profile_is_accepted_when_allowed()
    {
        var options = new SyncOptions { Dataset = OtherDataset, Roots = Root, Schemas = Mbo, AllowContributedProfile = true };

        SyncOptionsValidator.Validate(options, Contributed()).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void A_group_the_profile_refuses_is_named_by_the_profile_s_word_for_it()
    {
        var options = new SyncOptions { Roots = "ES.FUT", Schemas = Mbo };

        var result = new SyncOptionsValidator().Validate(null, options);

        result.FailureMessage!.ShouldContain($"'ES.FUT' is not a {Dataset} root.");
    }

    [Fact]
    public void A_schema_the_profile_does_not_keep_is_refused()
    {
        var options = new SyncOptions { Roots = Root, Schemas = "imbalance" };

        new SyncOptionsValidator().Validate(null, options).FailureMessage!.ShouldContain("'imbalance'");
    }

    [Fact]
    public void History_before_the_dataset_begins_is_refused()
    {
        var options = new SyncOptions { Roots = Root, Schemas = Mbo, HistoryStart = new DateOnly(2009, 1, 1) };

        new SyncOptionsValidator().Validate(null, options).FailureMessage!.ShouldContain("before GLBX.MDP3 begins");
    }

    [Fact]
    public void No_roots_are_refused_in_the_profile_s_own_word()
    {
        var failure = Failure(new SyncOptions { Schemas = Mbo });

        failure.ShouldContain("DbnSync:Roots names no roots.");
    }

    [Fact]
    public void No_schemas_are_refused()
    {
        var failure = Failure(new SyncOptions { Roots = Root });

        failure.ShouldContain("DbnSync:Schemas names no schemas.");
    }

    [Fact]
    public void A_root_listed_only_for_one_schema_is_checked_too()
    {
        var options = Keeping();
        options.RootsBySchema[Mbo] = "ES.FUT";

        Failure(options).ShouldContain("'ES.FUT' is not a");
    }

    [Fact]
    public void A_schema_listed_only_in_the_per_schema_roots_is_checked_too()
    {
        var options = Keeping();
        options.RootsBySchema["imbalance"] = Root;

        Failure(options).ShouldContain("'imbalance'");
    }

    [Theory]
    [InlineData(4, 5)]
    [InlineData(300, 0)]
    public void Passes_too_close_together_are_refused(int intervalMinutes, int busyMinutes)
    {
        var options = Keeping();
        options.Interval = TimeSpan.FromMinutes(intervalMinutes);
        options.BusyInterval = TimeSpan.FromMinutes(busyMinutes);

        Failure(options).ShouldContain("at least 5 minutes");
    }

    [Theory]
    [InlineData(31, 0)]
    [InlineData(0, 31)]
    public void A_wait_longer_than_a_month_is_refused(int intervalDays, int busyDays)
    {
        var options = Keeping();
        options.Interval = intervalDays > 0 ? TimeSpan.FromDays(intervalDays) : options.Interval;
        options.BusyInterval = busyDays > 0 ? TimeSpan.FromDays(busyDays) : options.BusyInterval;

        Failure(options).ShouldContain("at most 30 days");
    }

    [Fact]
    public void An_empty_scratch_folder_is_refused()
    {
        var options = Keeping();
        options.ScratchPath = " ";

        Failure(options).ShouldContain("ScratchPath");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public void A_pending_limit_out_of_range_is_refused(int jobs)
    {
        var options = Keeping();
        options.MaxPendingJobs = jobs;

        Failure(options).ShouldContain("MaxPendingJobs must be 1-500");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public void A_download_concurrency_out_of_range_is_refused(int downloads)
    {
        var options = Keeping();
        options.DownloadConcurrency = downloads;

        Failure(options).ShouldContain("DownloadConcurrency must be 1-16");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void A_depth_out_of_range_is_refused(int depth)
    {
        var options = Keeping();
        options.Depth = depth;

        Failure(options).ShouldContain("Depth must be 1-4");
    }

    [Fact]
    public void Reaching_back_before_the_dataset_begins_is_refused()
    {
        var options = Keeping();
        options.ReachBackTo = GlbxMdp3.Instance.Start.AddDays(-1);

        Failure(options).ShouldContain("DbnSync:ReachBackTo is before");
    }

    [Fact]
    public void Reaching_back_to_the_first_day_is_allowed()
    {
        var options = Keeping();
        options.ReachBackTo = GlbxMdp3.Instance.Start;

        new SyncOptionsValidator().Validate(null, options).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Every_problem_is_reported_at_once()
    {
        var options = Keeping();
        options.Depth = 0;
        options.DownloadConcurrency = 0;

        var failure = Failure(options);

        failure.ShouldContain("Depth");
        failure.ShouldContain("DownloadConcurrency");
    }

    [Theory]
    [InlineData(StoreUrl)]
    [InlineData("https://fra1.digitaloceanspaces.com")]
    public void An_http_store_is_accepted(string url)
    {
        new StoreOptionsValidator().Validate(null, Store(url)).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("minio:9000")]
    [InlineData("ftp://nas.local")]
    public void A_store_address_that_is_not_http_is_refused(string url)
    {
        new StoreOptionsValidator().Validate(null, Store(url)).FailureMessage!.ShouldContain("Store:ServiceUrl");
    }

    [Fact]
    public void A_store_with_no_bucket_is_refused()
    {
        var options = Store(StoreUrl);
        options.Bucket = " ";

        new StoreOptionsValidator().Validate(null, options).FailureMessage!.ShouldContain("Store:Bucket");
    }

    [Theory]
    [InlineData("", StoreSecret)]
    [InlineData(StoreKey, "")]
    public void A_store_missing_either_key_is_refused(string accessKey, string secretKey)
    {
        var options = Store(StoreUrl);
        options.AccessKey = accessKey;
        options.SecretKey = secretKey;

        new StoreOptionsValidator().Validate(null, options).FailureMessage!.ShouldContain("Store:AccessKey");
    }

    private const string StoreUrl = "http://nas.local:9000";
    private const string StoreKey = "key";
    private const string StoreSecret = "secret";

    private static SyncOptions Keeping() => new() { Roots = Root, Schemas = Mbo };

    private static string Failure(SyncOptions options) => new SyncOptionsValidator().Validate(null, options).FailureMessage!;

    private static StoreOptions Store(string url) =>
        new() { ServiceUrl = url, Bucket = "dbn", AccessKey = StoreKey, SecretKey = StoreSecret };

    private const string OtherDataset = "XNAS.ITCH";

    private static IDatasetProfile Contributed()
    {
        var profile = Substitute.For<IDatasetProfile>();

        profile.Dataset.Returns(OtherDataset);
        profile.Status.Returns(ProfileStatus.Contributed);
        profile.Maintainer.Returns("someone");
        profile.GroupNoun.Returns("symbol");
        profile.Start.Returns(new DateOnly(2018, 5, 1));
        profile.Schemas.Returns(GlbxMdp3.Instance.Schemas);
        profile.IsGroup(Arg.Any<string>()).Returns(true);

        return profile;
    }
}
