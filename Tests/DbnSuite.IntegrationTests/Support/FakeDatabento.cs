using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DbnSuite.Sync.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace DbnSuite.IntegrationTests.Support;

/// <summary>The paths of Databento's historical API that DbnSync calls, under <c>/v0/</c>.</summary>
public static class Endpoints
{
    public const string DatasetRange = "metadata.get_dataset_range";
    public const string GetCost = "metadata.get_cost";
    public const string SubmitJob = "batch.submit_job";
    public const string ListJobs = "batch.list_jobs";
    public const string JobDetails = "batch.get_job_details";
    public const string ListFiles = "batch.list_files";
    public const string Download = "batch/download";
}

/// <summary>What Databento's <c>batch.list_jobs</c> calls a job's state.</summary>
public static class JobStates
{
    public const string Processing = "processing";
    public const string Done = "done";
    public const string Expired = "expired";
    public const string Failed = "failed";
}

/// <summary>What a request asks Databento for, as the quote and the submission both spell it.</summary>
public sealed record JobSpec(string Dataset, string Schema, string Symbols, string StypeIn, DateOnly From, DateOnly Upto)
{
    public string Root => Symbols.Split('.')[0];

    public static JobSpec Read(IReadOnlyDictionary<string, string> fields) => new(fields["dataset"], fields["schema"],
        fields["symbols"], fields["stype_in"], ReadDay(fields["start"]), ReadDay(fields["end"]).AddDays(-1));

    private static DateOnly ReadDay(string text) => DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>One request as the gateway saw it.</summary>
public sealed record GatewayCall(string Endpoint, IReadOnlyDictionary<string, string> Fields, string? Authorization)
{
    /// <summary>The file a download asks for.</summary>
    public string? File => Fields.GetValueOrDefault(FakeDatabento.FileField);

    public JobSpec Job => JobSpec.Read(Fields);
}

/// <summary>A file a job delivers.</summary>
public sealed record FakeFile(string Name, byte[] Bytes)
{
    public string Sha256 => Convert.ToHexStringLower(SHA256.HashData(Bytes));
}

/// <summary>A job Databento has accepted.</summary>
public sealed class FakeJob(string id, JobSpec spec, string state, DateTimeOffset submittedAt, IEnumerable<FakeFile> files,
    string encoding = BatchRequest.Encoding)
{
    public string Encoding { get; } = encoding;

    /// <summary>When its files go, as <c>get_job_details</c> says; null until it is known.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    public string Id { get; } = id;

    public JobSpec Spec { get; } = spec;

    public DateTimeOffset SubmittedAt { get; } = submittedAt;

    public string State { get; set; } = state;

    public List<FakeFile> Files { get; } = [.. files];

    public FakeFile DataFile(DateOnly day) => Files.Single(f => f.Name == BatchFactory.FileName(Spec.Schema, day));
}

/// <summary>An answer other than the usual one.</summary>
public sealed record Reply(int Status, byte[] Body, int? DropAfter = null)
{
    /// <summary>Databento's answer, verbatim, for a request none of whose symbols existed.</summary>
    public static readonly Reply NoSymbols = Json(StatusCodes.Status422UnprocessableEntity, """
        {"detail":{"case":"symbology_invalid_request","message":"None of the symbols could be resolved","status_code":422,"docs":"https://databento.com/docs/api-reference-historical/basics/symbology","payload":null}}
        """);

    /// <summary>Databento's answer, verbatim, for a request whose symbols had no records.</summary>
    public static readonly Reply NoData = Json(StatusCodes.Status422UnprocessableEntity, """
        {"detail":{"case":"data_no_data_found_for_request","message":"No data was found for the request you submitted.","status_code":422,"docs":"https://databento.com/docs/api-reference-historical/batch/batch-submit-job","payload":null}}
        """);

    /// <summary>The connection is cut before an answer, as a timeout looks from the client.</summary>
    public static readonly Reply Dropped = new(StatusCodes.Status200OK, [], DropAfter: 0);

    public static Reply Json(int status, string body) => new(status, Encoding.UTF8.GetBytes(body));

    public static Reply Ok(string body) => Json(StatusCodes.Status200OK, body);

    public static Reply Ok(byte[] body) => new(StatusCodes.Status200OK, body);
}

/// <summary>
/// Databento's historical gateway as far as DbnSync uses it, on a real socket
/// so the worker's own HTTP client runs. Quotes, job states and failures are
/// the scenario's to set; batches come from <see cref="BatchFactory"/>. Every
/// request is kept, in order.
/// </summary>
public sealed class FakeDatabento : IAsyncDisposable
{
    public const string FileField = "file";

    private const string Prefix = "/v0/";
    private const string JobIdField = "job_id";
    private const string EncodingField = "encoding";
    private const string SinceField = "since";
    private const string DownloadPrefix = Endpoints.Download + "/";
    private const string DayFormat = "yyyy-MM-dd";

