using DbnFetch;
using Shouldly;
using static DbnSync.UnitTests.FetchTests.SampleArchive;
using static DbnSync.UnitTests.TestData;

namespace DbnSync.UnitTests.FetchTests;

[Collection(DbnfCollection.Name)]
public sealed class ShowCommandTests : IDisposable
{
    private const string Date = "2026-10-30";
    private const string NoDataDate = "2026-10-31";
    private const string NeverFetchedDate = "2026-11-02";
    private const string FrontSeries = "MNQ.n.0";
    private const string NextSeries = "MNQ.n.1";
    private const string VolumeSeries = "MNQ.v.0";
    private const string UnlistedContract = "MNQM26";
    private const string BadSchema = "tickles";
    private const string BadZone = "Mars/Olympus";
    private const string BadSymbol = "NOT!A!SYMBOL";
    private const string BadDate = "someday";
    private const string Heading = "GLBX.MDP3 mbo, DBN v3, 2026-10-30 UTC, requested as MNQ.FUT, times in America/Chicago";
    private const string ChicagoOffset = "-05:00";
    private const string UtcMark = "Z  ";
    private const uint UnnamedId = 77;

    private static readonly DateOnly Shown = Day(10, 30);
    private static readonly ulong Morning = At(Shown, 14);

    private readonly Dbnf dbnf = new();

    public void Dispose() => dbnf.Dispose();

    private void OrdersOfBothContracts(bool definitions = true)
    {
        dbnf.Archive.Add(Mbo, Root, Shown, new Dictionary<string, uint> { [FrontSeries] = FrontId, [NextSeries] = NextId },
            Order(FrontId, Morning, 7820m, 1, 'A', 'B', 1),
            Order(FrontId, Morning, 7820.25m, 2, 'A', 'A', 2),
            Order(FrontId, Morning, 7820.25m, 2, 'T', 'A', 2),
            Order(NextId, Morning, 7900m, 1, 'A', 'B', 3));

        if (definitions)
            dbnf.Archive.AddDefinitions(Shown);
    }

    [Fact]
    public async Task A_root_shows_every_record_under_a_heading_and_over_a_count()
    {
        OrdersOfBothContracts();

        var run = await dbnf.OnArchiveAsync($"show {Mbo} {Root} {Date}");

        run.ExitCode.ShouldBe(Session.Ok);
        run.Lines[0].ShouldBe(Heading);
        run.Lines.Count(l => l.Contains(" add ", StringComparison.Ordinal)).ShouldBe(3);
        run.Lines[^1].ShouldBe("Showed 4 of 4 records for MNQ (4 in the file).");
    }

    [Theory]
    [InlineData(Front)]
    [InlineData(FrontRaw)]
    public async Task A_contract_keeps_its_own_records_under_either_name(string contract)
    {
        OrdersOfBothContracts();

        var run = await dbnf.OnArchiveAsync($"show {Mbo} {contract} {Date}");

        run.Lines[^1].ShouldBe($"Showed 3 of 3 records for {contract} (4 in the file).");
        run.Out.ShouldNotContain(Next);
    }

    [Fact]
    public async Task Records_are_named_by_the_days_definitions()
    {
        OrdersOfBothContracts();

        var run = await dbnf.OnArchiveAsync($"show {Mbo} {Root} {Date}");

        run.Lines[2].ShouldContain(Front);
    }

    [Fact]
    public async Task A_continuous_name_keeps_the_records_the_file_was_requested_as()
    {
        OrdersOfBothContracts();

        var run = await dbnf.OnArchiveAsync($"show {Mbo} {NextSeries} {Date}");

        run.Lines[^1].ShouldBe($"Showed 1 of 1 records for {NextSeries} (4 in the file).");
    }

    [Fact]
    public async Task A_continuous_name_the_file_was_not_requested_as_is_refused()
    {
        OrdersOfBothContracts();

        var run = await dbnf.OnArchiveAsync($"show {Mbo} {VolumeSeries} {Date}");

        run.ExitCode.ShouldBe(Session.Usage);
        run.Error.ShouldContain($"This file was not requested as {VolumeSeries}");
    }

    [Fact]
    public async Task A_contract_the_day_does_not_define_is_refused()
    {
        OrdersOfBothContracts();

        var run = await dbnf.OnArchiveAsync($"show {Mbo} {UnlistedContract} {Date}");

        run.ExitCode.ShouldBe(Session.Usage);
        run.Error.ShouldContain($"The day's definitions have no contract {UnlistedContract}.");
    }

    [Fact]
    public async Task Without_definitions_records_take_the_files_own_names()
    {
        OrdersOfBothContracts(definitions: false);

        var run = await dbnf.OnArchiveAsync($"show {Mbo} {Root} {Date}");

        run.ExitCode.ShouldBe(Session.Ok);
        run.Lines[2].ShouldContain(FrontSeries);
    }

    [Fact]
    public async Task An_instrument_nothing_names_shows_its_id()
    {
        dbnf.Archive.Add(Mbo, Shown, Order(UnnamedId, Morning, 1m, 1, 'A', 'B', 1));

        var run = await dbnf.OnArchiveAsync($"show {Mbo} {Root} {Date}");

        run.Lines[2].ShouldContain($"  {UnnamedId} ");
    }

    [Fact]
    public async Task Twenty_records_are_shown_by_default()
    {
        var orders = Enumerable.Range(1, 25).Select(i => Order(FrontId, Morning, 1m, 1, 'A', 'B', (ulong)i)).ToArray();
        dbnf.Archive.Add(Mbo, Shown, orders);

        var run = await dbnf.OnArchiveAsync($"show {Mbo} {Root} {Date}");

        run.Lines[^1].ShouldBe("Showed 20 of 25 records for MNQ (25 in the file).");
    }

