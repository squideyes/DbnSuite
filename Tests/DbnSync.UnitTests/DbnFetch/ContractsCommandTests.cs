using DbnClient.Dbn;
using DbnFetch;
using DbnSync.UnitTests.ClientTests;
using Shouldly;
using static DbnSync.UnitTests.FetchTests.SampleArchive;
using static DbnSync.UnitTests.TestData;

namespace DbnSync.UnitTests.FetchTests;

[Collection(DbnfCollection.Name)]
public sealed class ContractsCommandTests : IDisposable
{
    private const string Header = "Contract";
    private const string Refused = "The archive refused the key";
    private const string OtherRootName = "ES";
    private const string ExpiredRaw = "MNQH0";
    private const string Expired = "MNQH20";
    private const string LiveRaw = "MNQZ9";
    private const string Live = "MNQZ99";
    private const string UndatedRaw = "MNQU6";
    private const uint ExpiredId = 2001;
    private const uint LiveId = 2002;
    private const uint UndatedId = 2003;

    private readonly Dbnf dbnf = new();

    public void Dispose() => dbnf.Dispose();

    [Fact]
    public async Task A_roots_outright_contracts_are_listed_in_expiry_order()
    {
        dbnf.Archive.AddDefinitions(Day(10, 30));

        var run = await dbnf.OnArchiveAsync($"contracts {Root}");

        run.ExitCode.ShouldBe(Session.Ok);
        run.Lines[0].ShouldStartWith(Header);
        run.Lines[1].ShouldStartWith(Front);
        run.Lines[2].ShouldStartWith(Next);
        run.Lines[^1].ShouldBe("2 contracts.");
    }

    [Fact]
    public async Task A_row_gives_both_names_the_dates_and_the_tick()
    {
        dbnf.Archive.AddDefinitions(Day(10, 30));

        var run = await dbnf.OnArchiveAsync($"contracts {Root}");

        run.Lines[1].ShouldBe($"{Front}    {FrontRaw}  2026-01-02  2026-12-18  0.25");
    }

    [Fact]
    public async Task Spreads_are_left_out_unless_asked_for()
    {
        dbnf.Archive.AddDefinitions(Day(10, 30));

        var plain = await dbnf.OnArchiveAsync($"contracts {Root}");
        var spreads = await dbnf.OnArchiveAsync($"contracts {Root} --spreads");

        plain.Out.ShouldNotContain(SpreadRaw);
        spreads.Out.ShouldContain(Spread);
        spreads.Out.ShouldContain(SpreadRaw);
    }

    [Fact]
    public async Task Active_keeps_only_what_has_not_expired()
    {
        dbnf.Archive.Add(Definition, Day(10, 30),
            Contract(ExpiredId, ExpiredRaw, Root, new DateOnly(2020, 3, 20)),
            Contract(LiveId, LiveRaw, Root, new DateOnly(2099, 12, 18)));

        var run = await dbnf.OnArchiveAsync($"contracts {Root} --active");

        run.Out.ShouldNotContain(Expired);
        run.Out.ShouldContain(Live);
    }

    [Fact]
    public async Task A_contract_with_no_expiry_stays_active_and_shows_dashes()
    {
        dbnf.Archive.Add(Definition, Day(10, 30),
            DbnBytes.Definition(UndatedId, At(Day(10, 30)), UndatedRaw, Root, DefinitionRecord.Future, DbnPrice.Undefined, 0, DbnTime.Undefined, 2026, 9));

        var run = await dbnf.OnArchiveAsync($"contracts {Root} --active");

        run.Lines[1].ShouldEndWith("-       -           -");
    }

    [Fact]
    public async Task The_root_may_be_typed_in_lower_case()
    {
        dbnf.Archive.AddDefinitions(Day(10, 30));

        var run = await dbnf.OnArchiveAsync($"contracts {Root.ToLowerInvariant()}");

        run.Out.ShouldContain(Front);
    }

    [Fact]
    public async Task Contracts_are_gathered_from_each_month_of_definitions()
    {
        dbnf.Archive.Add(Definition, Day(9, 1), FrontDefinition());
        dbnf.Archive.Add(Definition, Day(10, 1), NextDefinition());

        var run = await dbnf.OnArchiveAsync($"contracts {Root}");

        run.Lines[^1].ShouldBe("2 contracts.");
    }

    [Fact]
    public async Task A_root_with_no_definitions_says_so()
    {
        var run = await dbnf.OnArchiveAsync($"contracts {OtherRootName}");

        run.ExitCode.ShouldBe(Session.Ok);
        run.Lines.ShouldBe([$"No {OtherRootName} contracts in the archive's definitions."]);
    }

    [Fact]
    public async Task A_refused_key_fails_with_the_reason()
    {
        using var s3 = new FakeS3(dbnf.ArchiveFolder);
        dbnf.Archive.AddDefinitions(Day(10, 30));
        await dbnf.LogInAsync(s3);
        s3.Deny = true;

        var run = await dbnf.RunAsync($"contracts {Root}");

        run.ExitCode.ShouldBe(Session.Failed);
        run.Error.ShouldContain(Refused);
    }

    [Fact]
    public async Task The_root_is_required()
    {
        var run = await dbnf.OnArchiveAsync("contracts");

        run.ExitCode.ShouldBe(Dbnf.ParseFailure);
    }

    [Fact]
    public async Task Damaged_definitions_are_a_plain_error()
    {
        var path = dbnf.Archive.Write(DbnClient.Archive.DbnSyncLayout.Data(Dataset, Definition, Root, Day(10, 30)), [1, 2, 3]);

        var run = await dbnf.OnArchiveAsync($"contracts {Root}");

        run.ExitCode.ShouldBe(Session.Failed);
        run.Error.ShouldContain(path);
    }
}
