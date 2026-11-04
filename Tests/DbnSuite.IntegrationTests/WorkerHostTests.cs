using System.Net;
using System.Text.Json;
using DbnSuite.IntegrationTests.Support;
using DbnSync.Core;
using DbnSync.Data;
using DbnSync.Worker;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;
using static DbnSuite.IntegrationTests.Support.Sample;

namespace DbnSuite.IntegrationTests;

/// <summary>Program itself: what it refuses to start on, and what it answers once started.</summary>
[Collection(ArchiveCollection.Name)]
public sealed class WorkerHostTests(RustFs s3)
{
    private const string Dashboard = "/";
    private const string Health = "/health";
    private const string Alive = "/alive";
    private const string Status = "/status";
    private const string Healthy = "Healthy";
    private const string KeysFolder = ".keys";
    private const string SeqConnection = "ConnectionStrings:seq";
    private const string SeqIngest = "events/raw";

    private static readonly string ApiKeySetting = $"{DatabentoOptions.Section}:{nameof(DatabentoOptions.ApiKey)}";
    private static readonly string LogsUrlSetting = $"{DashboardOptions.Section}:{nameof(DashboardOptions.LogsUrl)}";

    private static readonly TimeSpan GraceToReachGateway = TimeSpan.FromSeconds(30);

    private static string Store(string setting) => $"{StoreOptions.Section}:{setting}";

    [Fact]
    public async Task In_production_health_answers_and_nothing_else_of_the_template_is_mapped()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        await sync.FirstPassAsync();
        using var client = sync.Host.CreateClient();

        var health = await client.GetAsync(Health);
        var alive = await client.GetAsync(Alive);

        health.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await health.Content.ReadAsStringAsync()).ShouldBe(Healthy);
        alive.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task In_development_health_and_liveness_both_answer()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.Environment = Environments.Development;
        await sync.FirstPassAsync();
        using var client = sync.Host.CreateClient();

        (await client.GetAsync(Health)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync(Alive)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Status_says_a_pass_is_running_while_it_is()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        var submitting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sync.Gateway.OnSubmit = async _ =>
        {
            submitting.TrySetResult();

            await release.Task;
        };

        sync.Start();
        await submitting.Task.WaitAsync(GraceToReachGateway);
        using var client = sync.Host.CreateClient();
        using var during = JsonDocument.Parse(await client.GetStringAsync(Status));
        release.SetResult();

        during.RootElement.GetProperty("running").GetBoolean().ShouldBeTrue();
        during.RootElement.GetProperty("nextPassAt").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Status_reports_the_last_pass_the_next_one_and_the_policy()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        await sync.FirstPassAsync();
        using var client = sync.Host.CreateClient();

        using var status = JsonDocument.Parse(await client.GetStringAsync(Status));

        var root = status.RootElement;
        root.GetProperty("running").GetBoolean().ShouldBeFalse();
        root.GetProperty("nextPassAt").GetDateTimeOffset().ShouldBe(Now + TestClock.BusyInterval);
        root.GetProperty("lastPass").GetProperty("jobsSubmitted").GetInt32().ShouldBe(1);
        root.GetProperty("roots").EnumerateArray().Select(r => r.GetString()).ShouldBe([Mnq]);
        root.GetProperty("schemas").EnumerateArray().Select(r => r.GetString()).ShouldBe([Mbo]);
        root.GetProperty("spendPolicy").GetString().ShouldBe(new SpendPolicy().Describe());
    }

    [Fact]
    public async Task The_key_falls_back_to_the_environment_variable_the_compose_file_sets()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.With(ApiKeySetting, "").With(DatabentoOptions.KeyVariable, ApiKey);

        var pass = await sync.FirstPassAsync();

        pass.JobsSubmitted.ShouldBe(1);
    }

    [Fact]
    public async Task A_worker_with_no_key_does_not_start()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.With(ApiKeySetting, "");

        var refused = Should.Throw<OptionsValidationException>(sync.Start);

        refused.Message.ShouldContain(DatabentoOptions.KeyVariable);
    }

    [Theory]
    [InlineData(nameof(StoreOptions.ServiceUrl), "minio:9000", "must be an absolute URL")]
    [InlineData(nameof(StoreOptions.Bucket), " ", "Bucket is required")]
    [InlineData(nameof(StoreOptions.AccessKey), "", "AccessKey and Store:SecretKey are required")]
    [InlineData(nameof(StoreOptions.SecretKey), "", "AccessKey and Store:SecretKey are required")]
    public async Task A_store_it_could_not_reach_stops_the_worker_at_startup(string setting, string value, string reason)
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.With(Store(setting), value);

        var refused = Should.Throw<OptionsValidationException>(sync.Start);

        refused.Message.ShouldContain(reason);
    }

    [Theory]
    [InlineData(nameof(SyncOptions.Dataset), "XNAS.ITCH", "has no profile")]
    [InlineData(nameof(SyncOptions.Roots), "", "names no roots")]
    [InlineData(nameof(SyncOptions.Schemas), "tape", "'tape' is not a schema")]
    [InlineData(nameof(SyncOptions.Interval), "00:01:00", "at least 5 minutes")]
    [InlineData(nameof(SyncOptions.MaxPendingJobs), "0", "MaxPendingJobs must be 1-500")]
    public async Task A_sync_setting_that_could_not_work_stops_the_worker_at_startup(string setting, string value, string reason)
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.With(SyncScenario.Sync(setting), value);

        var refused = Should.Throw<OptionsValidationException>(sync.Start);

        refused.Message.ShouldContain(reason);
        sync.Gateway.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("nas.local:5341")]
    [InlineData("ftp://nas.local")]
    public async Task A_logs_address_that_is_not_http_stops_the_worker_at_startup(string address)
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.With(LogsUrlSetting, address);

        var refused = Should.Throw<OptionsValidationException>(sync.Start);

        refused.Message.ShouldContain("LogsUrl");
    }

    [Fact]
    public async Task Antiforgery_keys_stay_on_the_scratch_volume_across_a_restart()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        await sync.FirstPassAsync();
        using (var client = sync.Host.CreateClient())
            await client.GetAsync(Dashboard);
        var keys = Directory.GetFiles(Path.Combine(sync.ScratchPath, KeysFolder));

        await sync.RestartAsync();
        using (var client = sync.Host.CreateClient())
            await client.GetAsync(Dashboard);

        keys.ShouldNotBeEmpty();
        Directory.GetFiles(Path.Combine(sync.ScratchPath, KeysFolder)).ShouldBe(keys, ignoreOrder: true);
    }

    [Fact]
    public async Task Logs_reach_Seq_when_it_is_configured()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.With(SeqConnection, sync.Gateway.Address.ToString());

        await sync.FirstPassAsync();
        await sync.StopAsync();

        sync.Gateway.Calls.ShouldContain(c => c.Endpoint.Contains(SeqIngest));
    }

    [Fact]
    public async Task The_key_and_the_store_secret_are_never_logged()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.Gateway.Fail(Endpoints.SubmitJob, Reply.Json(StatusCodes.Status400BadRequest, "{}"));

        await sync.FirstPassAsync();

        sync.Logs.Lines.ShouldNotBeEmpty();
        sync.Logs.Lines.ShouldAllBe(l => !l.Text.Contains(ApiKey) && !l.Text.Contains(RustFs.SecretKey));
    }
}