    [Fact]
    public async Task Limit_changes_how_many_are_shown_but_not_how_many_are_counted()
    {
        OrdersOfBothContracts();

        var run = await dbnf.OnArchiveAsync($"show {Mbo} {Root} {Date} --limit 1");

        run.Lines[^1].ShouldBe("Showed 1 of 4 records for MNQ (4 in the file).");
    }

    [Fact]
    public async Task Times_are_in_chicago_by_default()
    {
        OrdersOfBothContracts();

        var run = await dbnf.OnArchiveAsync($"show {Mbo} {Root} {Date}");

        run.Lines[2].ShouldContain(ChicagoOffset);
    }

    [Fact]
    public async Task Tz_shows_times_in_another_zone()
    {
        OrdersOfBothContracts();

        var run = await dbnf.OnArchiveAsync($"show {Mbo} {Root} {Date} --tz UTC");

        run.Lines[2].ShouldContain(UtcMark);
        run.Lines[0].ShouldEndWith("times in UTC");
    }

    [Fact]
    public async Task The_schema_may_be_typed_in_any_case()
    {
        OrdersOfBothContracts();

        var run = await dbnf.OnArchiveAsync($"show {Mbo.ToUpperInvariant()} {Root} {Date}");

        run.ExitCode.ShouldBe(Session.Ok);
    }

    [Fact]
    public async Task Other_schemas_print_their_own_kind_of_line()
    {
        dbnf.Archive.Add(Statistics, Shown, Stat(FrontId, Morning, Settlement, Price(7820.5m)));
        dbnf.Archive.Add(Status, Shown, StatusChange(FrontId, Morning, 7, 'Y'));
        dbnf.Archive.AddDefinitions(Shown);

        var statistics = await dbnf.OnArchiveAsync($"show {Statistics} {Root} {Date}");
        var status = await dbnf.OnArchiveAsync($"show {Status} {Root} {Date}");
        var definitions = await dbnf.OnArchiveAsync($"show {Definition} {Root} {Date}");

        statistics.Lines[2].ShouldEndWith("settlement price         7820.5");
        status.Lines[2].ShouldEndWith("trading Y");
        definitions.Lines[2].ShouldEndWith("tick 0.25  expires 2026-12-18");
    }

    [Fact]
    public async Task A_day_with_no_data_says_so()
    {
        dbnf.Archive.AddEmpty(Mbo, Day(10, 31));

        var run = await dbnf.OnArchiveAsync($"show {Mbo} {Root} {NoDataDate}");

        run.ExitCode.ShouldBe(Session.Failed);
        run.Error.ShouldContain($"{Root} {Mbo} had no data on {NoDataDate}.");
    }

    [Fact]
    public async Task A_day_never_fetched_says_so()
    {
        var run = await dbnf.OnArchiveAsync($"show {Mbo} {Root} {NeverFetchedDate}");

        run.ExitCode.ShouldBe(Session.Failed);
        run.Error.ShouldContain($"The archive has no {Root} {Mbo} file for {NeverFetchedDate}.");
    }

    [Fact]
    public async Task A_bad_schema_names_the_schemas_there_are()
    {
        var run = await dbnf.OnArchiveAsync($"show {BadSchema} {Root} {Date}");

        run.ExitCode.ShouldBe(Session.Usage);
        run.Error.ShouldContain($"\"{BadSchema}\" is not a schema. Schemas are {Mbo}, ");
    }

    [Fact]
    public async Task A_bad_zone_is_refused()
    {
        var run = await dbnf.OnArchiveAsync($"show {Mbo} {Root} {Date} --tz {BadZone}");

        run.ExitCode.ShouldBe(Session.Usage);
        run.Error.ShouldContain($"\"{BadZone}\" is not a time zone");
    }

    [Fact]
    public async Task A_bad_symbol_is_refused()
    {
        var run = await dbnf.OnArchiveAsync($"show {Mbo} {BadSymbol} {Date}");

        run.ExitCode.ShouldBe(Session.Usage);
        run.Error.ShouldContain($"\"{BadSymbol}\" is not a symbol");
    }

    [Fact]
    public async Task A_bad_date_is_refused_when_parsing()
    {
        var run = await dbnf.OnArchiveAsync($"show {Mbo} {Root} {BadDate}");

        run.ExitCode.ShouldBe(Dbnf.ParseFailure);
        run.Error.ShouldContain(BadDate);
    }

    [Fact]
    public async Task The_day_is_required()
    {
        var run = await dbnf.OnArchiveAsync($"show {Mbo} {Root}");

        run.ExitCode.ShouldBe(Dbnf.ParseFailure);
    }

    [Fact]
    public async Task Damaged_definitions_are_an_error_not_a_day_without_names()
    {
        OrdersOfBothContracts(definitions: false);
        var path = dbnf.Archive.Write(DbnClient.Archive.DbnSyncLayout.Data(Dataset, Definition, Root, Shown), [1, 2, 3]);

        var run = await dbnf.OnArchiveAsync($"show {Mbo} {Root} {Date}");

        run.ExitCode.ShouldBe(Session.Failed);
        run.Error.ShouldContain(path);
    }

    [Fact]
    public async Task A_file_damaged_part_way_through_is_a_plain_error()
    {
        var record = Order(FrontId, Morning, 1m, 1, 'A', 'B', 1);
        var path = dbnf.Archive.Add(Mbo, Shown, record, record[..30]);

        var run = await dbnf.OnArchiveAsync($"show {Mbo} {Root} {Date}");

        run.ExitCode.ShouldBe(Session.Failed);
        run.Error.ShouldContain(path);
        run.Error.ShouldNotContain("   at ");
    }
}
