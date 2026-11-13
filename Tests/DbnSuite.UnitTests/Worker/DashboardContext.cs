using Bunit;
using DbnSuite.Sync.Core;
using DbnSuite.Sync.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using static DbnSuite.UnitTests.TestData;

namespace DbnSuite.UnitTests.Worker;

/// <summary>What every dashboard page is rendered against: an empty store, and Databento with data to yesterday.</summary>
public abstract class DashboardContext : BunitContext
{
    protected static readonly DateOnly HistoryStart = Day(10, 1);

    private static readonly DateTimeOffset DatasetEnd = new(2026, 11, 1, 20, 0, 0, TimeSpan.Zero);

    protected DashboardContext()
    {
        var scopes = new ServiceCollection().AddSingleton(Databento).BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

        Index = new CorpusIndex(scopes, Store, Options, Clock, NullLogger<CorpusIndex>.Instance);
        Status = new SyncStatus(Options);

        Services.AddSingleton(Store);
        Services.AddSingleton(Databento);
        Services.AddSingleton<TimeProvider>(Clock);
        Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new DashboardOptions()));
        Services.AddSingleton(Index);
        Services.AddSingleton(Options);
        Services.AddSingleton(Status);
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        Pending();
        Passes();
        Stored();
        Store.ListSpendAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SpendEntry>>([]));
        Databento.GetAvailableEndsAsync(Dataset, Arg.Any<CancellationToken>())
            .Returns(Result(new Dictionary<string, DateTimeOffset> { [Mbo] = DatasetEnd, [Definition] = DatasetEnd }));
    }

    protected IArchiveStore Store { get; } = Substitute.For<IArchiveStore>();

    protected IDatabento Databento { get; } = Substitute.For<IDatabento>();

    protected FakeTimeProvider Clock { get; } = new(Now);

    protected SyncOptions Options { get; } = new()
    {
        Roots = Root,
        Schemas = $"{Mbo},{Definition}",
        HistoryStart = HistoryStart
    };

    protected CorpusIndex Index { get; }

    protected SyncStatus Status { get; }

    protected Task ScanAsync() => Index.RefreshAsync(CancellationToken.None);

    protected void Pending(params PendingJob[] jobs) =>
        Store.ListPendingAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<PendingJob>>(jobs));

    protected void Passes(params PassSummary[] passes) =>
        Store.ListPassesAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<PassSummary>>(passes));

    protected void Stored(params StoredDay[] days) =>
        Store.ListDaysAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<StoredDay>>(days));

    protected static PassSummary Pass(DateTimeOffset startedAt, TimeSpan took = default, int errors = 0,
        params Issue[] issues) =>
        new(startedAt, startedAt + took, 1, 2, 3, 4, 5, 6_000, 7, 0, errors, null, issues);

    private static Result<IReadOnlyDictionary<string, DateTimeOffset>> Result(Dictionary<string, DateTimeOffset> ends) => ends;
}
