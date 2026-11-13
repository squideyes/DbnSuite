using DbnSuite.Fetch;
using Shouldly;
using static DbnSuite.UnitTests.FetchTests.SampleArchive;
using static DbnSuite.UnitTests.TestData;

namespace DbnSuite.UnitTests.FetchTests;

[Collection(DbnfCollection.Name)]
public sealed class RollsCommandTests : IDisposable
{
    private const string Calendar = "MNQ.c.0";
    private const string Volume = "MNQ.v.0";
    private const string OpenInterestSeries = "MNQ.n.0";
    private const string Second = "MNQ.c.1";
    private const string BadSymbol = "NOT!A!SYMBOL";
    private const string OverTheRoll = "--from 2026-12-08 --to 2026-12-14";
    private const string BeforeTheRoll = "--from 2026-12-01 --to 2026-12-03";
    private const string Backwards = "--from 2026-12-14 --to 2026-12-08";
    private const string NoRolls = "No rolls in the range.";
    private const string Header = "From trade date";
    private const string OtherDataset = "XNAS.ITCH";
    private const string Unsupported = "are defined for GLBX.MDP3 only";

    private static readonly DateOnly Listing = new(2026, 12, 1);
    private static readonly DateOnly DayBefore = new(2026, 12, 9);

    private readonly Dbnf dbnf = new();

    public void Dispose() => dbnf.Dispose();

    private void Contracts() => dbnf.Archive.Add(Definition, Listing, FrontDefinition(), NextDefinition(), SpreadDefinition());

    private void Settlements()
    {
        dbnf.Archive.Add(Statistics, DayBefore,
            Stat(FrontId, At(DayBefore, 21), Settlement, Price(7820.25m)),
            Stat(NextId, At(DayBefore, 21), Settlement, Price(7900.5m)));
    }

    [Fact]
    public async Task A_calendar_series_rolls_eight_days_before_expiry()
    {
        Contracts();
        Settlements();

        var run = await dbnf.OnArchiveAsync($"rolls {Calendar} {OverTheRoll}");

        run.ExitCode.ShouldBe(Session.Ok);
        run.Lines[0].ShouldBe($"{Calendar} holds {Front} on 2026-12-08.");
        run.Lines[2].ShouldStartWith(Header);
        run.Lines[3].ShouldStartWith($"2026-12-10       {Front}  {Next}");
    }

    [Fact]
    public async Task A_roll_shows_both_settlements_and_the_gap()
    {
        Contracts();
        Settlements();

        var run = await dbnf.OnArchiveAsync($"rolls {Calendar} {OverTheRoll}");

        run.Lines[3].ShouldEndWith("7820.25      7900.5  80.25");
    }

    [Fact]
    public async Task A_roll_without_settlements_shows_dashes()
    {
        Contracts();

        var run = await dbnf.OnArchiveAsync($"rolls {Calendar} {OverTheRoll}");

        run.Lines[3].ShouldEndWith("-           -    -");
    }

    [Fact]
    public async Task Roll_days_moves_the_roll()
    {
        Contracts();

        var run = await dbnf.OnArchiveAsync($"rolls {Calendar} {OverTheRoll} --roll-days 6");

        run.Lines[3].ShouldStartWith("2026-12-14");
    }

    [Fact]
    public async Task A_range_with_no_roll_says_so()
    {
        Contracts();

        var run = await dbnf.OnArchiveAsync($"rolls {Calendar} {BeforeTheRoll}");

        run.ExitCode.ShouldBe(Session.Ok);
        run.Lines.ShouldBe([$"{Calendar} holds {Front} on 2026-12-01.", NoRolls]);
    }

    [Fact]
    public async Task The_second_contract_of_a_series_is_the_next_one()
    {
        Contracts();

        var run = await dbnf.OnArchiveAsync($"rolls {Second} {BeforeTheRoll}");

        run.Lines[0].ShouldBe($"{Second} holds {Next} on 2026-12-01.");
    }

    [Fact]
    public async Task With_no_definitions_no_contract_is_held()
    {
        var run = await dbnf.OnArchiveAsync($"rolls {Calendar} {BeforeTheRoll}");

        run.ExitCode.ShouldBe(Session.Ok);
        run.Lines.ShouldBe([$"{Calendar} holds no contract on 2026-12-01.", NoRolls]);
    }

