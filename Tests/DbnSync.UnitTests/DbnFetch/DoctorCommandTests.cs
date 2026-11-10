using DbnFetch;
using Shouldly;
using static DbnSync.UnitTests.TestData;

namespace DbnSync.UnitTests.FetchTests;

[Collection(DbnfCollection.Name)]
public sealed class DoctorCommandTests : IDisposable
{
    private const string Work = "work";
    private const string Prefix = "archive";
    private const string Reads = "ok    Reading  works; the files are in the dbnsync layout";
    private const string NoClock = "ok    Clock    the archive sent no time to compare with";
    private const string ClockOk = "ok    Clock";
    private const string Refused = "The archive refused the key";
    private const string CouldNotReach = "The archive could not be reached";
    private const string Unknown = "FAIL  Reading";
    private const string ClosedPort = "http://127.0.0.1:1";
    private const string LocationVariable = "DBNF_LOCATION";
    private const long Plenty = 10_000_000_000;

    private static readonly TimeSpan AnHour = TimeSpan.FromHours(1);

    private readonly Dbnf dbnf = new();
    private readonly FakeS3 s3;

    public DoctorCommandTests()
    {
        s3 = new FakeS3(dbnf.ArchiveFolder);
        dbnf.Archive.AddDefinitions(Day(10, 30));
    }

    public void Dispose()
    {
        s3.Dispose();
        dbnf.Dispose();
    }

    private string CacheRoot => Path.Combine(dbnf.CacheFolder, Profiles.DefaultName);

    private bool DriveHasRoom => new DriveInfo(Path.GetPathRoot(dbnf.CacheFolder)!).AvailableFreeSpace > Plenty;

    [Fact]
    public async Task With_no_login_it_says_to_log_in()
    {
        var run = await dbnf.RunAsync("doctor");

        run.ExitCode.ShouldBe(Session.Failed);
        run.Lines.ShouldBe(["FAIL  Login    none saved as default; run dbnf login"]);
    }

    [Fact]
    public async Task A_named_profile_is_named()
    {
        var run = await dbnf.RunAsync($"doctor --profile {Work}");

        run.Lines[0].ShouldContain($"none saved as {Work}");
    }

    [Fact]
    public async Task A_folder_archive_checks_only_the_folder()
    {
        var run = await dbnf.OnArchiveAsync("doctor");

        run.ExitCode.ShouldBe(Session.Ok);
        run.Lines.ShouldBe([$"ok    Archive  {dbnf.ArchiveFolder}", Reads]);
    }

    [Fact]
    public async Task A_folder_that_is_no_archive_fails_on_reading()
    {
        var run = await dbnf.RunAsync($"doctor --archive \"{dbnf.Output}\"");

        run.ExitCode.ShouldBe(Session.Failed);
        run.Lines[1].ShouldStartWith(Unknown);
    }

    [Fact]
    public async Task A_healthy_s3_login_passes_every_check()
    {
        await dbnf.LogInAsync(s3);

        var run = await dbnf.RunAsync("doctor");

        run.ExitCode.ShouldBe(DriveHasRoom ? Session.Ok : Session.Failed);
        run.Lines[0].ShouldBe($"ok    Archive  {s3.Location}, bucket {FakeS3.Bucket}");
        run.Lines[1].ShouldBe($"ok    Key      {TestKey.Access}");
        run.Lines[2].ShouldEndWith(" the archive's");
        run.Lines[2].ShouldStartWith(ClockOk);
        run.Lines[3].ShouldBe(Reads);
    }

    [Fact]
    public async Task The_cache_line_gives_its_size_and_the_free_space()
    {
        await dbnf.LogInAsync(s3);

        var run = await dbnf.RunAsync("doctor");

        run.Lines[4].ShouldContain($"Cache    {CacheRoot}: 0 B used, ");
        run.Lines[4].ShouldEndWith("free on the drive");
    }

    [Fact]
    public async Task A_prefix_is_shown_with_the_bucket()
    {
        new SampleArchive(Path.Combine(dbnf.ArchiveFolder, Prefix)).AddDefinitions(Day(10, 30));
        await dbnf.LogInAsync(s3, $"--prefix {Prefix}");

        var run = await dbnf.RunAsync("doctor");

        run.Lines[0].ShouldEndWith($"bucket {FakeS3.Bucket}/{Prefix}");
    }

