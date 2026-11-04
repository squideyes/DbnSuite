using DbnClient;
using DbnClient.Archive;
using DbnFetch;
using Shouldly;
using static DbnSync.UnitTests.TestData;

namespace DbnSync.UnitTests.FetchTests;

[Collection(DbnfCollection.Name)]
public sealed class SessionTests : IDisposable
{
    private const string Work = "work";
    private const string BadSchema = "tickles";
    private const string Stopped = "Stopped.";
    private const string Reason = "It went wrong.";
    private const string InputCode = "input.anything";
    private const string CliCode = "cli.anything";
    private const string ArchiveCode = "archive.anything";
    private const string Line = "a line";
    private const string OtherLine = "another line";
    private const string CacheFolderName = "DbnFetch";
    private const string NotLoggedIn = "Not logged in. Run dbnf login first.";
    private const string ProfilesFile = "profiles.json";
    private const string SecretsFile = "secrets.json";
    private const string Damaged = "{ \"profiles\": ";
    private const string LogInAgain = "dbnf login";
    private const string StackTrace = "   at ";
    private const int Done = 7;

    private readonly Dbnf dbnf = new();
    private readonly StringWriter output = new();
    private readonly StringWriter error = new();
    private readonly Session session;

    public SessionTests()
    {
        session = new Session { Profiles = dbnf.Profiles, Out = output, Error = error };
        dbnf.Archive.AddDefinitions(Day(10, 30));
    }

    public void Dispose()
    {
        output.Dispose();
        error.Dispose();
        dbnf.Dispose();
    }

    private System.CommandLine.ParseResult Parse(params string[] args) => App.Create(session).Parse(args);

    private System.CommandLine.ParseResult OnFolder() => Parse("ls", "--archive", dbnf.ArchiveFolder);

    [Fact]
    public void The_profile_is_default_unless_named()
    {
        Parse("ls").GetValue(session.Profile).ShouldBe(Profiles.DefaultName);
    }

    [Fact]
    public void The_cache_is_the_users_own_unless_named()
    {
        var cache = Parse("ls").GetValue(session.Cache)!;

        cache.ShouldBe(Profiles.DefaultCache);
        cache.ShouldContain(CacheFolderName);
    }

    [Fact]
    public void Archive_opens_that_folder_with_no_cache()
    {
        using var archive = session.Open(OnFolder()).Value;

        archive.Cache.ShouldBeNull();
        archive.Store.ShouldBeOfType<FolderObjectStore>();
    }

