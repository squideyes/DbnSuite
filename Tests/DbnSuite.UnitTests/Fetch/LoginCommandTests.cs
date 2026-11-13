using DbnSuite.Fetch;
using Shouldly;
using static DbnSuite.UnitTests.TestData;

namespace DbnSuite.UnitTests.FetchTests;

[Collection(DbnfCollection.Name)]
public sealed class LoginCommandTests : IDisposable
{
    private const string Work = "work";
    private const string Bucket = "market-data";
    private const string Prefix = "archive";
    private const string Prompt = "Secret key: ";
    private const string NeedsAccessKey = "An S3 archive needs --access-key.";
    private const string NoSecret = "No secret key given.";
    private const string NoSuchBucket = "The archive has no such bucket";
    private const string Refused = "The archive refused the key";
    private const string CouldNotReach = "The archive could not be reached";
    private const string NotALayout = "so it is not in a layout DbnClient knows";
    private const string ClosedPort = "http://127.0.0.1:1";
    private const string Typed = "ab";

    private readonly Dbnf dbnf = new();
    private readonly FakeS3 s3;

    public LoginCommandTests()
    {
        s3 = new FakeS3(dbnf.ArchiveFolder);
        dbnf.Archive.AddDefinitions(Day(10, 30));
    }

    public void Dispose()
    {
        s3.Dispose();
        dbnf.Dispose();
    }

    private string S3Login => $"login --location {s3.Location} --access-key {TestKey.Access}";

    private string FolderLogin => $"login --location \"{dbnf.ArchiveFolder}\"";

    /// <summary>A keyboard that presses these keys, then Enter.</summary>
    private static Func<ConsoleKeyInfo> Keys(params ConsoleKeyInfo[] keys)
    {
        var pressed = new Queue<ConsoleKeyInfo>([.. keys, Key('\r', ConsoleKey.Enter)]);

        return pressed.Dequeue;
    }

    private static ConsoleKeyInfo Key(char character, ConsoleKey key) => new(character, key, false, false, false);

    private static ConsoleKeyInfo Letter(char character) => Key(character, ConsoleKey.A);

    [Fact]
    public async Task A_folder_needs_no_key()
    {
        var run = await dbnf.RunAsync(FolderLogin);

        run.ExitCode.ShouldBe(Session.Ok);
        run.Lines.ShouldBe([$"Logged in to {dbnf.ArchiveFolder}."]);
    }

    [Fact]
    public async Task Later_commands_use_the_saved_folder()
    {
        await dbnf.RunAsync(FolderLogin);

        var run = await dbnf.RunAsync("ls");

        run.ExitCode.ShouldBe(Session.Ok);
        run.Out.ShouldContain(Root);
    }

    [Fact]
    public async Task A_profile_saves_under_its_own_name()
    {
        var run = await dbnf.RunAsync($"{FolderLogin} --profile {Work}");

        run.Lines.ShouldBe([$"Logged in to {dbnf.ArchiveFolder}, saved as {Work}."]);
        dbnf.Profiles.Find(Work).Value.ShouldNotBeNull();
        dbnf.Profiles.Find(Profiles.DefaultName).Value.ShouldBeNull();
    }

    [Fact]
    public async Task Another_profile_is_not_the_default_login()
    {
        await dbnf.RunAsync($"{FolderLogin} --profile {Work}");

        var run = await dbnf.RunAsync("ls");

        run.ExitCode.ShouldBe(Session.Usage);
    }

