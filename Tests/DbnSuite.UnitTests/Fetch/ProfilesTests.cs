using DbnSuite.Fetch;
using Shouldly;

namespace DbnSuite.UnitTests.FetchTests;

[Collection(DbnfCollection.Name)]
public sealed class ProfilesTests : IDisposable
{
    private const string Location = "https://s3.example.com";
    private const string AccessKey = "dbn-reader";
    private const string Secret = "a-forty-character-secret-for-the-tests-1";
    private const string Work = "work";
    private const string Home = "home";
    private const string OtherLocation = "https://other.example.com";
    private const string OtherBucket = "market-data";
    private const string OtherKey = "someone-else";
    private const string OtherSecret = "another-forty-character-secret-for-tests";
    private const string OtherDataset = "XNAS.ITCH";
    private const string Prefix = "archive";
    private const string Region = "eu-west-1";
    private const string DefaultBucket = "dbn";
    private const string DefaultDataset = "GLBX.MDP3";
    private const string AppFolder = "DbnFetch";
    private const string ProfilesFile = "profiles.json";
    private const string SecretsFile = "secrets.json";
    private const string EmptyJson = "{}";
    private const string NullJson = "null";
    private const string LocationVariable = "DBNF_LOCATION";
    private const string BucketVariable = "DBNF_BUCKET";
    private const string AccessKeyVariable = "DBNF_ACCESS_KEY";
    private const string SecretKeyVariable = "DBNF_SECRET_KEY";

    // Keeps this machine's DBNF_ variables out, and puts them back after the tests that set their own.
    private readonly Dbnf dbnf = new();