    private readonly ConcurrentQueue<GatewayCall> calls = new();
    private readonly ConcurrentDictionary<string, FakeJob> jobs = new();
    private readonly ConcurrentDictionary<string, Func<GatewayCall, Reply?>> faults = new();
    private readonly ConcurrentDictionary<DateOnly, byte> quietDays = new();
    private readonly TimeProvider clock;
    private readonly WebApplication app;

    private int nextJob;

    private FakeDatabento(TimeProvider clock)
    {
        this.clock = clock;

        var builder = WebApplication.CreateSlimBuilder();

        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        app = builder.Build();
        app.Run(HandleAsync);
    }

    /// <summary>Everything the gateway was asked, in order.</summary>
    public IReadOnlyList<GatewayCall> Calls => [.. calls];

    /// <summary>Every job accepted, oldest first.</summary>
    public IReadOnlyList<FakeJob> Jobs => [.. jobs.Values.OrderBy(j => j.Id, StringComparer.Ordinal)];

    public Uri Address { get; private set; } = null!;

    public DateTimeOffset AvailableEnd { get; set; } = Sample.AvailableEnd;

    /// <summary>What <c>metadata.get_cost</c> quotes. Free unless a scenario prices it.</summary>
    public Func<JobSpec, decimal> Quote { get; set; } = _ => 0m;

    /// <summary>The state a job has when it is accepted.</summary>
    public string NewJobState { get; set; } = JobStates.Done;

    /// <summary>Runs while <c>batch.submit_job</c> is in flight, before any fault applies.</summary>
    public Func<JobSpec, Task>? OnSubmit { get; set; }

    /// <summary>Runs while a download is in flight.</summary>
    public Func<GatewayCall, Task>? OnDownload { get; set; }

    public static async Task<FakeDatabento> StartAsync(TimeProvider clock)
    {
        var gateway = new FakeDatabento(clock);

        await gateway.app.StartAsync();

        gateway.Address = new Uri(gateway.app.Urls.Single());

        return gateway;
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }

    /// <summary>Stops listening, as an outage would.</summary>
    public Task StopAsync() => app.StopAsync();

    public IReadOnlyList<GatewayCall> CallsTo(string endpoint) => [.. Calls.Where(c => c.Endpoint == endpoint)];

    /// <summary>Answers an endpoint with whatever <paramref name="fault"/> returns; null means as usual.</summary>
    public void Fail(string endpoint, Func<GatewayCall, Reply?> fault) => faults[endpoint] = fault;

    public void Fail(string endpoint, Reply reply) => Fail(endpoint, _ => reply);

    public void Heal(string endpoint) => faults.TryRemove(endpoint, out _);

    /// <summary>Marks days a job delivers no file for, as weekends always are.</summary>
    public void Quiet(params IEnumerable<DateOnly> days)
    {
        foreach (var day in days)
            quietDays[day] = 0;
    }

    public void SetAllJobs(string state)
    {
        foreach (var job in jobs.Values)
            job.State = state;
    }

    private bool Trades(DateOnly day) =>
        day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !quietDays.ContainsKey(day);

    private async Task HandleAsync(HttpContext context)
    {
        var call = await ReadAsync(context.Request);

        calls.Enqueue(call);

        if (!Authorized(call))
        {
            await WriteAsync(context, Reply.Json(StatusCodes.Status401Unauthorized, """{"detail":"Unauthorized"}"""));

            return;
        }

        if (call.Endpoint == Endpoints.SubmitJob && OnSubmit is { } onSubmit)
            await onSubmit(call.Job);

        if (call.Endpoint == Endpoints.Download && OnDownload is { } onDownload)
            await onDownload(call);

        var answer = faults.TryGetValue(call.Endpoint, out var fault) ? fault(call) : null;

        await WriteAsync(context, answer ?? Answer(call));
    }

    private static async Task<GatewayCall> ReadAsync(HttpRequest request)
    {
        var path = request.Path.Value![Prefix.Length..];
        var fields = request.Query.ToDictionary(q => q.Key, q => q.Value.ToString(), StringComparer.Ordinal);

        if (request.HasFormContentType)
        {
            foreach (var field in await request.ReadFormAsync())
                fields[field.Key] = field.Value.ToString();
        }

        if (!path.StartsWith(DownloadPrefix, StringComparison.Ordinal))
            return new GatewayCall(path, fields, request.Headers.Authorization);

        var parts = path[DownloadPrefix.Length..].Split('/');

        fields[JobIdField] = parts[0];
        fields[FileField] = parts[1];

        return new GatewayCall(Endpoints.Download, fields, request.Headers.Authorization);
    }

    private static bool Authorized(GatewayCall call) =>
        AuthenticationHeaderValue.TryParse(call.Authorization, out var header)
            && header is { Scheme: "Basic", Parameter: { } value }
            && value == Convert.ToBase64String(Encoding.ASCII.GetBytes($"{Sample.ApiKey}:"));

    /// <summary>Takes a job as <c>batch.submit_job</c> would, for a fault that then loses the answer.</summary>
    public FakeJob Accept(GatewayCall call) => NewJob(call.Job, call.Fields[EncodingField]);

