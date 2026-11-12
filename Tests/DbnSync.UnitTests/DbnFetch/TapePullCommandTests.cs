using DbnFetch.Commands;
using DbnSync.UnitTests.TapeFormatTests;
using Shouldly;

namespace DbnSync.UnitTests.FetchTests;

[Collection(DbnfCollection.Name)]
public sealed class TapePullCommandTests : IDisposable
{
    private const string Catalog = "GLBX.MDP3/catalog/ES.catalog.json";
    private const string Early = "GLBX.MDP3/s1/ES/2026/ESZ6.s1.20261028.tape";
    private const string Late = "GLBX.MDP3/s1/ES/2026/ESZ6.s1.20261029.tape";
    private const string Tick = "GLBX.MDP3/l1/ES/2026/ESZ6.l1.20261029.tape";

    private readonly Dbnf dbnf = new();

    public void Dispose() => dbnf.Dispose();

    private string Tapes => $"--tapes \"{dbnf.Output}\"";

    private static readonly DateOnly EarlyDate = new(2026, 10, 28);
    private static readonly DateOnly LateDate = new(2026, 10, 29);

    /// <summary>Puts objects in the store: tapes as placeholder bytes, the catalog as a real one whose days are full.</summary>
    private async Task PutAsync(params string[] keys)
    {
        foreach (var key in keys)
            dbnf.Archive.Write(key, key == Catalog ? await TapeFixture.JsonOfAsync(CatalogOf(TapeFixture.FullDay(EarlyDate), TapeFixture.FullDay(LateDate))) : [1, 2, 3]);
    }

    private static DbnClient.Tape.Catalog CatalogOf(params DbnClient.Tape.CatalogDay[] days) => new(TapeFixture.Dataset, TapeFixture.Root, [], days);

    [Fact]
    public async Task A_date_that_is_not_a_full_ETH_day_neither_comes_down_nor_stays()
    {
        var shortDay = TapeFixture.FullDay(LateDate) with
        {
            Sessions = [TapeFixture.FullSession(LateDate) with { Close = TapeFixture.Nanos(DbnClient.Time.Sessions.SessionEnd(LateDate).AddHours(-4)) }]
        };

        dbnf.Archive.Write(Catalog, await TapeFixture.JsonOfAsync(CatalogOf(TapeFixture.FullDay(EarlyDate), shortDay)));
        dbnf.Archive.Write(Early, [1, 2, 3]);
        dbnf.Archive.Write(Late, [1, 2, 3]);

        var stale = Path.Combine(dbnf.Output, Tick.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(stale)!);
        await File.WriteAllBytesAsync(stale, [1, 2, 3]);

        var run = await dbnf.OnArchiveAsync($"tape pull ES {Tapes}");

        run.ExitCode.ShouldBe(DbnFetch.Session.Ok);
        dbnf.Written().ShouldBe([Catalog, Early], ignoreOrder: true);
        run.Out.ShouldContain("1 tapes removed here");
    }

    [Fact]
    public async Task A_roots_tapes_and_catalog_come_down_in_the_same_layout()
    {
        await PutAsync(Catalog, Early, Late, Tick);

        var run = await dbnf.OnArchiveAsync($"tape pull ES {Tapes}");

        run.ExitCode.ShouldBe(DbnFetch.Session.Ok);
        dbnf.Written().ShouldBe([Catalog, Tick, Early, Late], ignoreOrder: true);
    }

    [Fact]
    public async Task Dates_and_kinds_narrow_what_comes_down()
    {
        await PutAsync(Catalog, Early, Late, Tick);

        await dbnf.OnArchiveAsync($"tape pull ES {Tapes} --from 2026-10-29 --kind s1");

        dbnf.Written().ShouldBe([Catalog, Late], ignoreOrder: true);
    }

    [Theory]
    [InlineData("GLBX.MDP3/s1/ES/../../../../outside.s1.20261029.tape")]
    [InlineData("../outside.s1.20261029.tape")]
    public void A_key_that_climbs_out_of_the_folder_is_refused(string key)
    {
        var refused = Should.Throw<DbnClient.Archive.ArchiveException>(() => TapePullCommand.TargetOf(dbnf.Output, key));

        refused.Error.Code.ShouldBe(DbnClient.Errors.UnknownLayout);
    }

    [Fact]
    public void A_key_lands_inside_the_folder()
    {
        TapePullCommand.TargetOf(dbnf.Output, Late).ShouldBe(Path.Combine(dbnf.Output, Late.Replace('/', Path.DirectorySeparatorChar)));
    }

    [Fact]
    public async Task A_date_in_another_format_is_bad_usage()
    {
        await PutAsync(Catalog, Late);

        var run = await dbnf.OnArchiveAsync($"tape pull ES {Tapes} --from 10/29/2026");

        run.ExitCode.ShouldBe(Dbnf.ParseFailure);
        run.Error.ShouldContain("is not a date");
        dbnf.Written().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_file_already_here_is_left_alone()
    {
        await PutAsync(Catalog, Late);

        await dbnf.OnArchiveAsync($"tape pull ES {Tapes}");

        var run = await dbnf.OnArchiveAsync($"tape pull ES {Tapes}");

        run.Out.ShouldContain("0 files copied");
        run.Out.ShouldContain("2 already here");
    }

    [Fact]
    public async Task A_store_with_no_catalog_for_the_root_fails()
    {
        var run = await dbnf.OnArchiveAsync($"tape pull ES {Tapes}");

        run.ExitCode.ShouldBe(DbnFetch.Session.Failed);
        run.Error.ShouldContain("no catalog for ES");
    }

    [Fact]
    public async Task An_unknown_kind_is_bad_usage()
    {
        await PutAsync(Catalog);

        var run = await dbnf.OnArchiveAsync($"tape pull ES {Tapes} --kind order");

        run.ExitCode.ShouldBe(Dbnf.ParseFailure);
    }

    [Theory]
    [InlineData(Late, true)]
    [InlineData(Early, false)]
    [InlineData("GLBX.MDP3/s1/ES/2026/notes.txt", false)]
    public void Only_tapes_in_the_span_are_wanted(string key, bool wanted)
    {
        TapePullCommand.InSpan(key, new DateOnly(2026, 10, 29), null).ShouldBe(wanted);
    }
}
