using DbnClient;
using DbnClient.Archive;
using DbnClient.Tape;
using DbnSpool;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace DbnSync.UnitTests.SpoolTests;

public sealed class SpoolPassTests : IDisposable
{
    private const string Dataset = "GLBX.MDP3";
    private const string Root = "ES";
    private const string Symbol = "ESZ6";
    private const string Mbo = "mbo";
    private const string TapeKey = "GLBX.MDP3/s1/ES/2026/ESZ6.s1.20261027.tape";
    private const string CatalogKey = "GLBX.MDP3/catalog/ES.catalog.json";
    private const string LedgerKey = "GLBX.MDP3/spool/ES.json";
    private const string Build = "build-2";
    private const string EarlierBuild = "build-1";
    private const string SetAsideProblem = "ES: 2026-10-27 set aside after stopping DbnSpool 2 times";

    private static readonly DateOnly Monday = new(2026, 10, 26);
    private static readonly DateOnly Tuesday = Monday.AddDays(1);
    private static readonly DateOnly Wednesday = Monday.AddDays(2);

    private const string WentAway = "The archive went away.";
    private static readonly string[] Sources = ["ohlcv-1s", "definition", "statistics", "status"];

    private readonly string home = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly ISpoolEngine engine = Substitute.For<ISpoolEngine>();
    private readonly SpoolOptions options;
    private readonly FolderTapeShelf shelf;

    public SpoolPassTests()
    {
        options = new SpoolOptions { WorkPath = Path.Combine(home, "work") };
        shelf = new FolderTapeShelf(Path.Combine(home, "shelf"));

        engine.Dataset.Returns(Dataset);
        engine.Build.Returns(Build);
        engine.CatalogAsync(Root, Arg.Any<CancellationToken>()).Returns((Result<Catalog>)TestCatalog());
        engine.ProveAsync(Arg.Any<Catalog>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns([new ProofResult(Symbol, "S1 against source", true, null)]);
        engine.BuildAsync(Arg.Any<Catalog>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(c => (Result<BuildReport>)Built(c.ArgAt<DateOnly>(1)));

        Archived(Tuesday);
        Mbos(Monday, Tuesday);
    }

    public void Dispose()
    {
        if (Directory.Exists(home))
            Directory.Delete(home, recursive: true);
    }

    private SpoolPass Pass() => new(engine, shelf, options, NullLogger<SpoolPass>.Instance);

    private static Catalog TestCatalog() => new(Dataset, Root, [],
    [
        new CatalogDay { TradeDate = Tuesday, Sessions = [], Contracts = [] }
    ]);

    private BuildReport Built(DateOnly date)
    {
        var path = TapePaths.For(options.TapeFolder, Dataset, TapeKind.S1, Root, Symbol, date);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]);

        return new BuildReport(date, [new TapeOutcome(Symbol, TapeKind.S1, path, 1, 3, null)], []);
    }

    private static Result<IReadOnlyList<StoredDay>> Stored(IEnumerable<DateOnly> days) =>
        days.Select(d => new StoredDay(d, new StoredObject(DbnSyncLayout.Data(Dataset, Mbo, Root, d), 1, ""))).ToList();

    private void Archived(params DateOnly[] days)
    {
        foreach (var schema in Sources)
            engine.DaysAsync(schema, Root, Arg.Any<CancellationToken>()).Returns(Stored(days));
    }

    private void Mbos(params DateOnly[] days) =>
        engine.DaysAsync(Mbo, Root, Arg.Any<CancellationToken>()).Returns(Stored(days));

    private async Task<SpoolLedger> LedgerAsync() =>
        SpoolLedger.From(await shelf.ReadAsync(LedgerKey, CancellationToken.None));

    [Fact]
    public async Task A_new_date_reaches_the_shelf_with_its_catalog_and_ledger_entry()
    {
        var result = await Pass().RunAsync(Root, CancellationToken.None);

        result.ShouldBe(new RootResult(Root, 1, 0, false));
        (await shelf.ReadAsync(TapeKey, CancellationToken.None)).ShouldNotBeNull();
        (await shelf.ReadAsync(CatalogKey, CancellationToken.None)).ShouldNotBeNull();
        (await LedgerAsync()).Spooled(Tuesday).ShouldBe(new SpooledDate(Tuesday, 1, true));
    }