    /// <summary>A job Databento already holds, from before this scenario began.</summary>
    public FakeJob Hold(JobSpec spec, string state, string encoding = BatchRequest.Encoding) => NewJob(spec, encoding, state);

    private Reply Answer(GatewayCall call) => call.Endpoint switch
    {
        Endpoints.DatasetRange => DatasetRange(),
        Endpoints.GetCost => Reply.Ok(Quote(call.Job).ToString(CultureInfo.InvariantCulture)),
        Endpoints.SubmitJob => Submit(call),
        Endpoints.ListJobs => ListJobs(call),
        Endpoints.JobDetails => JobDetails(call),
        Endpoints.ListFiles => ListFiles(call),
        Endpoints.Download => Download(call),
        _ => new Reply(StatusCodes.Status404NotFound, [])
    };

    private Reply DatasetRange()
    {
        var end = Stamp(AvailableEnd);
        var range = new { start = "2010-06-06T00:00:00.000000000Z", end };

        return Reply.Ok(JsonSerializer.Serialize(new
        {
            range.start,
            range.end,
            schema = GlbxMdp3.Instance.Schemas.Keys.ToDictionary(name => name, _ => range)
        }));
    }

    private Reply Submit(GatewayCall call)
    {
        var job = Accept(call);

        return Reply.Ok(JsonSerializer.Serialize(new { id = job.Id, state = job.State }));
    }

    private FakeJob NewJob(JobSpec spec, string encoding, string? state = null)
    {
        var now = clock.GetUtcNow();
        var id = string.Create(CultureInfo.InvariantCulture,
            $"GLBX-{now:yyyyMMdd}-TEST{Interlocked.Increment(ref nextJob):D6}");

        IEnumerable<FakeFile> files =
        [
            .. new DayRange(spec.From, spec.Upto).Each().Where(Trades)
                .Select(day => new FakeFile(BatchFactory.FileName(spec.Schema, day),
                    BatchFactory.Day(spec.Schema, spec.Root, day))),
            new("manifest.json", "{}"u8.ToArray()),
            new("metadata.json", "{}"u8.ToArray())
        ];

        return jobs[id] = new FakeJob(id, spec, state ?? NewJobState, now, files, encoding);
    }

    private Reply ListJobs(GatewayCall call)
    {
        var since = DateOnly.ParseExact(call.Fields[SinceField], DayFormat, CultureInfo.InvariantCulture);

        return Reply.Ok(JsonSerializer.Serialize(jobs.Values
            .Where(j => DateOnly.FromDateTime(j.SubmittedAt.UtcDateTime) >= since)
            .OrderBy(j => j.Id, StringComparer.Ordinal)
            .Select(j => new { id = j.Id, state = j.State, ts_received = Stamp(j.SubmittedAt) })));
    }

    private Reply JobDetails(GatewayCall call)
    {
        var job = jobs[call.Fields[JobIdField]];

        return Reply.Ok(JsonSerializer.Serialize(new
        {
            id = job.Id,
            dataset = job.Spec.Dataset,
            symbols = job.Spec.Symbols,
            stype_in = job.Spec.StypeIn,
            schema = job.Spec.Schema,
            start = Stamp(job.Spec.From),
            end = Stamp(job.Spec.Upto.AddDays(1)),
            encoding = job.Encoding,
            state = job.State,
            ts_received = Stamp(job.SubmittedAt),
            ts_expiration = job.ExpiresAt is { } expires ? Stamp(expires) : null
        }));
    }

    private static string Stamp(DateOnly day) => Stamp(new DateTimeOffset(day, TimeOnly.MinValue, TimeSpan.Zero));

    /// <summary>A time as Databento writes it, to the nanosecond.</summary>
    private static string Stamp(DateTimeOffset time) =>
        time.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff00", CultureInfo.InvariantCulture) + "Z";

    private Reply ListFiles(GatewayCall call)
    {
        var job = jobs[call.Fields[JobIdField]];

        return Reply.Ok(JsonSerializer.Serialize(job.Files.Select(file => new
        {
            filename = file.Name,
            size = file.Bytes.Length,
            hash = $"sha256:{file.Sha256}",
            urls = new { https = new Uri(Address, $"{Prefix}{DownloadPrefix}{job.Id}/{file.Name}").ToString() }
        })));
    }

    private Reply Download(GatewayCall call) =>
        Reply.Ok(jobs[call.Fields[JobIdField]].Files.Single(f => f.Name == call.File).Bytes);

    private static async Task WriteAsync(HttpContext context, Reply reply)
    {
        if (reply.DropAfter is { } kept)
        {
            if (kept > 0)
            {
                context.Response.ContentLength = reply.Body.Length;

                await context.Response.Body.WriteAsync(reply.Body.AsMemory(0, kept));
                await context.Response.Body.FlushAsync();
            }

            context.Abort();

            return;
        }

        context.Response.StatusCode = reply.Status;
        context.Response.ContentLength = reply.Body.Length;

        await context.Response.Body.WriteAsync(reply.Body);
    }
}