    [Fact]
    public async Task A_volume_series_follows_the_previous_sessions_volume()
    {
        Contracts();
        dbnf.Archive.Add(SampleArchive.Ohlcv, new DateOnly(2026, 12, 8),
            Bar(FrontId, At(new DateOnly(2026, 12, 8), 15), 1m, 100), Bar(NextId, At(new DateOnly(2026, 12, 8), 15), 1m, 10));
        dbnf.Archive.Add(SampleArchive.Ohlcv, DayBefore,
            Bar(FrontId, At(DayBefore, 15), 1m, 10), Bar(NextId, At(DayBefore, 15), 1m, 100));

        var run = await dbnf.OnArchiveAsync($"rolls {Volume} --from 2026-12-09 --to 2026-12-11");

        run.Lines[0].ShouldBe($"{Volume} holds {Front} on 2026-12-09.");
        run.Lines[3].ShouldStartWith($"2026-12-10       {Front}  {Next}");
    }

    [Fact]
    public async Task An_open_interest_series_follows_open_interest()
    {
        Contracts();
        dbnf.Archive.Add(Statistics, new DateOnly(2026, 12, 8),
            Stat(FrontId, At(new DateOnly(2026, 12, 8), 15), OpenInterest, quantity: 100),
            Stat(NextId, At(new DateOnly(2026, 12, 8), 15), OpenInterest, quantity: 10));
        dbnf.Archive.Add(Statistics, DayBefore,
            Stat(FrontId, At(DayBefore, 15), OpenInterest, quantity: 10),
            Stat(NextId, At(DayBefore, 15), OpenInterest, quantity: 100));

        var run = await dbnf.OnArchiveAsync($"rolls {OpenInterestSeries} --from 2026-12-09 --to 2026-12-11");

        run.Lines[3].ShouldStartWith($"2026-12-10       {Front}  {Next}");
    }

    [Fact]
    public async Task A_name_that_is_not_continuous_is_refused()
    {
        var run = await dbnf.OnArchiveAsync($"rolls {Front} {OverTheRoll}");

        run.ExitCode.ShouldBe(Session.Usage);
        run.Error.ShouldContain($"{Front} is not a continuous name such as ES.v.0.");
    }

    [Fact]
    public async Task A_bad_symbol_is_refused()
    {
        var run = await dbnf.OnArchiveAsync($"rolls {BadSymbol} {OverTheRoll}");

        run.ExitCode.ShouldBe(Session.Usage);
        run.Error.ShouldContain($"\"{BadSymbol}\" is not a symbol");
    }

    [Fact]
    public async Task A_bad_range_is_refused()
    {
        var run = await dbnf.OnArchiveAsync($"rolls {Calendar} {Backwards}");

        run.ExitCode.ShouldBe(Session.Usage);
    }

    [Fact]
    public async Task A_range_is_required()
    {
        var run = await dbnf.OnArchiveAsync($"rolls {Calendar}");

        run.ExitCode.ShouldBe(Session.Usage);
    }

    [Fact]
    public async Task A_dataset_with_no_known_trading_day_is_refused()
    {
        dbnf.Profiles.Save(Profiles.DefaultName, new Profile { Location = dbnf.ArchiveFolder, Dataset = OtherDataset }, "");

        var run = await dbnf.RunAsync($"rolls {Calendar} {OverTheRoll}");

        run.ExitCode.ShouldBe(Session.Usage);
        run.Error.ShouldContain(Unsupported);
    }

    [Fact]
    public async Task A_roll_decided_by_fridays_volume_shows_monday()
    {
        var thursday = new DateOnly(2026, 12, 10);
        var friday = new DateOnly(2026, 12, 11);
        Contracts();
        dbnf.Archive.Add(SampleArchive.Ohlcv, thursday,
            Bar(FrontId, At(thursday, 15), 1m, 100), Bar(NextId, At(thursday, 15), 1m, 10));
        dbnf.Archive.Add(SampleArchive.Ohlcv, friday,
            Bar(FrontId, At(friday, 15), 1m, 10), Bar(NextId, At(friday, 15), 1m, 100));

        var run = await dbnf.OnArchiveAsync($"rolls {Volume} --from 2026-12-10 --to 2026-12-15");

        run.Lines[3].ShouldStartWith($"2026-12-14       {Front}  {Next}");
    }
}
