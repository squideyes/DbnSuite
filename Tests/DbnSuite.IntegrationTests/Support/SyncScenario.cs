using System.Globalization;
using DbnSync.Core;
using DbnSync.Data;
using DbnSync.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DbnSuite.IntegrationTests.Support;

/// <summary>
/// One worker against one fresh bucket and its own fake gateway. Set it up,
/// start it, then step it pass by pass: the clock moves only when told to.
/// </summary>
public sealed class SyncScenario : IAsyncDisposable
{
    private const string OtlpEndpoint = "OTEL_EXPORTER_OTLP_ENDPOINT";

    private static readonly TimeSpan PassTimeout = TimeSpan.FromMinutes(1);

    private readonly Dictionary<string, string?> settings;
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "dbnsuite-it", Guid.NewGuid().ToString("N"));

    private Action<IServiceCollection> services = _ => { };

    private SyncScenario(RustFs s3, FakeDatabento gateway, TestClock clock, string prefix)
    {
        Gateway = gateway;
        Clock = clock;
        Archive = new ArchiveProbe(s3, $"it-{Guid.NewGuid():N}", prefix);

        settings = new(StringComparer.Ordinal)
        {
            [$"{DatabentoOptions.Section}:{nameof(DatabentoOptions.ApiKey)}"] = Sample.ApiKey,

            // Blanked so a real key in this machine's environment can never reach a worker under test.
            [DatabentoOptions.KeyVariable] = "",
            [OtlpEndpoint] = "",

            [$"{DatabentoOptions.Section}:{nameof(DatabentoOptions.Gateway)}"] = gateway.Address.ToString(),
            [$"{StoreOptions.Section}:{nameof(StoreOptions.ServiceUrl)}"] = s3.Endpoint,
            [$"{StoreOptions.Section}:{nameof(StoreOptions.Bucket)}"] = Archive.Bucket,
            [$"{StoreOptions.Section}:{nameof(StoreOptions.Prefix)}"] = prefix,
            [$"{StoreOptions.Section}:{nameof(StoreOptions.AccessKey)}"] = RustFs.AccessKey,
            [$"{StoreOptions.Section}:{nameof(StoreOptions.SecretKey)}"] = RustFs.SecretKey,
            [Sync(nameof(SyncOptions.Roots))] = Sample.Mnq,
            [Sync(nameof(SyncOptions.Schemas))] = Sample.Mbo,
            [Sync(nameof(SyncOptions.HistoryStart))] = Text(Sample.FirstDay),
            [Sync(nameof(SyncOptions.ScratchPath))] = scratch
        };
    }

    public FakeDatabento Gateway { get; }

    public TestClock Clock { get; }

    public ArchiveProbe Archive { get; }

    public LogSink Logs { get; private set; } = new();

    public WorkerHost Host { get; private set; } = null!;

    public string Environment { get; set; } = Environments.Production;

    public string ScratchPath => scratch;

    /// <summary>How long the worker chose to wait after the pass just finished.</summary>
    public TimeSpan Delay { get; private set; }

    public SyncStatus Status => Host.Services.GetRequiredService<SyncStatus>();

    public CorpusIndex Corpus => Host.Services.GetRequiredService<CorpusIndex>();

    public static async Task<SyncScenario> CreateAsync(RustFs s3, string prefix = "")
    {
        var clock = new TestClock(Sample.Now);

        return new SyncScenario(s3, await FakeDatabento.StartAsync(clock), clock, prefix);
    }

    public static string Sync(string setting) => $"{SyncOptions.Section}:{setting}";

    public static string Spend(string setting) => Sync($"{nameof(SyncOptions.Spend)}:{setting}");

    public SyncScenario With(string key, string? value)
    {
        settings[key] = value;

        return this;
    }

    public SyncScenario Keeping(string roots, string schemas) =>
        With(Sync(nameof(SyncOptions.Roots)), roots).With(Sync(nameof(SyncOptions.Schemas)), schemas);

    public SyncScenario From(DateOnly historyStart) => With(Sync(nameof(SyncOptions.HistoryStart)), Text(historyStart));

    public SyncScenario ReachingBackTo(DateOnly day) => With(Sync(nameof(SyncOptions.ReachBackTo)), Text(day));

    public SyncScenario MaxPending(int jobs) =>
        With(Sync(nameof(SyncOptions.MaxPendingJobs)), jobs.ToString(CultureInfo.InvariantCulture));

    public SyncScenario Paying(decimal perJob, decimal perDay, decimal perMonth) =>
        With(Spend(nameof(SpendPolicy.AllowPaidJobs)), bool.TrueString)
            .With(Spend(nameof(SpendPolicy.MaxPerJob)), Text(perJob))
            .With(Spend(nameof(SpendPolicy.MaxPerDay)), Text(perDay))
            .With(Spend(nameof(SpendPolicy.MaxPerMonth)), Text(perMonth));

    /// <summary>Changes the worker's services, after Program has registered its own.</summary>
    public SyncScenario Replacing(Action<IServiceCollection> change)
    {
        services += change;

        return this;
    }

    /// <summary>Builds and starts the host, without waiting for a pass.</summary>
    public void Start()
    {
        var sink = Logs = new LogSink();

        Host = new WorkerHost(Environment, settings, s =>
        {
            s.AddSingleton<TimeProvider>(Clock);
            s.AddSingleton<ILoggerProvider>(sink);
            services(s);
        });

        // The server starts on first use, and the worker with it.
        _ = Host.Services;
    }

    /// <summary>Starts the worker and waits out its first pass, which always runs at once.</summary>
    public async Task<PassSummary> FirstPassAsync()
    {
        Start();

        return await PassAsync();
    }

    /// <summary>Moves the clock to the next pass and waits it out.</summary>
    public Task<PassSummary> NextPassAsync() => PassAfterAsync(Delay);

    /// <summary>Moves the clock on by at least the worker's delay and waits out the pass that wakes.</summary>
    public Task<PassSummary> PassAfterAsync(TimeSpan elapsed)
    {
        Clock.Advance(elapsed);

        return PassAsync();
    }

    /// <summary>Stops the worker as a container stop would, leaving the archive as it is.</summary>
    public async Task StopAsync()
    {
        await Host.DisposeAsync();

        Clock.ForgetDelays();
    }

    /// <summary>A new process over the same archive and gateway: only the bucket and the scratch volume survive.</summary>
    public async Task<PassSummary> RestartAsync()
    {
        await StopAsync();

        return await FirstPassAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Host is not null)
            await Host.DisposeAsync();

        await Gateway.DisposeAsync();

        Archive.Dispose();

        if (Directory.Exists(scratch))
            Directory.Delete(scratch, recursive: true);
    }

    private async Task<PassSummary> PassAsync()
    {
        using var timeout = new CancellationTokenSource(PassTimeout);

        Delay = await Clock.PassDelayAsync(timeout.Token);

        return Status.Snapshot.LastPass!;
    }

    private static string Text(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Text(decimal amount) => amount.ToString(CultureInfo.InvariantCulture);
}