    [Fact]
    public async Task A_folder_that_is_no_archive_is_not_saved()
    {
        Directory.CreateDirectory(dbnf.Output);

        var run = await dbnf.RunAsync($"login --location \"{dbnf.Output}\"");

        run.ExitCode.ShouldBe(Session.Failed);
        run.Error.ShouldContain(NotALayout);
        dbnf.Profiles.All().Value.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("s3.example.com")]
    [InlineData("not-a-folder")]
    public async Task A_location_that_is_neither_an_address_nor_a_folder_is_bad_usage(string location)
    {
        var run = await dbnf.RunAsync($"login --location {location} --access-key {TestKey.Access}");

        run.ExitCode.ShouldBe(Session.Usage);
        run.Error.ShouldContain("https://");
        dbnf.Profiles.All().Value.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_location_is_required()
    {
        var run = await dbnf.RunAsync("login");

        run.ExitCode.ShouldBe(Dbnf.ParseFailure);
        run.Error.ShouldContain("--location");
    }

    [Fact]
    public async Task An_s3_login_needs_an_access_key()
    {
        var run = await dbnf.RunAsync($"login --location {s3.Location}");

        run.ExitCode.ShouldBe(Session.Usage);
        run.Error.ShouldContain(NeedsAccessKey);
    }

    [Fact]
    public async Task A_piped_secret_is_read_and_saved()
    {
        dbnf.Stdin = new StringReader($"  {TestKey.Secret}  \n");

        var run = await dbnf.RunAsync($"{S3Login} --secret-stdin");

        run.ExitCode.ShouldBe(Session.Ok);
        run.Lines.ShouldBe([$"Logged in to {s3.Location} as {TestKey.Access}."]);
        dbnf.Profiles.Secret(Profiles.DefaultName).Value.ShouldBe(TestKey.Secret);
    }

    [Fact]
    public async Task The_secret_is_never_printed()
    {
        dbnf.Stdin = new StringReader(TestKey.Secret);

        var run = await dbnf.RunAsync($"{S3Login} --secret-stdin");

        run.Out.ShouldNotContain(TestKey.Secret);
    }

    [Fact]
    public async Task An_s3_profile_says_so_when_saved()
    {
        dbnf.Stdin = new StringReader(TestKey.Secret);

        var run = await dbnf.RunAsync($"{S3Login} --secret-stdin --profile {Work}");

        run.Lines.ShouldBe([$"Logged in to {s3.Location} as {TestKey.Access}, saved as {Work}."]);
    }

    [Fact]
    public async Task The_bucket_defaults_and_the_prefix_is_saved()
    {
        var folder = new SampleArchive(Path.Combine(dbnf.ArchiveFolder, Prefix));
        folder.AddDefinitions(Day(10, 30));
        dbnf.Stdin = new StringReader(TestKey.Secret);

        var run = await dbnf.RunAsync($"{S3Login} --secret-stdin --prefix {Prefix}");

        run.ExitCode.ShouldBe(Session.Ok);
        var saved = dbnf.Profiles.Find(Profiles.DefaultName).Value!;
        saved.Bucket.ShouldBe(FakeS3.Bucket);
        saved.Prefix.ShouldBe(Prefix);
    }

    [Fact]
    public async Task A_bucket_that_is_not_there_is_not_saved()
    {
        dbnf.Stdin = new StringReader(TestKey.Secret);

        var run = await dbnf.RunAsync($"{S3Login} --secret-stdin --bucket {Bucket}");

        run.ExitCode.ShouldBe(Session.Failed);
        run.Error.ShouldContain(NoSuchBucket);
        dbnf.Profiles.All().Value.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_empty_pipe_is_no_secret()
    {
        dbnf.Stdin = new StringReader("   \n");

        var run = await dbnf.RunAsync($"{S3Login} --secret-stdin");

        run.ExitCode.ShouldBe(Session.Usage);
        run.Error.ShouldContain(NoSecret);
    }

    [Fact]
    public async Task With_no_terminal_nobody_can_be_asked()
    {
        var run = await dbnf.RunAsync(S3Login);

        run.ExitCode.ShouldBe(Session.Usage);
        run.Error.ShouldContain(NoSecret);
    }

    [Fact]
    public async Task At_a_terminal_it_asks_for_the_secret_without_echoing_it()
    {
        dbnf.Keyboard = Keys(Letter('a'), Letter('b'));

        var run = await dbnf.RunAsync(S3Login);

        run.ExitCode.ShouldBe(Session.Ok);
        run.Out.ShouldStartWith(Prompt);
        run.Out.ShouldNotContain(Typed);
        dbnf.Profiles.Secret(Profiles.DefaultName).Value.ShouldBe(Typed);
    }

    [Fact]
    public async Task Backspace_takes_back_a_key()
    {
        dbnf.Keyboard = Keys(Letter('a'), Letter('x'), Key('\b', ConsoleKey.Backspace), Letter('b'));

        await dbnf.RunAsync(S3Login);

        dbnf.Profiles.Secret(Profiles.DefaultName).Value.ShouldBe(Typed);
    }

    [Fact]
    public async Task Backspace_on_nothing_does_nothing()
    {
        dbnf.Keyboard = Keys(Key('\b', ConsoleKey.Backspace), Letter('a'), Letter('b'));

        await dbnf.RunAsync(S3Login);

        dbnf.Profiles.Secret(Profiles.DefaultName).Value.ShouldBe(Typed);
    }

    [Fact]
    public async Task Control_keys_are_ignored()
    {
        dbnf.Keyboard = Keys(Letter('a'), Key('\t', ConsoleKey.Tab), Letter('b'));

        await dbnf.RunAsync(S3Login);

        dbnf.Profiles.Secret(Profiles.DefaultName).Value.ShouldBe(Typed);
    }

    [Fact]
    public async Task Pressing_enter_alone_is_no_secret()
    {
        dbnf.Keyboard = Keys();

        var run = await dbnf.RunAsync(S3Login);

        run.ExitCode.ShouldBe(Session.Usage);
        run.Error.ShouldContain(NoSecret);
    }

    [Fact]
    public async Task A_refused_key_is_not_saved()
    {
        s3.Deny = true;
        dbnf.Stdin = new StringReader(TestKey.Secret);

        var run = await dbnf.RunAsync($"{S3Login} --secret-stdin");

        run.ExitCode.ShouldBe(Session.Failed);
        run.Error.ShouldContain(Refused);
        dbnf.Profiles.All().Value.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_archive_out_of_reach_is_not_saved()
    {
        dbnf.Stdin = new StringReader(TestKey.Secret);

        var run = await dbnf.RunAsync($"login --location {ClosedPort} --access-key {TestKey.Access} --secret-stdin");

        run.ExitCode.ShouldBe(Session.Failed);
        run.Error.ShouldContain(CouldNotReach);
        dbnf.Profiles.All().Value.ShouldBeEmpty();
    }

    [Fact]
    public async Task Logging_in_again_mends_a_damaged_profiles_file()
    {
        File.WriteAllText(Path.Combine(dbnf.ConfigFolder, DamagedName), DamagedText);

        var run = await dbnf.RunAsync($"login --location \"{dbnf.ArchiveFolder}\"");

        run.ExitCode.ShouldBe(Session.Ok);
        dbnf.Profiles.All().Value.ShouldHaveSingleItem();
    }

    private const string DamagedName = "profiles.json";
    private const string DamagedText = "{ \"profiles\": ";
}
