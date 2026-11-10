using DbnFetch;
using DbnFetch.Commands;
using DbnClient.Archive;
using Shouldly;
using static DbnSync.UnitTests.TestData;

namespace DbnSync.UnitTests.FetchTests;

[Collection(DbnfCollection.Name)]
public sealed class CoverageCommandTests : IDisposable
{
    private const string Legend = "# stored   . no data (weekend, holiday)   - not in the archive";
    private const string Ruler = "         1        10        20         31";
    private const string Refused = "The archive refused the key";
    private const string NotLoggedIn = "Not logged in. Run dbnf login first.";
    private const string OtherRootName = "ES";
    private const string Ohlcv = SampleArchive.Ohlcv;

    private readonly Dbnf dbnf = new();

    public void Dispose() => dbnf.Dispose();

    private static StoredDay Stored(DateOnly day) =>
        new(day, new StoredObject(DbnSyncLayout.Data(Dataset, Mbo, Root, day), 1, ""));

    private static StoredDay NoData(DateOnly day) =>
        new(day, new StoredObject(DbnSyncLayout.Empty(Dataset, Mbo, Root, day), 0, ""));

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task Fewer_than_one_month_is_bad_usage(int months)
    {
        dbnf.Archive.AddDefinitions(Day(10, 30));

        var run = await dbnf.OnArchiveAsync($"coverage {Root} --months {months}");

        run.ExitCode.ShouldBe(Session.Usage);
        run.Error.ShouldContain("--months");
    }

    [Fact]
    public void Each_day_is_marked_stored_no_data_or_missing()
    {
        var lines = CoverageCommand.Draw(Root, Mbo, [Stored(Day(10, 2)), NoData(Day(10, 3)), Stored(Day(10, 5))], 12).ToList();

        lines[0].ShouldBe("MNQ mbo: 2 days stored, 1 no data, 1 missing, 2026-10-02 to 2026-10-05");
        lines[2].ShouldBe("2026-10   #.-#");
    }

    [Fact]
    public void Months_before_the_first_shown_are_left_out()
    {
        var lines = CoverageCommand.Draw(Root, Mbo, [Stored(Day(9, 30)), Stored(Day(10, 1))], months: 1).ToList();

        lines.Count.ShouldBe(3);
        lines[2].ShouldStartWith("2026-10");
    }

    [Fact]
    public void Nothing_stored_says_so()
    {
        CoverageCommand.Draw(Root, Mbo, [], 12).ShouldBe(["MNQ mbo: nothing stored."]);
    }

    private void OctoberAndNovember()
    {
        dbnf.Archive.Add(Mbo, Day(10, 30));
        dbnf.Archive.AddEmpty(Mbo, Day(10, 31));
        dbnf.Archive.Add(Mbo, Day(11, 2));
        dbnf.Archive.Add(Ohlcv, Day(11, 2));
    }

    [Fact]
    public async Task Every_schema_of_the_root_is_drawn_above_a_legend()
    {
        OctoberAndNovember();

        var run = await dbnf.OnArchiveAsync($"coverage {Root}");

        run.ExitCode.ShouldBe(Session.Ok);
        run.Lines[0].ShouldBe("MNQ mbo: 2 days stored, 1 no data, 1 missing, 2026-10-30 to 2026-11-02");
        run.Lines[1].ShouldBe(Ruler);
        run.Lines[2].ShouldEndWith("#.");
        run.Lines[3].ShouldBe("2026-11  -#");
        run.Lines[5].ShouldStartWith("MNQ ohlcv-1s: 1 days stored");
        run.Lines[^1].ShouldBe(Legend);
    }

    [Fact]
    public async Task The_root_may_be_typed_in_lower_case()
    {
        OctoberAndNovember();

        var run = await dbnf.OnArchiveAsync($"coverage {Root.ToLowerInvariant()}");

        run.Lines[0].ShouldStartWith(Root);
    }