    private readonly string folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public void Dispose()
    {
        dbnf.Dispose();

        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    private Profiles Profiles() => new(folder);

    private static Profile Profile => new() { Location = Location, AccessKey = AccessKey };

    private void WriteSecret(string stored)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, SecretsFile), $"{{\"{Work}\":\"{stored}\"}}");
    }

    [Fact]
    public void A_saved_login_comes_back_with_its_secret()
    {
        Profiles().Save(Work, Profile, Secret);

        var options = Profiles().Resolve(Work).Value!;

        options.Location.ShouldBe(Location);
        options.AccessKey.ShouldBe(AccessKey);
        options.SecretKey.ShouldBe(Secret);
    }

    [Fact]
    public void The_secret_is_never_in_the_profiles_file()
    {
        Profiles().Save(Work, Profile, Secret);

        File.ReadAllText(Path.Combine(folder, "profiles.json")).ShouldNotContain(Secret);
    }

    [Fact]
    public void On_windows_the_secret_is_encrypted_at_rest()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Profiles().Save(Work, Profile, Secret);

        File.ReadAllText(Path.Combine(folder, "secrets.json")).ShouldNotContain(Secret);
    }

    [Fact]
    public void A_secret_left_without_its_login_is_removed_too()
    {
        WriteSecret("plain:" + Secret);

        Profiles().Remove(Work).Value.ShouldBeFalse();

        Profiles().Secret(Work).Value.ShouldBeNull();
        File.ReadAllText(Path.Combine(folder, SecretsFile)).ShouldNotContain(Secret);
    }

    [Fact]
    public void Saving_leaves_no_temp_file_behind()
    {
        Profiles().Save(Work, Profile, Secret);

        Directory.GetFiles(folder).Select(Path.GetFileName).ShouldBe([ProfilesFile, SecretsFile], ignoreOrder: true);
    }

    [Fact]
    public void Off_windows_the_secrets_are_readable_by_their_owner_alone()
    {
        if (OperatingSystem.IsWindows())
            return;

        Profiles().Save(Work, Profile, Secret);

        File.GetUnixFileMode(Path.Combine(folder, SecretsFile)).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.GetUnixFileMode(folder).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    public void A_removed_login_is_gone_with_its_secret()
    {
        Profiles().Save(Work, Profile, Secret);

        Profiles().Remove(Work).Value.ShouldBeTrue();

        Profiles().Find(Work).Value.ShouldBeNull();
        Profiles().Secret(Work).Value.ShouldBeNull();
    }

    [Fact]
    public void No_login_resolves_to_nothing()
    {
        Profiles().Resolve(Work).Value.ShouldBeNull();
    }


    [Fact]
    public void A_profile_keeps_what_it_was_saved_with()
    {
        var saved = Profile with { Bucket = OtherBucket, Prefix = Prefix, Region = Region, Dataset = OtherDataset };

        Profiles().Save(Work, saved, Secret);

        Profiles().Find(Work).Value.ShouldBe(saved);
    }

    [Fact]
    public void A_profile_defaults_to_the_dbn_bucket_and_glbx()
    {
        Profile.Bucket.ShouldBe(DefaultBucket);
        Profile.Dataset.ShouldBe(DefaultDataset);
    }

    [Fact]
    public void A_profile_becomes_archive_options_with_its_secret()
    {
        var options = (Profile with { Prefix = Prefix }).ToOptions(Secret);

        options.Location.ShouldBe(Location);
        options.Prefix.ShouldBe(Prefix);
        options.SecretKey.ShouldBe(Secret);
    }

    [Fact]
    public void All_lists_every_login()
    {
        Profiles().Save(Work, Profile, Secret);
        Profiles().Save(Home, Profile, Secret);

        Profiles().All().Value.Keys.ShouldBe([Work, Home], ignoreOrder: true);
    }

    [Fact]
    public void Saving_again_replaces_the_login_and_its_secret()
    {
        Profiles().Save(Work, Profile, Secret);

        Profiles().Save(Work, Profile with { AccessKey = OtherKey }, OtherSecret);

        Profiles().Find(Work).Value!.AccessKey.ShouldBe(OtherKey);
        Profiles().Secret(Work).Value.ShouldBe(OtherSecret);
    }

    [Fact]
    public void Removing_one_login_leaves_the_others()
    {
        Profiles().Save(Work, Profile, Secret);
        Profiles().Save(Home, Profile, OtherSecret);

        Profiles().Remove(Work);

        Profiles().Secret(Home).Value.ShouldBe(OtherSecret);
    }

    [Fact]
    public void Removing_a_login_that_is_not_there_says_so()
    {
        Profiles().Remove(Work).Value.ShouldBeFalse();
    }

    [Fact]
    public void A_login_with_no_secret_resolves_with_an_empty_one()
    {
        Profiles().Save(Work, Profile, Secret);
        File.WriteAllText(Path.Combine(folder, SecretsFile), EmptyJson);

        Profiles().Resolve(Work).Value!.SecretKey.ShouldBeEmpty();
    }

    [Fact]
    public void A_secret_stored_plain_is_read_back()
    {
        WriteSecret($"plain:{Secret}");

        Profiles().Secret(Work).Value.ShouldBe(Secret);
    }

    [Theory]
    [InlineData("dpapi:AAAA")]
    [InlineData("rot13:abc")]
    public void A_secret_this_user_cannot_read_is_no_secret(string stored)
    {
        WriteSecret(stored);

        Profiles().Secret(Work).Value.ShouldBeNull();
    }

    [Fact]
    public void Files_holding_null_are_empty()
    {
        Profiles();
        File.WriteAllText(Path.Combine(folder, ProfilesFile), NullJson);
        File.WriteAllText(Path.Combine(folder, SecretsFile), NullJson);

        Profiles().All().Value.ShouldBeEmpty();
        Profiles().Secret(Work).Value.ShouldBeNull();
    }

    [Fact]
    public void The_folder_is_made_when_needed()
    {
        Profiles();

        Directory.Exists(folder).ShouldBeTrue();
    }

    [Fact]
    public void The_config_folder_is_the_users_own()
    {
        Path.GetFileName(DbnSuite.Fetch.Profiles.DefaultFolder).ShouldBe(AppFolder);
    }

    [Fact]
    public void The_cache_folder_is_the_users_own()
    {
        DbnSuite.Fetch.Profiles.DefaultCache.ShouldContain(AppFolder);
    }

    [Fact]
    public void Location_alone_needs_no_login()
    {
        Environment.SetEnvironmentVariable(LocationVariable, Location);

        var options = Profiles().Resolve(Work).Value!;

        options.Location.ShouldBe(Location);
        options.Bucket.ShouldBe(DefaultBucket);
        options.SecretKey.ShouldBeEmpty();
    }

    [Fact]
    public void Variables_take_precedence_over_a_login()
    {
        Profiles().Save(Work, Profile, Secret);
        Environment.SetEnvironmentVariable(LocationVariable, OtherLocation);
        Environment.SetEnvironmentVariable(BucketVariable, OtherBucket);
        Environment.SetEnvironmentVariable(AccessKeyVariable, OtherKey);
        Environment.SetEnvironmentVariable(SecretKeyVariable, OtherSecret);

        var options = Profiles().Resolve(Work).Value!;

        options.Location.ShouldBe(OtherLocation);
        options.Bucket.ShouldBe(OtherBucket);
        options.AccessKey.ShouldBe(OtherKey);
        options.SecretKey.ShouldBe(OtherSecret);
    }

    [Fact]
    public void One_variable_changes_only_its_own_setting()
    {
        Profiles().Save(Work, Profile, Secret);
        Environment.SetEnvironmentVariable(BucketVariable, OtherBucket);

        var options = Profiles().Resolve(Work).Value!;

        options.Location.ShouldBe(Location);
        options.Bucket.ShouldBe(OtherBucket);
        options.SecretKey.ShouldBe(Secret);
    }

    private string PathOf(string name) => Path.Combine(folder, name);

    private void Damage(string name, string text)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(PathOf(name), text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ \"profiles\": ")]
    [InlineData("[]")]
    [InlineData("{\"profiles\":null}")]
    [InlineData("{\"profiles\":{\"work\":{}}}")]
    [InlineData("{\"profiles\":{\"work\":{\"location\":null}}}")]
    public void A_damaged_profiles_file_is_an_error_naming_it(string text)
    {
        Damage(ProfilesFile, text);

        var found = Profiles().Find(Work);

        found.Error!.Code.ShouldBe(DbnSuite.Fetch.Profiles.Damaged);
        found.Error.Message.ShouldContain(PathOf(ProfilesFile));
        found.Error.Message.ShouldContain("dbnf login");
    }

    [Fact]
    public void Every_way_of_reading_a_damaged_profiles_file_says_so()
    {
        Damage(ProfilesFile, Garbled);

        Profiles().All().Error!.Code.ShouldBe(DbnSuite.Fetch.Profiles.Damaged);
        Profiles().Resolve(Work).Error!.Code.ShouldBe(DbnSuite.Fetch.Profiles.Damaged);
        Profiles().Remove(Work).Error!.Code.ShouldBe(DbnSuite.Fetch.Profiles.Damaged);
    }

    [Fact]
    public void Every_way_of_reading_a_damaged_secrets_file_says_so()
    {
        Profiles().Save(Work, Profile, Secret);
        Damage(SecretsFile, Garbled);

        Profiles().Secret(Work).Error!.Message.ShouldContain(PathOf(SecretsFile));
        Profiles().Resolve(Work).Error!.Code.ShouldBe(DbnSuite.Fetch.Profiles.Damaged);
        Profiles().Remove(Work).Error!.Code.ShouldBe(DbnSuite.Fetch.Profiles.Damaged);
    }

    [Fact]
    public void A_login_removed_from_a_damaged_secrets_file_stays_in_the_profiles()
    {
        Profiles().Save(Work, Profile, Secret);
        Damage(SecretsFile, Garbled);

        Profiles().Remove(Work);

        Damage(SecretsFile, EmptyJson);
        Profiles().Find(Work).Value.ShouldNotBeNull();
    }

    [Theory]
    [InlineData(ProfilesFile)]
    [InlineData(SecretsFile)]
    public void Saving_replaces_a_damaged_file_and_sets_it_aside(string name)
    {
        Damage(name, Garbled);

        Profiles().Save(Work, Profile, Secret);

        Profiles().Resolve(Work).Value!.SecretKey.ShouldBe(Secret);
        File.ReadAllText(PathOf(name + DamagedCopy)).ShouldBe(Garbled);
    }

    [Fact]
    public void A_secret_stored_as_null_is_no_secret()
    {
        Damage(SecretsFile, $"{{\"{Work}\":null}}");

        Profiles().Secret(Work).Value.ShouldBeNull();
    }

    private const string Garbled = "{ \"profiles\": ";
    private const string DamagedCopy = ".damaged";
}
