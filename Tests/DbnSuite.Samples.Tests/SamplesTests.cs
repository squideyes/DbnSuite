using DbnSuite.Client.Tape;
using DbnSuite.Samples;
using Shouldly;
using static DbnSuite.Testing.TestData;

namespace DbnSuite.Tests.Samples;

/// <summary>
/// Every sample runs, on synthetic data: an archive made here, and the synthetic tapes in
/// Clients/python/tests/fixtures. They write to the console and read the current folder, as the
/// docs show them doing, so these tests take both over and run one at a time.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class SamplesTests : IDisposable
{
    private const uint Instrument = 42;
    private const long Price = 6_850_250_000_000;
    private const uint Quantity = 3;
    private const ulong OrderId = 6_883_391_723_797;

    private readonly StringWriter output = new();
    private readonly TextWriter console = Console.Out;
    private readonly string folder = Environment.CurrentDirectory;

    public SamplesTests() => Console.SetOut(output);

    public void Dispose()
    {
        Console.SetOut(console);
        Environment.CurrentDirectory = folder;
    }

    private string[] Lines => output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    private static ArchiveFolder Archive()
    {
        var archive = new ArchiveFolder();

        archive.Add("mbo", "MES", Day(10, 30), DbnBytes.Mbo(Instrument, DbnBytes.Start, Price, Quantity, 'T', 'A', OrderId));
        archive.AddEmpty("mbo", "MES", Day(11, 1));

        return archive;
    }

    [Fact]
    public async Task Listing_names_each_series_and_its_days()
    {
        using var folder = Archive();
        using var archive = folder.Open();

        await ArchiveSamples.ListAsync(archive, CancellationToken.None);

        Lines.ShouldHaveSingleItem().ShouldStartWith("MES  mbo             1 days");
    }

    [Fact]
    public async Task Reading_prints_each_trade()
    {
        using var folder = Archive();
        using var archive = folder.Open();

        await ArchiveSamples.ReadAsync(archive, CancellationToken.None);

        Lines.ShouldHaveSingleItem().ShouldEndWith($"{Quantity} @ 6850.25");
    }

    [Fact]
    public async Task A_day_with_no_data_is_an_error_to_branch_on()
    {
        using var folder = Archive();
        using var archive = folder.Open();

        await ArchiveSamples.ErrorsAsync(archive, CancellationToken.None);

        Lines.ShouldBe(["A day with no trading."]);
    }

    [Fact]
    public async Task Exporting_runs_to_an_answer()
    {
        using var folder = Archive();
        using var archive = folder.Open();
        var output = Directory.CreateTempSubdirectory();

        try
        {
            Environment.CurrentDirectory = output.FullName;

            // The synthetic archive holds no ES, so the answer is the error saying so; the bars themselves
            // are the exporter's own tests' business. This holds the sample to the API it shows.
            await Should.NotThrowAsync(() => ArchiveSamples.ExportAsync(archive, CancellationToken.None));
        }
        finally
        {
            Environment.CurrentDirectory = this.folder;
            output.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Replaying_hands_every_kind_of_event_to_the_strategy()
    {
        Environment.CurrentDirectory = Repository.Fixtures;

        await TapeSamples.ReplayAsync(CancellationToken.None);

        Lines.ShouldHaveSingleItem().ShouldMatch(@"^replay: [1-9][\d,]* quotes, [1-9][\d,]* trades .*, [1-9]\d* state changes");
    }

    [Fact]
    public async Task Bars_roll_up_from_an_s1_tape()
    {
        Environment.CurrentDirectory = Repository.Fixtures;
        var tape = (await Tape.LoadAsync("tapes/GLBX.MDP3/s1/ES/2026/ES.s1.20261029.tape", CancellationToken.None)).Value;

        TapeSamples.Bars(tape);

        Lines.ShouldHaveSingleItem().ShouldMatch(@"[1-9][\d,]* bars$");
    }

    [Fact]
    public async Task A_monte_carlo_run_deals_warm_up_and_tradable_sessions()
    {
        Environment.CurrentDirectory = Repository.Fixtures;

        await TapeSamples.MonteCarloAsync(CancellationToken.None);

        Lines.ShouldHaveSingleItem().ShouldMatch(@"^monte carlo, run 0: 0 quotes, [1-9][\d,]* trades \([1-9][\d,]* tradable\)");
    }
}
