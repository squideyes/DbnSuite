using DbnSuite.Sync.Core;
using DbnSuite.Sync.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using static DbnSuite.UnitTests.TestData;

namespace DbnSuite.UnitTests.Worker;

public sealed class CorpusIndexTests
{
    private const long FileBytes = 1_000;
    private const string ScanFailure = "the store is down";

    private static readonly DateOnly HistoryStart = Day(10, 1);
    private static readonly DateTimeOffset DatasetEnd = new(2026, 10, 30, 20, 0, 0, TimeSpan.Zero);
    private static readonly DayRange Range = new(Day(10, 5), Day(10, 6));

    private readonly IArchiveStore store = Substitute.For<IArchiveStore>();
    private readonly IDatabento databento = Substitute.For<IDatabento>();
    private readonly FakeTimeProvider clock = new(Now);
    private readonly SyncOptions options = new() { Roots = $"{Root},{OtherRoot}", Schemas = Mbo, HistoryStart = HistoryStart };
    private readonly CorpusIndex index;

    public CorpusIndexTests()
    {
        var scopes = new ServiceCollection().AddSingleton(databento).BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

        index = new CorpusIndex(scopes, store, options, clock, NullLogger<CorpusIndex>.Instance);

        store.ListPendingAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<PendingJob>>([]));
        store.ListDaysAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<StoredDay>>([]));
        databento.GetAvailableEndsAsync(Dataset, Arg.Any<CancellationToken>()).Returns(Ends(DatasetEnd));
    }

    private static Result<IReadOnlyDictionary<string, DateTimeOffset>> Ends(DateTimeOffset end) =>
        new Dictionary<string, DateTimeOffset> { [Mbo] = end };

    private Task ScanAsync() => index.RefreshAsync(CancellationToken.None);

    [Fact]
    public void There_is_no_snapshot_until_the_first_scan()
    {
        index.Snapshot.ShouldBeNull();
        index.Scanning.ShouldBeFalse();
    }

    [Fact]
    public async Task A_scan_holds_every_series_and_when_it_ran()
    {
        store.ListDaysAsync(Mbo, Root, Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<StoredDay>>(
            [new StoredDay(Day(10, 1), FileBytes, Empty: false), new StoredDay(Day(10, 2), 0, Empty: true)]));

        await ScanAsync();

        var snapshot = index.Snapshot.ShouldNotBeNull();
        snapshot.ScannedAt.ShouldBe(Now);
        snapshot.Failure.ShouldBeNull();
        snapshot.Series.Select(s => s.Root).ShouldBe([Root, OtherRoot]);
        snapshot.TotalBytes.ShouldBe(FileBytes);
        snapshot.Count(DayState.Held).ShouldBe(1);
        snapshot.Count(DayState.Empty).ShouldBe(1);
    }

    [Fact]
    public async Task The_series_end_where_the_dataset_does()
    {
        await ScanAsync();

        index.Snapshot!.Series[0].Span!.Value.Upto.ShouldBe(Entitlement.LastCompleteDay(DatasetEnd));
    }

    [Fact]
    public async Task With_the_dataset_range_unavailable_the_series_end_yesterday()
    {
        databento.GetAvailableEndsAsync(Dataset, Arg.Any<CancellationToken>())
            .Returns(new Error(Errors.Unreachable, ScanFailure));

        await ScanAsync();

        index.Snapshot!.Series[0].Span!.Value.Upto.ShouldBe(Today.AddDays(-1));
        index.Snapshot.Failure.ShouldBeNull();
    }

    [Fact]
    public async Task Days_of_a_pending_job_show_as_pending_on_its_own_series_only()
    {
        store.ListPendingAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<PendingJob>>(
            [new PendingJob(JobId, Root, Mbo, Range, Now)]));

        await ScanAsync();

        var series = index.Snapshot!.Series;
        series.Single(s => s.Root == Root).Count(DayState.Pending).ShouldBe(Range.Days);
        series.Single(s => s.Root == OtherRoot).Count(DayState.Pending).ShouldBe(0);
        index.Snapshot.Pending.ShouldHaveSingleItem().Id.ShouldBe(JobId);
    }

    [Fact]
    public async Task A_failed_first_scan_still_gives_the_pages_a_snapshot_to_show_it_on()
    {
        store.ListPendingAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException(ScanFailure));

        await ScanAsync();

        var snapshot = index.Snapshot.ShouldNotBeNull();
        snapshot.Failure.ShouldBe($"{nameof(InvalidOperationException)}: {ScanFailure}");
        snapshot.Series.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failed_scan_keeps_the_last_good_series()
    {
        await ScanAsync();
        store.ListPendingAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException(ScanFailure));

        await ScanAsync();

        index.Snapshot!.Series.Count.ShouldBe(2);
        index.Snapshot.Failure.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_good_scan_clears_the_failure()
    {
        store.ListPendingAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException(ScanFailure));
        await ScanAsync();
        store.ListPendingAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<PendingJob>>([]));

        await ScanAsync();

        index.Snapshot!.Failure.ShouldBeNull();
    }

    [Fact]
    public async Task A_cancelled_scan_is_not_recorded_as_a_failure()
    {
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        store.ListPendingAsync(cancel.Token).ThrowsAsync(new OperationCanceledException(cancel.Token));

        await Should.ThrowAsync<OperationCanceledException>(() => index.RefreshAsync(cancel.Token));

        index.Snapshot.ShouldBeNull();
        index.Scanning.ShouldBeFalse();
    }

    [Fact]
    public async Task A_second_refresh_waits_for_the_scan_already_running_and_does_not_start_another()
    {
        var slow = new TaskCompletionSource<IReadOnlyList<PendingJob>>();
        store.ListPendingAsync(Arg.Any<CancellationToken>()).Returns(slow.Task);

        var first = ScanAsync();
        var scanning = index.Scanning;
        var second = ScanAsync();
        var secondWaited = !second.IsCompleted;
        slow.SetResult([]);
        await Task.WhenAll(first, second);

        scanning.ShouldBeTrue();
        secondWaited.ShouldBeTrue();
        await store.Received(1).ListPendingAsync(Arg.Any<CancellationToken>());
        index.Scanning.ShouldBeFalse();
    }

    [Fact]
    public async Task A_rescan_waits_for_the_scan_already_running_and_then_sees_what_changed_since()
    {
        var slow = new TaskCompletionSource<IReadOnlyList<PendingJob>>();
        store.ListPendingAsync(Arg.Any<CancellationToken>()).Returns(slow.Task,
            Task.FromResult<IReadOnlyList<PendingJob>>([new PendingJob(JobId, Root, Mbo, Range, Now)]));

        var startup = ScanAsync();
        var rescan = index.RescanAsync(CancellationToken.None);
        var rescanWaited = !rescan.IsCompleted;
        slow.SetResult([]);
        await Task.WhenAll(startup, rescan);

        rescanWaited.ShouldBeTrue();
        await store.Received(2).ListPendingAsync(Arg.Any<CancellationToken>());
        index.Snapshot!.Pending.ShouldHaveSingleItem().Id.ShouldBe(JobId);
        index.Scanning.ShouldBeFalse();
    }

    [Fact]
    public async Task A_rescan_with_nothing_running_scans_at_once()
    {
        await index.RescanAsync(CancellationToken.None);

        index.Snapshot.ShouldNotBeNull();
        await store.Received(1).ListPendingAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Listeners_hear_a_scan_start_and_finish()
    {
        var heard = new List<bool>();
        index.Changed += () => heard.Add(index.Scanning);

        await ScanAsync();

        heard.ShouldBe([true, false]);
    }
}
