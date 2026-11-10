using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using DbnSync.Core;
using DbnSync.Data;
using Microsoft.Extensions.Options;
using Shouldly;
using static DbnSync.UnitTests.TestData;

namespace DbnSync.UnitTests.Data;

public sealed class DatabentoClientTests : IDisposable
{
    private const string GetCost = "metadata.get_cost";
    private const string SubmitJob = "batch.submit_job";
    private const string ListJobs = "batch.list_jobs";
    private const string DatasetRange = "metadata.get_dataset_range";
    private const string ListFiles = "batch.list_files";
    private const string JobDetails = "batch.get_job_details";
    private const string Received = "2026-10-28T09:23:17.519708000Z";
    private const string Midnight = "T00:00:00.000000000Z";
    private const string Noon = "T12:00:00.000000000Z";
    private const string Expires = "2026-11-27T14:48:15.710116000Z";
    private const string OneSymbol = "\"MNQ.n.0\"";
    private const string Offline = "no route to host";
    private const string DownloadUrl = "https://api.databento.com/v0/batch/download/X/file";
    private const string OwnGateway = "http://gateway.test";
    private const string ServerErrorWords = "Databento had a server error";

    /// <summary>Databento's answer, verbatim, for MCL.FUT across 2020, before MCL was listed.</summary>
    private const string NoSymbolsBody = """
        {"detail":{"case":"symbology_invalid_request","message":"None of the symbols could be resolved","status_code":422,"docs":"https://databento.com/docs/api-reference-historical/basics/symbology","payload":null}}
        """;

    /// <summary>Databento's answer, verbatim, for MCL ohlcv-1s from April to July 2021.</summary>
    private const string NoDataBody = """
        {"detail":{"case":"data_no_data_found_for_request","message":"No data was found for the request you submitted.","status_code":422,"docs":"https://databento.com/docs/api-reference-historical/batch/batch-submit-job","payload":null}}
        """;

    private static readonly BatchRequest Request = new(Dataset, Mbo, [$"{Root}.n.0"], "continuous",
        new DayRange(Day(10, 28), Day(10, 28)));

    private static readonly IReadOnlySet<string> NoneKnown = new HashSet<string>();

    private readonly StubHandler handler = new();
    private readonly string folder = Path.Combine(Path.GetTempPath(), $"dbnsync-tests-{Guid.NewGuid():N}");

    public DatabentoClientTests() => Directory.CreateDirectory(folder);

    public void Dispose() => Directory.Delete(folder, recursive: true);

    [Fact]
    public async Task Nothing_is_submitted_when_the_quote_is_above_zero()
    {
        handler.Answer(GetCost, "0.000005");

        var result = await Client().SubmitWithinAsync(Request, 0m, CancellationToken.None);

        result.Error!.Code.ShouldBe(Errors.OverLimit);
        handler.Paths.ShouldNotContain(p => p.Contains(SubmitJob));
    }

    [Fact]
    public async Task A_job_quoted_within_the_limit_is_submitted()
    {
        handler.Answer(GetCost, "2.5");
        handler.Answer(SubmitJob, $$"""{"id":"{{JobId}}","state":"queued"}""");

        var result = await Client().SubmitWithinAsync(Request, 2.5m, CancellationToken.None);

        result.Value.ShouldBe(JobId);
    }

    [Fact]
    public async Task A_negative_limit_counts_as_zero()
    {
        handler.Answer(GetCost, "0.000005");

        var result = await Client().SubmitWithinAsync(Request, -10m, CancellationToken.None);

        result.Error!.Code.ShouldBe(Errors.OverLimit);
        handler.Paths.ShouldNotContain(p => p.Contains(SubmitJob));
    }

    [Fact]
    public async Task A_negative_quote_is_malformed_and_nothing_is_submitted()
    {
        handler.Answer(GetCost, "-1");

        var result = await Client().SubmitWithinAsync(Request, 5m, CancellationToken.None);

        result.Error!.Code.ShouldBe(Errors.Malformed);
        handler.Paths.ShouldNotContain(p => p.Contains(SubmitJob));
    }

    [Fact]
    public async Task A_free_job_is_submitted_split_daily_and_compressed()
    {
        handler.Answer(GetCost, "0.0");
        handler.Answer(SubmitJob, $$"""{"id":"{{JobId}}","state":"queued"}""");

        var result = await Client().SubmitWithinAsync(Request, 0m, CancellationToken.None);

        result.Value.ShouldBe(JobId);
        handler.Bodies[SubmitJob].ShouldContain("split_duration=day");
        handler.Bodies[SubmitJob].ShouldContain("compression=zstd");
        handler.Bodies[SubmitJob].ShouldContain("end=2026-10-29");
    }

