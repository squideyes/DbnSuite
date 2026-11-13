using System.Net;
using System.Text.RegularExpressions;
using DbnSuite.IntegrationTests.Support;
using DbnSuite.Sync.Core;
using DbnSuite.Sync.Worker;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Shouldly;
using static DbnSuite.IntegrationTests.Support.Sample;

namespace DbnSuite.IntegrationTests;

/// <summary>
/// The pages as a browser first receives them, rendered by the real host from
/// what a real pass left in the store. The dashboard has no sign-in, so it must
/// show no secret and offer no way to spend; the password is Caddy's job.
/// </summary>
[Collection(ArchiveCollection.Name)]
public sealed partial class DashboardTests(RustFs s3)
{
    private const string Corpus = "/";
    private const string Activity = "/activity";
    private const string Status = "/status";
    private const string LogsAddress = "http://nas.local:5341";
    private const string NoSuchPage = "/admin";
    private const string NoSuchJob = "GLBX-00000000-NOSUCHJOB";
    private const string BlazorScript = "/_framework/blazor.web.js";
    private const string Negotiate = "/_blazor/negotiate?negotiateVersion=1";

    private static readonly string MboSeries = $"/series/{Mbo}/{Mnq}";

    private static string JobPage(string id) => $"/job/{id}";

    private static async Task<string> PageAsync(SyncScenario sync, string path)
    {
        using var client = sync.Host.CreateClient();

        var response = await client.GetAsync(path);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    [GeneratedRegex("""<link rel="stylesheet" href="(?<href>[^"]+)""")]
    private static partial Regex Stylesheet();

    [Fact]
    public async Task The_corpus_page_shows_what_the_store_holds_and_the_free_policy()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        await sync.FirstPassAsync();
        await sync.NextPassAsync();

        var page = await PageAsync(sync, Corpus);

        page.ShouldContain(new SpendPolicy().Describe());
        page.ShouldContain($"{TradingDays.Length} day files");
        page.ShouldContain(Dataset);
        page.ShouldContain($"series/{Mbo}/{Mnq}");
        page.ShouldContain("Caught up");
        page.ShouldContain("None in flight.");
    }

    [Fact]
    public async Task A_paid_policy_shows_what_the_ledger_says_was_spent()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.Paying(perJob: 5m, perDay: 10m, perMonth: 100m);
        sync.Gateway.Quote = _ => 4m;
        await sync.FirstPassAsync();

        var page = await PageAsync(sync, Corpus);