    [Fact]
    public async Task A_login_with_no_secret_fails_on_the_key()
    {
        dbnf.Profiles.Save(Profiles.DefaultName, new Profile { Location = s3.Location, AccessKey = TestKey.Access }, "");

        var run = await dbnf.RunAsync("doctor");

        run.ExitCode.ShouldBe(Session.Failed);
        run.Lines[1].ShouldBe($"FAIL  Key      {TestKey.Access}, but no secret is saved; run dbnf login");
        run.Lines.Count.ShouldBe(3);
        run.Error.ShouldBeEmpty();
    }

    [Fact]
    public void Free_space_is_read_for_a_folder_that_exists()
    {
        DbnFetch.Commands.DoctorCommand.FreeSpace(dbnf.Home).ShouldNotBeNull().ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Free_space_that_cannot_be_read_is_unknown()
    {
        if (!OperatingSystem.IsWindows())
            return;

        DbnFetch.Commands.DoctorCommand.FreeSpace(@"\\no-such-server\share\cache").ShouldBeNull();
    }

    [Fact]
    public async Task A_clock_running_fast_fails()
    {
        await dbnf.LogInAsync(s3);
        s3.ClockOffset = -AnHour;

        var run = await dbnf.RunAsync("doctor");

        run.ExitCode.ShouldBe(Session.Failed);
        run.Lines[2].ShouldStartWith("FAIL  Clock");
        run.Lines[2].ShouldContain(" s ahead of the archive");
    }

    [Fact]
    public async Task A_clock_running_slow_fails()
    {
        await dbnf.LogInAsync(s3);
        s3.ClockOffset = AnHour;

        var run = await dbnf.RunAsync("doctor");

        run.ExitCode.ShouldBe(Session.Failed);
        run.Lines[2].ShouldContain(" s behind the archive's");
        run.Lines[2].ShouldStartWith("FAIL");
    }

    [Fact]
    public async Task A_small_skew_is_within_tolerance()
    {
        await dbnf.LogInAsync(s3);
        s3.ClockOffset = TimeSpan.FromMinutes(-2);

        var run = await dbnf.RunAsync("doctor");

        run.Lines[2].ShouldStartWith(ClockOk);
    }

    [Fact]
    public async Task A_server_with_no_date_cannot_be_compared()
    {
        await dbnf.LogInAsync(s3);
        s3.ClockOffset = null;

        var run = await dbnf.RunAsync("doctor");

        run.Lines[2].ShouldBe(NoClock);
    }

    [Fact]
    public async Task A_server_that_never_answers_the_clock_check_cannot_be_compared()
    {
        await dbnf.LogInAsync(s3);
        s3.SilentAtRoot = true;

        var run = await dbnf.RunAsync("doctor");

        run.Lines[2].ShouldBe(NoClock);
        run.Lines[3].ShouldBe(Reads);
    }

    [Fact]
    public async Task A_refused_key_fails_on_reading()
    {
        await dbnf.LogInAsync(s3);
        s3.Deny = true;

        var run = await dbnf.RunAsync("doctor");

        run.ExitCode.ShouldBe(Session.Failed);
        run.Lines[3].ShouldStartWith("FAIL  Reading");
        run.Lines[3].ShouldContain(Refused);
    }

    [Fact]
    public async Task An_archive_out_of_reach_fails_on_reading_and_has_no_clock()
    {
        dbnf.Profiles.Save(Profiles.DefaultName, new Profile { Location = ClosedPort, AccessKey = TestKey.Access }, TestKey.Secret);

        var run = await dbnf.RunAsync("doctor");

        run.ExitCode.ShouldBe(Session.Failed);
        run.Lines[2].ShouldBe(NoClock);
        run.Lines[3].ShouldContain(CouldNotReach);
    }

    [Fact]
    public async Task Variables_stand_in_for_a_login()
    {
        Environment.SetEnvironmentVariable(LocationVariable, dbnf.ArchiveFolder);

        var run = await dbnf.RunAsync("doctor");

        run.ExitCode.ShouldBe(Session.Ok);
        run.Lines[0].ShouldBe($"ok    Archive  {dbnf.ArchiveFolder}");
    }
}