    [Theory]
    [InlineData(Ohlcv)]
    [InlineData("OHLCV-1S")]
    public async Task Schema_draws_only_that_schema(string schema)
    {
        OctoberAndNovember();

        var run = await dbnf.OnArchiveAsync($"coverage {Root} --schema {schema}");

        run.Lines[0].ShouldStartWith($"{Root} {Ohlcv}:");
        run.Out.ShouldNotContain($"{Root} {Mbo}:");
    }

    [Fact]
    public async Task A_root_the_archive_lacks_says_so()
    {
        OctoberAndNovember();

        var run = await dbnf.OnArchiveAsync($"coverage {OtherRootName}");

        run.ExitCode.ShouldBe(Session.Ok);
        run.Lines.ShouldBe([$"The archive holds nothing for {OtherRootName}."]);
    }

    [Fact]
    public async Task A_schema_the_root_lacks_says_so()
    {
        OctoberAndNovember();

        var run = await dbnf.OnArchiveAsync($"coverage {Root} --schema {Trades}");

        run.Lines.ShouldBe([$"The archive holds nothing for {Root}."]);
    }

    [Fact]
    public async Task Twelve_months_are_drawn_by_default()
    {
        dbnf.Archive.Add(Mbo, new DateOnly(2025, 1, 15));
        dbnf.Archive.Add(Mbo, Day(11, 2));

        var run = await dbnf.OnArchiveAsync($"coverage {Root}");

        run.Lines.Count(l => l.StartsWith("202", StringComparison.Ordinal)).ShouldBe(12);
        run.Lines[2].ShouldStartWith("2025-12");
    }

    [Fact]
    public async Task Months_changes_how_many_are_drawn()
    {
        dbnf.Archive.Add(Mbo, new DateOnly(2025, 1, 15));
        dbnf.Archive.Add(Mbo, Day(11, 2));

        var run = await dbnf.OnArchiveAsync($"coverage {Root} --months 2");

        run.Lines[2].ShouldStartWith("2026-10");
        run.Lines[3].ShouldStartWith("2026-11");
    }

    [Fact]
    public async Task All_draws_every_month_from_the_first()
    {
        dbnf.Archive.Add(Mbo, new DateOnly(2025, 1, 15));
        dbnf.Archive.Add(Mbo, Day(11, 2));

        var run = await dbnf.OnArchiveAsync($"coverage {Root} --all");

        run.Lines[2].ShouldStartWith("2025-01");
        run.Lines.Count(l => l.StartsWith("202", StringComparison.Ordinal)).ShouldBe(23);
    }

    [Fact]
    public async Task A_day_stored_after_a_no_data_marker_counts_once_as_stored()
    {
        dbnf.Archive.Add(Mbo, Day(10, 30));
        dbnf.Archive.AddEmpty(Mbo, Day(10, 30));

        var run = await dbnf.OnArchiveAsync($"coverage {Root}");

        run.ExitCode.ShouldBe(Session.Ok);
        run.Lines[0].ShouldBe("MNQ mbo: 1 days stored, 0 no data, 0 missing, 2026-10-30 to 2026-10-30");
    }

    [Fact]
    public async Task Without_a_login_it_says_to_log_in()
    {
        var run = await dbnf.RunAsync($"coverage {Root}");

        run.ExitCode.ShouldBe(Session.Usage);
        run.Error.ShouldContain(NotLoggedIn);
    }

    [Fact]
    public async Task A_refused_key_fails_with_the_reason()
    {
        using var s3 = new FakeS3(dbnf.ArchiveFolder);
        dbnf.Archive.Add(Mbo, Day(10, 30));
        await dbnf.LogInAsync(s3);
        s3.Deny = true;

        var run = await dbnf.RunAsync($"coverage {Root}");

        run.ExitCode.ShouldBe(Session.Failed);
        run.Error.ShouldContain(Refused);
    }

    [Fact]
    public async Task The_root_is_required()
    {
        var run = await dbnf.OnArchiveAsync("coverage");

        run.ExitCode.ShouldBe(Dbnf.ParseFailure);
    }
}