    [Fact]
    public void Archive_takes_precedence_over_a_login()
    {
        dbnf.Profiles.Save(Profiles.DefaultName, new Profile { Location = dbnf.Output }, "");

        session.Open(OnFolder()).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_saved_login_opens_with_a_cache_for_that_profile()
    {
        using var s3 = new FakeS3(dbnf.ArchiveFolder);
        dbnf.Profiles.Save(Work, new Profile { Location = s3.Location, AccessKey = TestKey.Access }, TestKey.Secret);

        using var archive = session.Open(Parse("ls", "--profile", Work, "--cache", dbnf.CacheFolder)).Value;

        archive.Cache!.Root.ShouldBe(Path.Combine(dbnf.CacheFolder, Work));
    }

    [Fact]
    public void With_no_login_it_says_to_log_in()
    {
        var opened = session.Open(Parse("ls"));

        opened.Error!.Message.ShouldBe(NotLoggedIn);
    }

    [Fact]
    public void With_no_login_of_that_name_it_names_the_profile()
    {
        var opened = session.Open(Parse("ls", "--profile", Work));

        opened.Error!.Message.ShouldBe($"No login named {Work}. Run dbnf login --profile {Work} first.");
    }

    [Fact]
    public async Task Run_returns_what_the_work_returns()
    {
        var exit = await session.RunAsync(OnFolder(), _ => Task.FromResult(Done), CancellationToken.None);

        exit.ShouldBe(Done);
    }

    [Fact]
    public async Task Run_never_starts_the_work_without_an_archive()
    {
        var started = false;

        var exit = await session.RunAsync(Parse("ls"), _ =>
        {
            started = true;

            return Task.FromResult(Done);
        }, CancellationToken.None);

        exit.ShouldBe(Session.Usage);
        started.ShouldBeFalse();
        error.ToString().ShouldContain(NotLoggedIn);
    }

    [Fact]
    public async Task An_archive_failure_becomes_a_message_and_an_exit_code()
    {
        var exit = await session.RunAsync(OnFolder(),
            _ => throw new ArchiveException(new Error(Errors.Denied, Reason)), CancellationToken.None);

        exit.ShouldBe(Session.Failed);
        error.ToString().ShouldContain(Reason);
    }

    [Fact]
    public async Task A_transfer_that_stops_becomes_a_message_and_an_exit_code()
    {
        var exit = await session.RunAsync(OnFolder(), _ => throw new IOException(Reason), CancellationToken.None);

        exit.ShouldBe(Session.Failed);
        error.ToString().ShouldContain(Reason);
    }

    [Fact]
    public async Task Stopping_says_so_and_exits_130()
    {
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();

        var exit = await session.RunAsync(OnFolder(), _ => throw new OperationCanceledException(stop.Token), stop.Token);

        exit.ShouldBe(Session.Cancelled);
        error.ToString().ShouldContain(Stopped);
    }

    [Fact]
    public async Task A_cancellation_nobody_asked_for_is_not_swallowed()
    {
        await Should.ThrowAsync<OperationCanceledException>(() =>
            session.RunAsync(OnFolder(), _ => throw new OperationCanceledException(), CancellationToken.None));
    }

    [Fact]
    public async Task A_command_stopped_part_way_exits_130()
    {
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();

        var run = await dbnf.OnArchiveAsync("ls", stop.Token);

        run.ExitCode.ShouldBe(Session.Cancelled);
        run.Error.ShouldContain(Stopped);
    }

    [Theory]
    [InlineData(" MBO ", "mbo")]
    [InlineData("ohlcv-1s", "ohlcv-1s")]
    [InlineData("Definition", "definition")]
    public void A_schema_is_spelled_as_dbn_spells_it(string text, string expected)
    {
        Session.Schema(text).Value.ShouldBe(expected);
    }

    [Fact]
    public void A_bad_schema_names_the_schemas_there_are()
    {
        var error = Session.Schema(BadSchema).Error!;

        error.Code.ShouldBe("input.bad_schema");
        error.Message.ShouldContain($"\"{BadSchema}\" is not a schema.");
        error.Message.ShouldContain(Mbo);
    }

    [Theory]
    [InlineData(InputCode, Session.Usage)]
    [InlineData(CliCode, Session.Usage)]
    [InlineData(ArchiveCode, Session.Failed)]
    public async Task What_the_user_got_wrong_is_a_usage_error(string code, int expected)
    {
        var exit = await session.FailAsync(new Error(code, Reason));

        exit.ShouldBe(expected);
        error.ToString().ShouldContain(Reason);
        output.ToString().ShouldBeEmpty();
    }

    [Fact]
    public async Task Lines_are_written_in_order()
    {
        await session.WriteAsync([Line, OtherLine]);

        output.ToString().ReplaceLineEndings("\n").ShouldBe($"{Line}\n{OtherLine}\n");
    }

    [Fact]
    public void Standard_input_is_the_consoles_unless_given()
    {
        new Session { Profiles = dbnf.Profiles }.In.ShouldBe(Console.In);
    }

    [Theory]
    [InlineData("ls", ProfilesFile)]
    [InlineData("doctor", ProfilesFile)]
    [InlineData("logout", ProfilesFile)]
    [InlineData("ls", SecretsFile)]
    [InlineData("doctor", SecretsFile)]
    [InlineData("logout", SecretsFile)]
    public async Task A_damaged_login_file_is_a_plain_error_that_says_to_log_in_again(string command, string name)
    {
        dbnf.Profiles.Save(Profiles.DefaultName, new Profile { Location = dbnf.ArchiveFolder }, "");
        var path = Path.Combine(dbnf.ConfigFolder, name);
        File.WriteAllText(path, Damaged);

        var run = await dbnf.RunAsync(command);

        run.ExitCode.ShouldBe(Session.Failed);
        (run.Out + run.Error).ShouldContain(path);
        (run.Out + run.Error).ShouldContain(LogInAgain);
        run.Error.ShouldNotContain(StackTrace);
    }

    [Theory]
    [InlineData("show mbo MNQ 2026-10-30")]
    [InlineData("export mbo MNQ --from 2026-10-30")]
    public async Task A_corrupt_data_file_is_a_plain_error_that_names_it(string command)
    {
        var path = dbnf.Archive.Write(DbnSyncLayout.Data(Dataset, Mbo, Root, Day(10, 30)), [1, 2, 3, 4, 5, 6, 7, 8]);

        var run = await dbnf.OnArchiveAsync(command);

        run.ExitCode.ShouldBe(Session.Failed);
        run.Error.ShouldContain(path);
        run.Error.ShouldNotContain(StackTrace);
    }
}