    [Fact]
    public async Task Requests_carry_the_key_as_the_basic_user_name()
    {
        handler.Answer(GetCost, "1");

        await Client().SubmitWithinAsync(Request, 0m, CancellationToken.None);

        handler.Authorization.ShouldBe(Convert.ToBase64String(Encoding.ASCII.GetBytes($"{ApiKey}:")));
    }

    [Fact]
    public async Task A_rejection_is_an_error_not_an_exception()
    {
        handler.Answer(GetCost, """{"detail":"symbology_invalid_request"}""", HttpStatusCode.UnprocessableEntity);

        var result = await Client().SubmitWithinAsync(Request, 0m, CancellationToken.None);

        result.Error!.Code.ShouldBe(Errors.Rejected);
        result.Error.Message.ShouldContain("422");
    }

    [Fact]
    public async Task A_request_none_of_whose_symbols_existed_says_so()
    {
        handler.Answer(GetCost, NoSymbolsBody, HttpStatusCode.UnprocessableEntity);

        var result = await Client().SubmitWithinAsync(Request, 0m, CancellationToken.None);

        result.Error!.Code.ShouldBe(Errors.NoSymbols);
        handler.Paths.ShouldNotContain(p => p.Contains(SubmitJob));
    }

    [Fact]
    public async Task A_request_that_found_no_records_says_so()
    {
        handler.Answer(GetCost, NoDataBody, HttpStatusCode.UnprocessableEntity);

        var result = await Client().SubmitWithinAsync(Request, 0m, CancellationToken.None);

        result.Error!.Code.ShouldBe(Errors.NoData);
        handler.Paths.ShouldNotContain(p => p.Contains(SubmitJob));
    }

    [Fact]
    public async Task Dataset_range_reads_nanosecond_ends_per_schema()
    {
        handler.Answer(DatasetRange, """
            {"start":"2010-06-06T00:00:00.000000000Z","end":"2026-11-02T04:20:00.000000000Z",
             "schema":{"mbo":{"start":"2017-05-21T00:00:00.000000000Z","end":"2026-11-01T20:24:28.125760000Z"}}}
            """);

        var ends = await Client().GetAvailableEndsAsync(Dataset, CancellationToken.None);

        ends.Value[Mbo].ShouldBe(new DateTimeOffset(2026, 11, 1, 20, 24, 28, TimeSpan.Zero).AddTicks(1257600));
    }

    [Theory]
    [InlineData("done", JobState.Done)]
    [InlineData("processing", JobState.Working)]
    [InlineData("expired", JobState.Gone)]
    public async Task Job_states_map_to_what_the_runner_needs(string state, JobState expected)
    {
        handler.Answer(ListJobs, $$"""[{"id":"{{JobId}}","state":"{{state}}"}]""");

        var result = await Client().GetStateAsync(JobId, Now, CancellationToken.None);

        result.Value.ShouldBe(expected);
    }

    [Fact]
    public async Task A_job_missing_from_the_listing_is_gone_once_Databento_does_not_know_it()
    {
        handler.Answer(ListJobs, $$"""[{"id":"{{OtherJobId}}","state":"done"}]""");
        handler.Answer(JobDetails, "{}", HttpStatusCode.NotFound);

        var result = await Client().GetStateAsync(JobId, Now, CancellationToken.None);

        result.Value.ShouldBe(JobState.Gone);
    }

    [Theory]
    [InlineData("processing", JobState.Working)]
    [InlineData("done", JobState.Done)]
    [InlineData("expired", JobState.Gone)]
    public async Task A_job_missing_from_the_listing_keeps_the_state_Databento_gives_for_it(string state, JobState expected)
    {
        handler.Answer(ListJobs, "[]");
        handler.Answer(JobDetails, $$"""{"id":"{{JobId}}","state":"{{state}}"}""");

        var result = await Client().GetStateAsync(JobId, Now, CancellationToken.None);

        result.Value.ShouldBe(expected);
    }

    [Fact]
    public async Task A_job_missing_from_the_listing_is_not_given_up_on_a_failed_answer()
    {
        handler.Answer(ListJobs, "[]");
        handler.Answer(JobDetails, "{}", HttpStatusCode.InternalServerError);

        var result = await Client().GetStateAsync(JobId, Now, CancellationToken.None);

        result.Error!.Code.ShouldBe(Errors.ServerError);
    }

    [Fact]
    public async Task A_job_missing_from_the_listing_with_a_state_unknown_here_is_not_given_up()
    {
        handler.Answer(ListJobs, "[]");
        handler.Answer(JobDetails, """{"state":"something new"}""");

        var result = await Client().GetStateAsync(JobId, Now, CancellationToken.None);

        result.Error!.Code.ShouldBe(Errors.Malformed);
    }

