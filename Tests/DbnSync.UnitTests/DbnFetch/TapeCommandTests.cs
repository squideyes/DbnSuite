using DbnClient.Tape;
using DbnSync.UnitTests.TapeFormatTests;
using Shouldly;

namespace DbnSync.UnitTests.FetchTests;

[Collection(DbnfCollection.Name)]
public sealed class TapeCommandTests : IDisposable
{
    private const string FirstDate = "--from 2026-10-29";
    private const string TapeName = "ESZ6.s1.tape";
    private const string NotATapeName = "notes.txt";
    private const string NotATapeText = "This is not a tape.";

    private readonly Dbnf dbnf = new();

    public void Dispose() => dbnf.Dispose();

    private string Tapes => $"--tapes \"{dbnf.Output}\"";

    private async Task<string> SaveAsync(TapeWriter writer)
    {
        var path = Path.Combine(dbnf.Home, TapeName);
        var saved = await writer.SaveAsync(path, CancellationToken.None);

        saved.IsSuccess.ShouldBeTrue(saved.Error?.Message);

        return path;
    }

    private static S1Writer OneBar(bool compress = false)
    {
        var writer = new S1Writer(TapeFixture.Info(compress));

        writer.Add(TapeFixture.OpenMicros, 30_842, 30_843, 30_841, 30_843, 5);

        return writer;
    }

    [Theory]
    [InlineData("s1", 1)]
    [InlineData("trade", 1)]
    [InlineData("L1", 3)]
    public async Task Schema_lists_a_kinds_fields(string kind, int lines)
    {
        var run = await dbnf.RunAsync($"tape schema {kind}");

        run.ExitCode.ShouldBe(DbnFetch.Session.Ok);
        run.Lines.Count.ShouldBe(lines);
    }

    [Fact]
    public async Task Schema_refuses_an_unknown_kind_as_bad_usage()
    {
        var run = await dbnf.RunAsync("tape schema order");

        run.ExitCode.ShouldBe(Dbnf.ParseFailure);
        run.Error.ShouldContain("A kind is s1, trade or l1.");
    }

    [Fact]
    public async Task Info_shows_the_header_with_the_session_in_chicago_time()
    {
        var path = await SaveAsync(OneBar());

        var run = await dbnf.RunAsync($"tape info \"{path}\"");

        run.ExitCode.ShouldBe(DbnFetch.Session.Ok);
        run.Lines[0].ShouldBe("ESZ6  S1 v1  GLBX.MDP3  trade date 2026-10-29");
        run.Lines[1].ShouldBe("session  10-28 17:00 to 10-29 16:00 Chicago");
        run.Lines[2].ShouldBe("rth      none");
        run.Lines[3].ShouldStartWith("events   1, ");
    }

    [Fact]
    public async Task Info_says_when_a_tape_is_compressed()
    {
        var path = await SaveAsync(OneBar(compress: true));

        var run = await dbnf.RunAsync($"tape info \"{path}\"");

        run.Lines[3].ShouldEndWith("in zstd");
    }

    [Fact]
    public async Task Info_fails_on_a_file_that_is_not_a_tape()
    {
        var path = Path.Combine(dbnf.Home, NotATapeName);

        await File.WriteAllTextAsync(path, NotATapeText);

        var run = await dbnf.RunAsync($"tape info \"{path}\"");

        run.ExitCode.ShouldBe(DbnFetch.Session.Failed);
        run.Error.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Bench_times_a_replay_and_reports_nothing_allocated()
    {
        var writer = new TradeWriter(TapeFixture.Info());

        writer.Add(TapeFixture.OpenMicros, 30_842, 3, TradeSide.Buyer);

        var run = await dbnf.RunAsync($"tape bench \"{await SaveAsync(writer)}\"");

        run.ExitCode.ShouldBe(DbnFetch.Session.Ok);
        run.Out.ShouldContain("Trade replay: ");
        run.Out.ShouldContain(", 0 bytes allocated");
    }

    [Theory]
    [InlineData("catalog")]
    [InlineData("build")]
    [InlineData("verify")]
    public async Task A_command_that_reads_the_archive_needs_the_tape_folder(string command)
    {
        var run = await dbnf.OnArchiveAsync($"tape {command} ES {FirstDate}");

        run.ExitCode.ShouldBe(Dbnf.ParseFailure);
        run.Error.ShouldContain("--tapes");
    }

    [Theory]
    [InlineData("build")]
    [InlineData("verify")]
    public async Task Building_or_proving_needs_a_trade_date(string command)
    {
        var run = await dbnf.OnArchiveAsync($"tape {command} ES {Tapes}");

        run.ExitCode.ShouldBe(Dbnf.ParseFailure);
        run.Error.ShouldContain("--from");
    }

    [Theory]
    [InlineData("build")]
    [InlineData("verify")]
    public async Task Building_or_proving_fails_without_a_catalog(string command)
    {
        var run = await dbnf.OnArchiveAsync($"tape {command} ES {Tapes} {FirstDate}");

        run.ExitCode.ShouldBe(DbnFetch.Session.Failed);
        run.Error.ShouldNotBeEmpty();
    }
}