        page.ShouldContain("Quoted so far: $4.00 today, $4.00 this month");
        page.ShouldContain("up to $5 a job, $10 a day, $100 a month");
    }

    [Fact]
    public async Task A_job_in_flight_is_on_the_corpus_page_and_its_own_page_says_what_Databento_says()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.Gateway.NewJobState = JobStates.Processing;
        await sync.FirstPassAsync();
        var job = sync.Gateway.Jobs.Single();

        var corpus = await PageAsync(sync, Corpus);
        var own = await PageAsync(sync, JobPage(job.Id));

        corpus.ShouldContain(job.Id);
        own.ShouldContain("still preparing it");
        own.ShouldContain($"{Mnq} {Mbo}");
    }

    [Fact]
    public async Task The_series_page_shows_each_day_and_lists_a_gap()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        await sync.FirstPassAsync();
        await sync.NextPassAsync();
        await sync.Archive.DeleteAsync(ArchiveKeys.Data(Dataset, Mbo, Mnq, Monday));
        await sync.Corpus.RefreshAsync(CancellationToken.None);

        var page = await PageAsync(sync, MboSeries);

        page.ShouldContain("2026-10");
        page.ShouldContain("2026-11");
        page.ShouldContain($"{Monday:yyyy-MM-dd} · Owed");
        page.ShouldContain($"{Saturday:yyyy-MM-dd} · No data");
        page.ShouldContain($"{FirstDay:yyyy-MM-dd} · Stored");
        page.ShouldNotContain("None: every day is stored");
    }

    [Fact]
    public async Task A_series_not_kept_says_so()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        await sync.FirstPassAsync();

        var page = await PageAsync(sync, $"/series/{Mbo}/{Es}");

        page.ShouldContain($"{Es} {Mbo} is not kept.");
    }

    [Fact]
    public async Task The_activity_page_lists_a_problem_and_the_job_page_explains_it()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        await sync.FirstPassAsync();
        var job = sync.Gateway.Jobs.Single();
        sync.Gateway.Fail(Endpoints.Download, _ => Reply.Ok([0]));
        await sync.NextPassAsync();

        var activity = await PageAsync(sync, Activity);
        var own = await PageAsync(sync, JobPage(job.Id));

        activity.ShouldContain(Errors.Corrupt);
        activity.ShouldContain(job.Id);
        activity.ShouldContain(IssueGuide.For(Errors.Corrupt).Title);
        own.ShouldContain(IssueGuide.For(Errors.Corrupt).Meaning);
    }

    [Fact]
    public async Task The_job_page_links_to_the_logs_when_seq_is_configured()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.With($"{DashboardOptions.Section}:{nameof(DashboardOptions.LogsUrl)}", LogsAddress);
        sync.Gateway.NewJobState = JobStates.Processing;
        await sync.FirstPassAsync();

        var page = await PageAsync(sync, JobPage(sync.Gateway.Jobs.Single().Id));

        page.ShouldContain($"{LogsAddress}/#/events?filter=");
    }

    [Fact]
    public async Task A_job_nobody_has_heard_of_is_a_page_not_an_error()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        await sync.FirstPassAsync();

        var page = await PageAsync(sync, JobPage(NoSuchJob));

        page.ShouldContain("No record of this job");
    }

    [Fact]
    public async Task A_broken_store_shows_on_the_corpus_page_instead_of_taking_it_down()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        await sync.FirstPassAsync();
        await sync.Archive.PutAsync(ArchiveKeys.Job("GLBX-BROKEN"), "not json");
        await sync.Corpus.RefreshAsync(CancellationToken.None);

        var page = await PageAsync(sync, Corpus);

        page.ShouldContain("Last scan failed");
    }

    [Fact]
    public async Task A_path_with_no_page_is_a_404()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        await sync.FirstPassAsync();
        using var client = sync.Host.CreateClient();

        var response = await client.GetAsync(NoSuchPage);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_page_s_styles_and_script_are_served()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.Environment = Environments.Development;
        await sync.FirstPassAsync();
        using var client = sync.Host.CreateClient();

        var page = await PageAsync(sync, Corpus);

        foreach (Match sheet in Stylesheet().Matches(page))
            (await client.GetAsync("/" + sheet.Groups["href"].Value)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync(BlazorScript)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_interactive_circuit_can_negotiate()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        await sync.FirstPassAsync();
        using var client = sync.Host.CreateClient();

        var response = await client.PostAsync(Negotiate, null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task No_page_or_endpoint_shows_a_secret()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.Gateway.Fail(Endpoints.SubmitJob, Reply.Json(StatusCodes.Status400BadRequest, "{}"));
        await sync.FirstPassAsync();

        foreach (var path in new[] { Corpus, Activity, MboSeries, Status })
        {
            var page = await PageAsync(sync, path);

            page.ShouldNotContain(ApiKey);
            page.ShouldNotContain(RustFs.SecretKey);
            page.ShouldNotContain(RustFs.AccessKey);
        }
    }

    [Fact]
    public async Task Reading_every_page_quotes_and_submits_nothing()
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        sync.Gateway.NewJobState = JobStates.Processing;
        await sync.FirstPassAsync();
        var quotes = sync.Gateway.CallsTo(Endpoints.GetCost).Count;

        foreach (var path in new[] { Corpus, Activity, MboSeries, Status, JobPage(sync.Gateway.Jobs.Single().Id) })
            await PageAsync(sync, path);

        sync.Gateway.CallsTo(Endpoints.GetCost).Count.ShouldBe(quotes);
        sync.Gateway.CallsTo(Endpoints.SubmitJob).ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Status_cannot_be_written_to(string method)
    {
        await using var sync = await SyncScenario.CreateAsync(s3);
        await sync.FirstPassAsync();
        using var client = sync.Host.CreateClient();

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), Status));

        response.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
    }
}
