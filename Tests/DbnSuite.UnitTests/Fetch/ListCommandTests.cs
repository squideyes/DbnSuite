using DbnSuite.Fetch;
using Shouldly;
using static DbnSuite.UnitTests.TestData;

namespace DbnSuite.UnitTests.FetchTests;

[Collection(DbnfCollection.Name)]
public sealed class ListCommandTests : IDisposable
{
    private const string Header = "Root  Schema    Days  No data";
    private const string NothingMatches = "Nothing matches in the archive.";
    private const string NotLoggedIn = "Not logged in. Run dbnf login first.";
    private const string Refused = "The archive refused the key";

    private readonly Dbnf dbnf = new();

    public void Dispose() => dbnf.Dispose();

    private void TwoRoots()
    {
        dbnf.Archive.Add(Mbo, Day(10, 30));
        dbnf.Archive.AddEmpty(Mbo, Day(10, 31));
        dbnf.Archive.Add(SampleArchive.Ohlcv, OtherRoot, Day(11, 2));
    }

    [Fact]
    public async Task An_empty_archive_says_nothing_matches()
    {
        var run = await dbnf.OnArchiveAsync("ls");

        run.ExitCode.ShouldBe(Session.Ok);
        run.Lines.ShouldBe([NothingMatches]);
    }

    [Fact]
    public async Task Every_series_is_listed_by_root_with_a_total()
    {
        TwoRoots();

        var run = await dbnf.OnArchiveAsync("ls");

        run.ExitCode.ShouldBe(Session.Ok);
        run.Lines[0].ShouldStartWith(Header);
        run.Lines[1].ShouldStartWith(OtherRoot);
        run.Lines[2].ShouldStartWith(Root);
        run.Lines[^1].ShouldStartWith("2 series, ");
    }

    [Fact]
    public async Task A_row_gives_days_no_data_days_and_the_stored_span()
    {
        TwoRoots();

        var run = await dbnf.OnArchiveAsync($"ls --root {Root}");

        var row = run.Lines[1];

        row.ShouldContain(Mbo);
        row.ShouldContain(" 1 ");
        row.ShouldEndWith("2026-10-30  2026-10-30");
    }

    [Theory]
    [InlineData("--root mcl")]
    [InlineData("--schema OHLCV-1S")]
    public async Task Filters_ignore_case(string filter)
    {
        TwoRoots();

        var run = await dbnf.OnArchiveAsync($"ls {filter}");

        run.Lines.Count.ShouldBe(4);
        run.Lines[1].ShouldStartWith(OtherRoot);
    }

    [Fact]
    public async Task Filters_combine()
    {
        TwoRoots();

        var run = await dbnf.OnArchiveAsync($"ls --root {Root} --schema {SampleArchive.Ohlcv}");

        run.Lines.ShouldBe([NothingMatches]);
    }

    [Fact]
    public async Task A_series_of_no_data_days_has_no_span()
    {
        dbnf.Archive.AddEmpty(Mbo, Day(10, 31));

        var run = await dbnf.OnArchiveAsync("ls");

        run.Lines[1].ShouldEndWith("-      -");
    }

    [Fact]
    public async Task A_day_with_data_and_an_old_no_data_marker_counts_as_stored()
    {
        dbnf.Archive.Add(Mbo, Day(10, 30));
        dbnf.Archive.AddEmpty(Mbo, Day(10, 30));

        var run = await dbnf.OnArchiveAsync("ls");

        run.Lines[1].ShouldContain($"{Mbo}        1        0");
    }

    [Fact]
    public async Task Without_a_login_it_says_to_log_in()
    {
        var run = await dbnf.RunAsync("ls");

        run.ExitCode.ShouldBe(Session.Usage);
        run.Error.ShouldContain(NotLoggedIn);
    }

    [Fact]
    public async Task A_refused_key_fails_with_the_reason()
    {
        using var s3 = new FakeS3(dbnf.ArchiveFolder);
        dbnf.Archive.AddDefinitions(Day(10, 30));
        await dbnf.LogInAsync(s3);
        s3.Deny = true;

        var run = await dbnf.RunAsync("ls");

        run.ExitCode.ShouldBe(Session.Failed);
        run.Error.ShouldContain(Refused);
    }
}