    [Fact]
    public async Task A_download_matching_size_and_hash_lands_in_place()
    {
        var bytes = Encoding.ASCII.GetBytes("dbn bytes");
        var file = Remote(bytes, SHA256.HashData(bytes));
        var path = Path.Combine(folder, file.Name);
        handler.Answer(file.Name, bytes);

        var result = await Client().DownloadAsync(file, path, CancellationToken.None);

        result.Value.ShouldBe(bytes.Length);
        System.IO.File.ReadAllBytes(path).ShouldBe(bytes);
    }

    [Fact]
    public async Task A_download_with_the_wrong_hash_leaves_no_file()
    {
        var bytes = Encoding.ASCII.GetBytes("dbn bytes");
        var file = Remote(bytes, SHA256.HashData([0]));
        var path = Path.Combine(folder, file.Name);
        handler.Answer(file.Name, bytes);

        var result = await Client().DownloadAsync(file, path, CancellationToken.None);

        result.Error!.Code.ShouldBe(Errors.Corrupt);
        Directory.GetFiles(folder).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task A_server_error_is_not_a_refusal_since_the_job_may_have_been_taken(HttpStatusCode status)
    {
        handler.Answer(GetCost, "0");
        handler.Answer(SubmitJob, "{}", status);

        var result = await Client().SubmitWithinAsync(Request, 0m, CancellationToken.None);

        result.Error!.Code.ShouldBe(Errors.ServerError);
        result.Error.Message.ShouldContain(ServerErrorWords);
        result.Error.Message.ShouldContain(((int)status).ToString(CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task A_client_error_is_a_definite_refusal(HttpStatusCode status)
    {
        handler.Answer(GetCost, "0");
        handler.Answer(SubmitJob, "{}", status);

        var result = await Client().SubmitWithinAsync(Request, 0m, CancellationToken.None);

        result.Error!.Code.ShouldBe(Errors.Rejected);
    }

    [Fact]
    public async Task A_long_refusal_is_cut_to_a_readable_length()
    {
        handler.Answer(GetCost, new string('x', 2_000), HttpStatusCode.BadRequest);

        var result = await Client().QuoteAsync(Request, CancellationToken.None);

        result.Error!.Message.Length.ShouldBeLessThan(600);
    }

    [Fact]
    public async Task A_network_failure_is_unreachable_not_an_exception()
    {
        handler.Throw(GetCost, new HttpRequestException(Offline));

        var result = await Client().QuoteAsync(Request, CancellationToken.None);

        result.Error!.Code.ShouldBe(Errors.Unreachable);
    }

    [Fact]
    public async Task A_timeout_is_unreachable_not_an_exception()
    {
        handler.Throw(GetCost, new TaskCanceledException(Offline));

        var result = await Client().QuoteAsync(Request, CancellationToken.None);

        result.Error!.Code.ShouldBe(Errors.Unreachable);
    }

    [Fact]
    public async Task Cancelling_a_request_cancels_rather_than_reports_it_unreachable()
    {
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        handler.Throw(GetCost, new TaskCanceledException(Offline));

        await Should.ThrowAsync<TaskCanceledException>(() => Client().QuoteAsync(Request, cancel.Token));
    }

    [Fact]
    public async Task An_answer_that_is_not_json_is_malformed()
    {
        handler.Answer(GetCost, "<html>");

        var result = await Client().QuoteAsync(Request, CancellationToken.None);

        result.Error!.Code.ShouldBe(Errors.Malformed);
    }

    [Theory]
    [InlineData("""{"state":"queued"}""")]
    [InlineData("""{"id":""}""")]
    [InlineData("""{"id":5}""")]
    [InlineData("[]")]
    public async Task An_acceptance_without_a_job_id_is_malformed(string body)
    {
        handler.Answer(GetCost, "0");
        handler.Answer(SubmitJob, body);

        var result = await Client().SubmitWithinAsync(Request, 0m, CancellationToken.None);

        result.Error!.Code.ShouldBe(Errors.Malformed);
    }

    [Fact]
    public async Task A_failed_submission_says_so()
    {
        handler.Answer(GetCost, "0");
        handler.Throw(SubmitJob, new HttpRequestException(Offline));

        var result = await Client().SubmitWithinAsync(Request, 0m, CancellationToken.None);

        result.Error!.Code.ShouldBe(Errors.Unreachable);
    }

    [Fact]
    public async Task A_failed_range_request_says_so()
    {
        handler.Answer(DatasetRange, "{}", HttpStatusCode.Unauthorized);

        var result = await Client().GetAvailableEndsAsync(Dataset, CancellationToken.None);

        result.Error!.Code.ShouldBe(Errors.Rejected);
    }

    [Theory]
    [InlineData("""{"end":"2026-11-02T04:20:00.000000000Z"}""")]
    [InlineData("""{"schema":"mbo"}""")]
    [InlineData("""{"schema":{"mbo":{"end":20261102}}}""")]
    public async Task A_range_of_the_wrong_shape_is_malformed(string body)
    {
        handler.Answer(DatasetRange, body);

        var result = await Client().GetAvailableEndsAsync(Dataset, CancellationToken.None);

        result.Error!.Code.ShouldBe(Errors.Malformed);
    }

    [Fact]
    public async Task A_schema_without_a_readable_end_is_left_out()
    {
        handler.Answer(DatasetRange, """
            {"schema":{"mbo":{"start":"2017-05-21T00:00:00Z"},"trades":{"end":"soon"},"status":{"end":""},
             "definition":{"end":"2026-11-01T20:00:00Z"}}}
            """);

        var ends = await Client().GetAvailableEndsAsync(Dataset, CancellationToken.None);

        ends.Value.Keys.ShouldBe([Definition]);
    }

    [Theory]
    [InlineData("2026-11-01T20:24:28.1234567891Z")]
    [InlineData("2026-11-01T20:24:28.1234567891+00:00")]
    [InlineData("2026-11-01T20:24:28.1234567891")]
    public async Task An_end_keeps_what_a_timestamp_can_hold_with_or_without_a_zone(string end)
    {
        handler.Answer(DatasetRange, $$"""{"schema":{"mbo":{"end":"{{end}}"} } }""");

        var ends = await Client().GetAvailableEndsAsync(Dataset, CancellationToken.None);

        ends.Value[Mbo].ShouldBe(new DateTimeOffset(2026, 11, 1, 20, 24, 28, TimeSpan.Zero).AddTicks(1234567));
    }

    [Theory]
    [InlineData("received", JobState.Working)]
    [InlineData("queued", JobState.Working)]
    [InlineData("expired", JobState.Gone)]
    public async Task Other_job_states_map_to_what_the_runner_needs(string state, JobState expected)
    {
        handler.Answer(ListJobs, $$"""[{"id":"{{JobId}}","state":"{{state}}"}]""");

        var result = await Client().GetStateAsync(JobId, Now, CancellationToken.None);

        result.Value.ShouldBe(expected);
    }

    [Fact]
    public async Task A_listed_state_never_seen_before_is_not_guessed_at()
    {
        handler.Answer(ListJobs, $$"""[{"id":"{{JobId}}","state":"something new"}]""");

        var result = await Client().GetStateAsync(JobId, Now, CancellationToken.None);

        result.Error!.Code.ShouldBe(Errors.Malformed);
        result.Error.Message.ShouldContain("something new");
    }

    [Fact]
    public async Task A_download_that_goes_silent_is_given_up_and_leaves_no_file()
    {
        var bytes = Encoding.ASCII.GetBytes("dbn bytes");
        var file = Remote(bytes, SHA256.HashData(bytes));
        handler.Stream(file.Name, new SilentStream());

        var result = await Client(stall: Briefly).DownloadAsync(file, Path.Combine(folder, file.Name), CancellationToken.None);

        result.Error!.Code.ShouldBe(Errors.Unreachable);
        result.Error.Message.ShouldContain("stalled");
        Directory.GetFiles(folder).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_download_stopped_by_the_caller_is_not_called_a_stall()
    {
        var bytes = Encoding.ASCII.GetBytes("dbn bytes");
        var file = Remote(bytes, SHA256.HashData(bytes));
        using var cancel = new CancellationTokenSource(Briefly);
        handler.Stream(file.Name, new SilentStream());

        await Should.ThrowAsync<OperationCanceledException>(
            () => Client().DownloadAsync(file, Path.Combine(folder, file.Name), cancel.Token));
    }

    [Fact]
    public async Task A_call_that_gets_no_answer_in_time_is_unreachable()
    {
        handler.Stream(GetCost, new SilentStream());

        var result = await Client(call: Briefly).QuoteAsync(Request, CancellationToken.None);

        result.Error!.Code.ShouldBe(Errors.Unreachable);
        result.Error.Message.ShouldContain("no answer");
    }

    [Theory]
    [InlineData("http://api.databento.com/v0/batch/download/X/file")]
    [InlineData("not an address")]
    public async Task The_key_is_never_sent_to_an_address_that_is_not_https(string url)
    {
        var bytes = Encoding.ASCII.GetBytes("dbn bytes");
        var file = Remote(bytes, SHA256.HashData(bytes)) with { Url = url };

        var result = await Client().DownloadAsync(file, Path.Combine(folder, file.Name), CancellationToken.None);

        result.Error!.Code.ShouldBe(Errors.Malformed);
        handler.Paths.ShouldBeEmpty();
    }

    [Fact]
    public async Task Jobs_are_looked_for_from_the_day_before_they_were_submitted()
    {
        handler.Answer(ListJobs, "[]");
        handler.Answer(JobDetails, "{}", HttpStatusCode.NotFound);

        await Client().GetStateAsync(JobId, Now, CancellationToken.None);

        handler.Queries[ListJobs].ShouldContain($"since={Today.AddDays(-1):yyyy-MM-dd}");
    }

    [Fact]
    public async Task A_failed_job_listing_says_so()
    {
        handler.Answer(ListJobs, "{}", HttpStatusCode.ServiceUnavailable);

        var result = await Client().GetStateAsync(JobId, Now, CancellationToken.None);

        result.Error!.Code.ShouldBe(Errors.ServerError);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""[{"state":"done"}]""")]
    [InlineData($$"""[{"id":"{{JobId}}"}]""")]
    public async Task A_job_listing_of_the_wrong_shape_is_malformed(string body)
    {
        handler.Answer(ListJobs, body);

        var result = await Client().GetStateAsync(JobId, Now, CancellationToken.None);

        result.Error!.Code.ShouldBe(Errors.Malformed);
    }

    /// <summary>A job as <c>list_jobs</c> gives it: three fields.</summary>
    private static string Listed(string state = "done") =>
        $$"""[{"id":"{{JobId}}","state":"{{state}}","ts_received":"{{Received}}"}]""";

    /// <summary>A job as <c>get_job_details</c> gives it, for a day of <see cref="Request"/>.</summary>
    private static string Details(string symbols = OneSymbol, string startTime = Midnight, string expires = "null") =>
        $$"""{"id":"{{JobId}}","dataset":"{{Dataset}}","symbols":{{symbols}},"stype_in":"continuous","schema":"{{Mbo}}","start":"2026-10-28{{startTime}}","end":"2026-10-29{{Midnight}}","encoding":"dbn","compression":"zstd","state":"done","ts_received":"{{Received}}","ts_expiration":{{expires}}}""";

    private Task<Result<IReadOnlyList<RemoteJob>>> Jobs(IReadOnlySet<string>? known = null) =>
        Client().ListJobsAsync(Today, known ?? NoneKnown, CancellationToken.None);

    [Fact]
    public async Task A_listed_job_is_asked_about_and_read_in_full()
    {
        handler.Answer(ListJobs, Listed());
        handler.Answer(JobDetails, Details());

        var job = (await Jobs()).Value.ShouldHaveSingleItem();

        job.Fills(Request).ShouldBeTrue();
        job.Id.ShouldBe(JobId);
        job.ReceivedAt.ShouldBe(new DateTimeOffset(2026, 10, 28, 9, 23, 17, TimeSpan.Zero).AddTicks(5197080));
        job.ExpiresAt.ShouldBeNull();
        handler.Queries[JobDetails].ShouldContain($"job_id={JobId}");
    }

    [Fact]
    public async Task Jobs_are_listed_from_the_day_asked_for()
    {
        handler.Answer(ListJobs, "[]");

        await Jobs();

        handler.Queries[ListJobs].ShouldContain($"since={Today:yyyy-MM-dd}");
    }

    [Fact]
    public async Task A_job_the_ledger_knows_is_left_out_and_not_asked_about()
    {
        handler.Answer(ListJobs, Listed());

        var jobs = await Jobs(new HashSet<string> { JobId });

        jobs.Value.ShouldBeEmpty();
        handler.Paths.ShouldNotContain(p => p.Contains(JobDetails));
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("failed")]
    public async Task A_job_expired_or_failed_is_left_out_and_not_asked_about(string state)
    {
        handler.Answer(ListJobs, Listed(state));

        (await Jobs()).Value.ShouldBeEmpty();
        handler.Paths.ShouldNotContain(p => p.Contains(JobDetails));
    }

    [Theory]
    [InlineData("received")]
    [InlineData("queued")]
    [InlineData("processing")]
    [InlineData("done")]
    public async Task A_job_at_any_live_state_is_listed(string state)
    {
        handler.Answer(ListJobs, Listed(state));
        handler.Answer(JobDetails, Details());

        (await Jobs()).Value.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Symbols_are_read_as_Databento_spells_them()
    {
        handler.Answer(ListJobs, Listed());
        handler.Answer(JobDetails, Details("\"MNQ.n.0,MNQ.n.1\""));

        var job = (await Jobs()).Value.Single();

        job.Symbols.ShouldBe(["MNQ.n.0", "MNQ.n.1"]);
    }

    [Fact]
    public async Task When_a_jobs_files_expire_is_read()
    {
        handler.Answer(ListJobs, Listed());
        handler.Answer(JobDetails, Details(expires: $"\"{Expires}\""));

        (await Jobs()).Value.Single().ExpiresAt!.Value.UtcDateTime.Date.ShouldBe(new DateTime(2026, 11, 27));
    }

    [Fact]
    public async Task A_job_that_did_not_ask_for_whole_days_has_no_range_and_fills_nothing()
    {
        handler.Answer(ListJobs, Listed());
        handler.Answer(JobDetails, Details(startTime: Noon));

        var job = (await Jobs()).Value.Single();

        job.Range.ShouldBeNull();
        job.Fills(Request).ShouldBeFalse();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("""[{"state":"done"}]""")]
    [InlineData($$"""[{"id":"{{JobId}}"}]""")]
    [InlineData($$"""[{"id":"{{JobId}}","state":"done"}]""")]
    [InlineData($$"""[{"id":"{{JobId}}","state":"done","ts_received":"yesterday"}]""")]
    [InlineData($$"""[{"id":"{{JobId}}","state":"paused","ts_received":"{{Received}}"}]""")]
    [InlineData($$"""[{"id":"","state":"done","ts_received":"{{Received}}"}]""")]
    public async Task A_list_that_cannot_be_read_is_malformed_whatever_is_wrong(string body)
    {
        handler.Answer(ListJobs, body);

        (await Jobs()).Error!.Code.ShouldBe(Errors.Malformed);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("""{"dataset":"GLBX.MDP3"}""")]
    public async Task Details_that_cannot_be_read_fail_the_whole_list(string body)
    {
        handler.Answer(ListJobs, Listed());
        handler.Answer(JobDetails, body);

        (await Jobs()).Error!.Code.ShouldBe(Errors.Malformed);
    }

    [Theory]
    [InlineData("7")]
    [InlineData("""["MNQ.n.0"]""")]
    public async Task Symbols_of_no_known_shape_fail_the_whole_list(string symbols)
    {
        handler.Answer(ListJobs, Listed());
        handler.Answer(JobDetails, Details(symbols));

        (await Jobs()).Error!.Code.ShouldBe(Errors.Malformed);
    }

    [Fact]
    public async Task A_time_that_cannot_be_read_in_details_fails_the_whole_list()
    {
        handler.Answer(ListJobs, Listed());
        handler.Answer(JobDetails, Details(expires: "\"soon\""));

        (await Jobs()).Error!.Code.ShouldBe(Errors.Malformed);
    }

    [Fact]
    public async Task A_server_error_on_the_list_is_reported_as_one()
    {
        handler.Answer(ListJobs, "{}", HttpStatusCode.BadGateway);

        (await Jobs()).Error!.Code.ShouldBe(Errors.ServerError);
    }

    [Fact]
    public async Task A_refusal_of_details_fails_the_whole_list()
    {
        handler.Answer(ListJobs, Listed());
        handler.Answer(JobDetails, "{}", HttpStatusCode.NotFound);

        (await Jobs()).Error!.Code.ShouldBe(Errors.Rejected);
    }

    [Fact]
    public async Task A_network_failure_on_details_fails_the_whole_list()
    {
        handler.Answer(ListJobs, Listed());
        handler.Throw(JobDetails, new HttpRequestException(Offline));

        (await Jobs()).Error!.Code.ShouldBe(Errors.Unreachable);
    }

    [Fact]
    public async Task A_failed_connection_is_unreachable_and_says_nothing_of_a_server_error()
    {
        handler.Throw(GetCost, new HttpRequestException(Offline));

        var result = await Client().QuoteAsync(Request, CancellationToken.None);

        result.Error!.Code.ShouldBe(Errors.Unreachable);
        result.Error.Message.ShouldNotContain(ServerErrorWords);
    }

    [Fact]
    public async Task A_refusal_says_nothing_of_a_server_error()
    {
        handler.Answer(GetCost, "{}", HttpStatusCode.BadRequest);

        var result = await Client().QuoteAsync(Request, CancellationToken.None);

        result.Error!.Message.ShouldNotContain(ServerErrorWords);
    }

    [Fact]
    public async Task A_job_s_files_are_read_with_their_size_hash_and_address()
    {
        handler.Answer(ListFiles, $$"""
            [{"filename":"{{FileName(Day(10, 28), Mbo)}}","size":3,"hash":"sha256:abc","urls":{"https":"{{DownloadUrl}}"} }]
            """);

        var files = await Client().ListFilesAsync(JobId, CancellationToken.None);

        files.Value.ShouldBe([new RemoteFile(FileName(Day(10, 28), Mbo), 3, "abc", DownloadUrl)]);
    }

    [Fact]
    public async Task A_file_with_no_size_never_matches_a_download()
    {
        handler.Answer(ListFiles, $$"""[{"filename":"a.dbn.zst","hash":"sha256:abc","urls":{"https":"{{DownloadUrl}}"} }]""");

        var files = await Client().ListFilesAsync(JobId, CancellationToken.None);

        files.Value.ShouldHaveSingleItem().Size.ShouldBe(-1);
    }

    [Theory]
    [InlineData("""[{"filename":"","size":3,"hash":"sha256:abc","urls":{"https":"u"}}]""")]
    [InlineData("""[{"filename":"a","size":3,"hash":"sha256:abc","urls":{}}]""")]
    [InlineData("""[{"filename":"a","size":3,"hash":"sha256:abc"}]""")]
    [InlineData("""[{"filename":"a","size":3,"urls":{"https":"u"}}]""")]
    [InlineData("""[{"filename":"a","size":3,"hash":"md5:abc","urls":{"https":"u"}}]""")]
    [InlineData("""[{"size":3}]""")]
    [InlineData("""[{"filename":"a","size":"big"}]""")]
    [InlineData("{}")]
    public async Task A_file_entry_that_cannot_be_checked_is_malformed(string body)
    {
        handler.Answer(ListFiles, body);

        var files = await Client().ListFilesAsync(JobId, CancellationToken.None);

        files.Error!.Code.ShouldBe(Errors.Malformed);
    }

    [Fact]
    public async Task A_failed_file_listing_says_so()
    {
        handler.Answer(ListFiles, "{}", HttpStatusCode.Forbidden);

        var files = await Client().ListFilesAsync(JobId, CancellationToken.None);

        files.Error!.Code.ShouldBe(Errors.Rejected);
    }

    [Fact]
    public async Task A_download_of_the_wrong_size_leaves_no_file()
    {
        var bytes = Encoding.ASCII.GetBytes("dbn bytes");
        var file = Remote(bytes, SHA256.HashData(bytes)) with { Size = bytes.Length + 1 };
        handler.Answer(file.Name, bytes);

        var result = await Client().DownloadAsync(file, Path.Combine(folder, file.Name), CancellationToken.None);

        result.Error!.Code.ShouldBe(Errors.Corrupt);
        Directory.GetFiles(folder).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_hash_in_capitals_still_matches()
    {
        var bytes = Encoding.ASCII.GetBytes("dbn bytes");
        var file = Remote(bytes, SHA256.HashData(bytes));
        file = file with { Sha256 = file.Sha256.ToUpperInvariant() };
        handler.Answer(file.Name, bytes);

        var result = await Client().DownloadAsync(file, Path.Combine(folder, file.Name), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task A_refused_download_writes_nothing()
    {
        var file = Remote([1], SHA256.HashData([1]));
        handler.Answer(file.Name, "{}", HttpStatusCode.NotFound);

        var result = await Client().DownloadAsync(file, Path.Combine(folder, file.Name), CancellationToken.None);

        result.Error!.Code.ShouldBe(Errors.Rejected);
        Directory.GetFiles(folder).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_download_that_cannot_connect_leaves_no_file()
    {
        var file = Remote([1], SHA256.HashData([1]));
        handler.Throw(file.Name, new HttpRequestException(Offline));

        var result = await Client().DownloadAsync(file, Path.Combine(folder, file.Name), CancellationToken.None);

        result.Error!.Code.ShouldBe(Errors.Unreachable);
        Directory.GetFiles(folder).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_connection_lost_mid_file_leaves_no_partial_behind()
    {
        var file = Remote([1, 2, 3], SHA256.HashData([1, 2, 3]));
        handler.Stream(file.Name, new BrokenStream());

        var result = await Client().DownloadAsync(file, Path.Combine(folder, file.Name), CancellationToken.None);

        result.Error!.Code.ShouldBe(Errors.Unreachable);
        Directory.GetFiles(folder).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_download_over_an_older_copy_replaces_it()
    {
        var bytes = Encoding.ASCII.GetBytes("dbn bytes");
        var file = Remote(bytes, SHA256.HashData(bytes));
        var path = Path.Combine(folder, file.Name);
        await System.IO.File.WriteAllBytesAsync(path, [9]);
        handler.Answer(file.Name, bytes);

        await Client().DownloadAsync(file, path, CancellationToken.None);

        (await System.IO.File.ReadAllBytesAsync(path)).ShouldBe(bytes);
    }

    [Fact]
    public async Task Downloads_carry_the_key_too()
    {
        var file = Remote([1], SHA256.HashData([1]));
        handler.Answer(file.Name, new byte[] { 1 });

        await Client().DownloadAsync(file, Path.Combine(folder, file.Name), CancellationToken.None);

        handler.Authorization.ShouldBe(Convert.ToBase64String(Encoding.ASCII.GetBytes($"{ApiKey}:")));
    }

    [Fact]
    public async Task A_quote_asks_for_the_whole_range_with_an_exclusive_end()
    {
        handler.Answer(GetCost, "1.5");

        var quote = await Client().QuoteAsync(Request, CancellationToken.None);

        quote.Value.ShouldBe(1.5m);
        var query = handler.Queries[GetCost];
        query.ShouldContain($"dataset={Dataset}");
        query.ShouldContain($"schema={Mbo}");
        query.ShouldContain("start=2026-10-28");
        query.ShouldContain("end=2026-10-29");
    }

    [Fact]
    public async Task A_gateway_of_your_own_is_used_for_every_request()
    {
        handler.Answer(GetCost, "1");

        await Client(new Uri(OwnGateway)).QuoteAsync(Request, CancellationToken.None);

        handler.Hosts.ShouldBe([new Uri(OwnGateway).Host]);
    }

    private static readonly TimeSpan Briefly = TimeSpan.FromMilliseconds(150);

    private DatabentoClient Client() => new(new HttpClient(handler),
        Options.Create(new DatabentoOptions { ApiKey = ApiKey }));

    private DatabentoClient Client(TimeSpan? stall = null, TimeSpan? call = null)
    {
        var options = new DatabentoOptions { ApiKey = ApiKey };

        options.StallTimeout = stall ?? options.StallTimeout;
        options.CallTimeout = call ?? options.CallTimeout;

        return new DatabentoClient(new HttpClient(handler), Options.Create(options));
    }

    private DatabentoClient Client(Uri gateway) => new(new HttpClient(handler),
        Options.Create(new DatabentoOptions { ApiKey = ApiKey, Gateway = gateway }));

    private static RemoteFile Remote(byte[] bytes, byte[] hash)
    {
        var name = FileName(Day(10, 28), Mbo);

        return new RemoteFile(name, bytes.Length, Convert.ToHexStringLower(hash),
            $"https://api.databento.com/v0/batch/download/X/{JobId}/{name}");
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (byte[] Body, HttpStatusCode Status)> answers = [];
        private readonly Dictionary<string, Exception> failures = [];
        private readonly Dictionary<string, Stream> streams = [];

        public List<string> Paths { get; } = [];

        public Dictionary<string, string> Bodies { get; } = [];

        public Dictionary<string, string> Queries { get; } = [];

        public List<string> Hosts { get; } = [];

        public string? Authorization { get; private set; }

        public void Answer(string path, string body, HttpStatusCode status = HttpStatusCode.OK) =>
            answers[path] = (Encoding.UTF8.GetBytes(body), status);

        public void Answer(string path, byte[] body) => answers[path] = (body, HttpStatusCode.OK);

        public void Throw(string path, Exception failure) => failures[path] = failure;

        public void Stream(string path, Stream body) => streams[path] = body;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            Paths.Add(path);
            Hosts.Add(request.RequestUri.Host);
            Authorization = request.Headers.Authorization?.Parameter;

            if (failures.Keys.FirstOrDefault(path.EndsWith) is { } failing)
                throw failures[failing];

            if (streams.Keys.FirstOrDefault(path.EndsWith) is { } streamed)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(streams[streamed]) };

            var match = answers.Keys.FirstOrDefault(path.EndsWith)
                ?? throw new InvalidOperationException($"No answer for {path}.");

            Queries[match] = request.RequestUri.Query;

            if (request.Content is { } content)
                Bodies[match] = await content.ReadAsStringAsync(cancellationToken);

            var (body, status) = answers[match];

            return new HttpResponseMessage(status) { Content = new ByteArrayContent(body) };
        }
    }

    /// <summary>A response body on a connection that has gone silent: no bytes, and no end.</summary>
    private sealed class SilentStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            return 0;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            return 0;
        }
    }

    /// <summary>A response body whose connection drops after the headers.</summary>
    private sealed class BrokenStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new IOException(Offline);
    }
}
