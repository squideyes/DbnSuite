using DbnFetch.Commands;
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

    private void Put(params string[] keys)
    {
        foreach (var key in keys)
            dbnf.Archive.Write(key, [1, 2, 3]);
    }

    [Fact]
    public async Task A_roots_tapes_and_catalog_come_down_in_the_same_layout()
    {
        Put(Catalog, Early, Late, Tick);

        var run = await dbnf.OnArchiveAsync($"tape pull ES {Tapes}");

        run.ExitCode.ShouldBe(DbnFetch.Session.Ok);
        dbnf.Written().ShouldBe([Catalog, Tick, Early, Late], ignoreOrder: true);
    }

    [Fact]
    public async Task Dates_and_kinds_narrow_what_comes_down()
    {
        Put(Catalog, Early, Late, Tick);

        await dbnf.OnArchiveAsync($"tape pull ES {Tapes} --from 2026-10-29 --kind s1");

        dbnf.Written().ShouldBe([Catalog, Late], ignoreOrder: true);
    }

    [Fact]
    public async Task A_file_already_here_is_left_alone()
    {
        Put(Catalog, Late);

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
        Put(Catalog);

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