    [Fact]
    public async Task The_work_folder_is_cleared_after_a_date()
    {
        await Pass().RunAsync(Root, CancellationToken.None);

        Directory.EnumerateFiles(options.TapeFolder, "*.tape", SearchOption.AllDirectories).ShouldBeEmpty();
        engine.Received(1).Release();
    }

    [Fact]
    public async Task A_second_pass_over_the_same_archive_does_no_work()
    {
        await Pass().RunAsync(Root, CancellationToken.None);

        var result = await Pass().RunAsync(Root, CancellationToken.None);

        result.Skipped.ShouldBeTrue();
        await engine.Received(1).CatalogAsync(Root, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_date_that_fails_its_proof_stays_off_the_shelf()
    {
        engine.ProveAsync(Arg.Any<Catalog>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns([new ProofResult(Symbol, "S1 against source", false, "a bar differs")]);

        var result = await Pass().RunAsync(Root, CancellationToken.None);

        result.Failed.ShouldBe(1);
        (await shelf.ReadAsync(TapeKey, CancellationToken.None)).ShouldBeNull();
        (await LedgerAsync()).Spooled(Tuesday).ShouldBeNull();
    }

    [Fact]
    public async Task A_failed_date_is_tried_again_when_the_archive_changes()
    {
        engine.BuildAsync(Arg.Any<Catalog>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns((Result<BuildReport>)new Error(TapeErrors.Refused, "A source day is missing."));

        await Pass().RunAsync(Root, CancellationToken.None);

        engine.BuildAsync(Arg.Any<Catalog>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(c => (Result<BuildReport>)Built(c.ArgAt<DateOnly>(1)));
        Archived(Tuesday.AddDays(1));
        Mbos(Monday, Tuesday, Tuesday.AddDays(1));

        var result = await Pass().RunAsync(Root, CancellationToken.None);

        result.Spooled.ShouldBe(1);
    }

    [Fact]
    public async Task A_refused_tape_keeps_the_whole_date_off_the_shelf()
    {
        engine.BuildAsync(Arg.Any<Catalog>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).Returns(c =>
        {
            var report = Built(c.ArgAt<DateOnly>(1));

            return (Result<BuildReport>)(report with
            {
                Tapes = [.. report.Tapes, new TapeOutcome(Symbol, TapeKind.L1, null, 0, 0, new Error(TapeErrors.Refused, "The book crossed."))]
            });
        });

        var result = await Pass().RunAsync(Root, CancellationToken.None);

        result.Failed.ShouldBe(1);
        (await shelf.ReadAsync(TapeKey, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task A_contract_mbo_does_not_hold_is_no_failure()
    {
        engine.BuildAsync(Arg.Any<Catalog>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).Returns(c =>
        {
            var report = Built(c.ArgAt<DateOnly>(1));

            return (Result<BuildReport>)(report with
            {
                Tapes = [.. report.Tapes, new TapeOutcome(Symbol, TapeKind.L1, null, 0, 0, new Error(TapeErrors.NotHeld, "Not held."))]
            });
        });

        var result = await Pass().RunAsync(Root, CancellationToken.None);

        result.Spooled.ShouldBe(1);
        (await LedgerAsync()).Spooled(Tuesday)!.Tapes.ShouldBe(1);
    }

    [Fact]
    public async Task Proving_can_be_turned_off()
    {
        options.Prove = false;

        await Pass().RunAsync(Root, CancellationToken.None);

        await engine.DidNotReceive().ProveAsync(Arg.Any<Catalog>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_archive_without_mbo_still_spools_bars()
    {
        Mbos();

        var result = await Pass().RunAsync(Root, CancellationToken.None);

        result.Spooled.ShouldBe(1);
        (await LedgerAsync()).Spooled(Tuesday)!.Ticks.ShouldBeFalse();
    }

    [Fact]
    public async Task A_date_comes_back_when_its_mbo_arrives_later()
    {
        Mbos();

        await Pass().RunAsync(Root, CancellationToken.None);

        Mbos(Monday, Tuesday);

        var result = await Pass().RunAsync(Root, CancellationToken.None);

        result.Spooled.ShouldBe(1);
        (await LedgerAsync()).Spooled(Tuesday)!.Ticks.ShouldBeTrue();
    }

    [Fact]
    public async Task Nothing_happens_until_every_schema_has_arrived()
    {
        Archived();

        var result = await Pass().RunAsync(Root, CancellationToken.None);

        result.Skipped.ShouldBeTrue();
        await engine.DidNotReceive().CatalogAsync(Root, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_date_that_stopped_DbnSpool_twice_is_set_aside()
    {
        await StoppedAsync(2, Build);

        var result = await Pass().RunAsync(Root, CancellationToken.None);

        result.ShouldBe(new RootResult(Root, 0, 1, false, SetAsideProblem, SetAside: 1));
        await engine.DidNotReceive().BuildAsync(Arg.Any<Catalog>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
        (await LedgerAsync()).Attempt(Tuesday)!.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_date_that_stopped_DbnSpool_once_is_tried_again()
    {
        await StoppedAsync(1, Build);

        var result = await Pass().RunAsync(Root, CancellationToken.None);

        result.Spooled.ShouldBe(1);
        (await LedgerAsync()).Attempt(Tuesday).ShouldBeNull();
    }

    [Fact]
    public async Task A_date_set_aside_is_tried_again_once_the_code_changes()
    {
        await StoppedAsync(2, EarlierBuild);

        var result = await Pass().RunAsync(Root, CancellationToken.None);

        result.ShouldBe(new RootResult(Root, 1, 0, false));
        (await LedgerAsync()).Attempt(Tuesday).ShouldBeNull();
    }

    [Fact]
    public async Task The_attempt_is_on_the_shelf_before_the_work_starts()
    {
        SpoolAttempt? during = null;

        engine.BuildAsync(Arg.Any<Catalog>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).Returns(async c =>
        {
            during = (await LedgerAsync()).Attempt(Tuesday);

            return (Result<BuildReport>)Built(c.ArgAt<DateOnly>(1));
        });

        await Pass().RunAsync(Root, CancellationToken.None);

        during.ShouldBe(new SpoolAttempt(Tuesday, 1, Build));
    }

    [Fact]
    public async Task A_failed_date_is_not_counted_as_a_stop()
    {
        engine.BuildAsync(Arg.Any<Catalog>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns((Result<BuildReport>)new Error(TapeErrors.Refused, "A source day is missing."));

        await Pass().RunAsync(Root, CancellationToken.None);

        (await LedgerAsync()).Attempt(Tuesday).ShouldBeNull();
    }

    [Fact]
    public async Task An_exception_is_not_counted_as_a_stop()
    {
        engine.BuildAsync(Arg.Any<Catalog>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Result<BuildReport>>(new IOException(WentAway)));

        var result = await Pass().RunAsync(Root, CancellationToken.None);

        result.Failed.ShouldBe(1);
        result.Trouble.ShouldBe($"2026-10-27: {WentAway}");
        (await LedgerAsync()).Attempt(Tuesday).ShouldBeNull();
    }

    [Fact]
    public async Task A_date_that_throws_does_not_hold_up_the_dates_after_it()
    {
        Days(Tuesday, Wednesday);
        Mbos(Monday, Tuesday, Wednesday);
        engine.BuildAsync(Arg.Any<Catalog>(), Tuesday, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Result<BuildReport>>(new IOException(WentAway)));

        var result = await Pass().RunAsync(Root, CancellationToken.None);

        result.Spooled.ShouldBe(1);
        result.Failed.ShouldBe(1);
        (await LedgerAsync()).Spooled(Wednesday).ShouldNotBeNull();
        (await shelf.ReadAsync(CatalogKey, CancellationToken.None)).ShouldNotBeNull();
    }

    [Fact]
    public async Task A_date_that_threw_is_tried_again_at_the_next_pass_with_the_archive_unchanged()
    {
        engine.BuildAsync(Arg.Any<Catalog>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Result<BuildReport>>(new IOException(WentAway)));

        await Pass().RunAsync(Root, CancellationToken.None);

        engine.BuildAsync(Arg.Any<Catalog>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(c => (Result<BuildReport>)Built(c.ArgAt<DateOnly>(1)));

        var again = await Pass().RunAsync(Root, CancellationToken.None);

        again.ShouldBe(new RootResult(Root, 1, 0, false));
    }

    [Fact]
    public async Task Dates_that_throw_one_after_another_stop_the_pass()
    {
        Days(Tuesday, Wednesday, Wednesday.AddDays(1), Wednesday.AddDays(2));
        Mbos(Monday, Tuesday, Wednesday, Wednesday.AddDays(1), Wednesday.AddDays(2));
        engine.BuildAsync(Arg.Any<Catalog>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Result<BuildReport>>(new IOException(WentAway)));

        await Should.ThrowAsync<IOException>(() => Pass().RunAsync(Root, CancellationToken.None));

        await engine.Received(SpoolPass.MostThrowsInARow).BuildAsync(Arg.Any<Catalog>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
        (await LedgerAsync()).Attempts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Running_out_of_memory_is_counted_as_a_stop()
    {
        engine.BuildAsync(Arg.Any<Catalog>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Result<BuildReport>>(new OutOfMemoryException()));

        await Should.ThrowAsync<OutOfMemoryException>(() => Pass().RunAsync(Root, CancellationToken.None));
        await Should.ThrowAsync<OutOfMemoryException>(() => Pass().RunAsync(Root, CancellationToken.None));

        var third = await Pass().RunAsync(Root, CancellationToken.None);

        third.ShouldBe(new RootResult(Root, 0, 1, false, SetAsideProblem, SetAside: 1));
        await engine.Received(2).BuildAsync(Arg.Any<Catalog>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_shutdown_is_not_counted_as_a_stop()
    {
        using var stop = new CancellationTokenSource();

        engine.BuildAsync(Arg.Any<Catalog>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).Returns<Result<BuildReport>>(_ =>
        {
            stop.Cancel();

            throw new OperationCanceledException(stop.Token);
        });

        await Should.ThrowAsync<OperationCanceledException>(() => Pass().RunAsync(Root, stop.Token));

        (await LedgerAsync()).Attempt(Tuesday).ShouldBeNull();
    }

    [Fact]
    public async Task Dates_set_aside_are_still_reported_when_the_archive_has_not_changed()
    {
        await StoppedAsync(2, Build);
        await Pass().RunAsync(Root, CancellationToken.None);

        var skipped = await Pass().RunAsync(Root, CancellationToken.None);

        skipped.ShouldBe(new RootResult(Root, 0, 1, true, SetAsideProblem, SetAside: 1));
    }

    [Fact]
    public async Task Mbo_that_stopped_arriving_long_ago_no_longer_holds_bar_tapes_back()
    {
        var later = Tuesday.AddDays(SpoolPlan.MboPatience + 1);

        Days(Tuesday, later);
        Mbos(Monday, Tuesday);

        var result = await Pass().RunAsync(Root, CancellationToken.None);

        result.Spooled.ShouldBe(2);
        (await LedgerAsync()).Spooled(later)!.Ticks.ShouldBeFalse();
    }

    [Fact]
    public async Task Mbo_a_few_days_behind_is_still_waited_for()
    {
        var later = Tuesday.AddDays(SpoolPlan.MboPatience);

        Days(Tuesday, later);
        Mbos(Monday, Tuesday);

        var result = await Pass().RunAsync(Root, CancellationToken.None);

        result.Spooled.ShouldBe(1);
        (await LedgerAsync()).Spooled(later).ShouldBeNull();
    }

    [Fact]
    public async Task The_folder_shelf_leaves_no_temporary_file()
    {
        await shelf.PutBytesAsync(LedgerKey, [1, 2, 3], CancellationToken.None);
        await shelf.PutBytesAsync(LedgerKey, [4, 5], CancellationToken.None);

        (await shelf.ReadAsync(LedgerKey, CancellationToken.None)).ShouldBe([4, 5]);
        Directory.GetFiles(Path.Combine(home, "shelf"), "*.tmp", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    /// <summary>The archive and the catalog both hold these trade dates.</summary>
    private void Days(params DateOnly[] dates)
    {
        Archived(dates);
        engine.CatalogAsync(Root, Arg.Any<CancellationToken>()).Returns((Result<Catalog>)new Catalog(Dataset, Root, [],
            [.. dates.Select(d => new CatalogDay { TradeDate = d, Sessions = [], Contracts = [] })]));
    }

    private async Task StoppedAsync(int count, string build) =>
        await shelf.PutBytesAsync(LedgerKey, new SpoolLedger { Attempts = [new SpoolAttempt(Tuesday, count, build)] }.ToBytes(), CancellationToken.None);

    [Fact]
    public async Task A_folder_shelf_answers_null_for_a_key_it_lacks()
    {
        (await shelf.ReadAsync(TapeKey, CancellationToken.None)).ShouldBeNull();
    }
}
