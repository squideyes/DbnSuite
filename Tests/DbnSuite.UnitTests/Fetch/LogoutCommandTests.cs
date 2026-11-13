using DbnSuite.Fetch;
using Shouldly;
using static DbnSuite.UnitTests.TestData;

namespace DbnSuite.UnitTests.FetchTests;

[Collection(DbnfCollection.Name)]
public sealed class LogoutCommandTests : IDisposable
{
    private const string Work = "work";
    private const string ForgotDefault = "Forgot the default login.";
    private const string NoDefault = "There was no default login.";

    private readonly Dbnf dbnf = new();

    public LogoutCommandTests() => dbnf.Archive.AddDefinitions(Day(10, 30));

    public void Dispose() => dbnf.Dispose();

    private Task<DbnfRun> LogInAsync(string options = "") =>
        dbnf.RunAsync($"login --location \"{dbnf.ArchiveFolder}\" {options}");

    [Fact]
    public async Task A_login_is_forgotten()
    {
        await LogInAsync();

        var run = await dbnf.RunAsync("logout");

        run.ExitCode.ShouldBe(Session.Ok);
        run.Lines.ShouldBe([ForgotDefault]);
        dbnf.Profiles.All().Value.ShouldBeEmpty();
    }

    [Fact]
    public async Task After_logout_commands_ask_for_a_login()
    {
        await LogInAsync();
        await dbnf.RunAsync("logout");

        var run = await dbnf.RunAsync("ls");

        run.ExitCode.ShouldBe(Session.Usage);
    }

    [Fact]
    public async Task The_secret_goes_with_the_login()
    {
        using var s3 = new FakeS3(dbnf.ArchiveFolder);
        await dbnf.LogInAsync(s3);

        await dbnf.RunAsync("logout");

        dbnf.Profiles.Secret(Profiles.DefaultName).Value.ShouldBeNull();
    }

    [Fact]
    public async Task Logging_out_with_no_login_says_so_and_succeeds()
    {
        var run = await dbnf.RunAsync("logout");

        run.ExitCode.ShouldBe(Session.Ok);
        run.Lines.ShouldBe([NoDefault]);
    }

    [Fact]
    public async Task A_profile_is_forgotten_alone()
    {
        await LogInAsync();
        await LogInAsync($"--profile {Work}");

        var run = await dbnf.RunAsync($"logout --profile {Work}");

        run.Lines.ShouldBe([$"Forgot the {Work} login."]);
        dbnf.Profiles.Find(Work).Value.ShouldBeNull();
        dbnf.Profiles.Find(Profiles.DefaultName).Value.ShouldNotBeNull();
    }
}
