using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DbnSync.Core;
using Microsoft.Extensions.Options;

namespace DbnSync.Data;

/// <summary>
/// Databento's historical API over HTTP: Basic auth with the key as the user
/// name and no password, against <c>/v0</c>.
/// <para>
/// The only way in to <c>batch.submit_job</c> is <see cref="SubmitWithinAsync"/>,
/// which quotes the identical request first and submits only when the quote
/// is at most the limit it is given: zero, unless a spend policy says
/// otherwise. The quote is the streaming price, which batch never exceeds, so
/// the guard can refuse a job within the limit but never admit one above it.
/// </para>
/// <para>
/// That holds only as far as Databento's quote matches its bill. Anything that
/// spends money through this class does so at its own risk.
/// </para>
/// </summary>
public sealed class DatabentoClient(HttpClient http, IOptions<DatabentoOptions> options)
    : IDatabento
{
    /// <summary>Databento's words, in a 422, for a request none of whose symbols existed.</summary>
    internal const string NoSymbolsResolved = "None of the symbols could be resolved";

    /// <summary>Databento's case, in a 422, for a request whose symbols had no records.</summary>
    internal const string NoDataFound = "data_no_data_found_for_request";

    private readonly DatabentoOptions settings = options.Value;

    public async Task<Result<IReadOnlyDictionary<string, DateTimeOffset>>> GetAvailableEndsAsync(
        string dataset, CancellationToken cancel)
    {
        var json = await GetJsonAsync(Api($"metadata.get_dataset_range?dataset={Escape(dataset)}"),
            cancel).ConfigureAwait(false);

        if (!json.IsSuccess)
            return json.Error!;

        using var document = json.Value;

        return Parsed<IReadOnlyDictionary<string, DateTimeOffset>>("get_dataset_range", () =>
        {
            if (!document.RootElement.TryGetProperty("schema", out var schemas)
                || schemas.ValueKind != JsonValueKind.Object)
            {
                return new Error(Errors.Malformed, "get_dataset_range has no per-schema ranges.");
            }

            var ends = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

            foreach (var schema in schemas.EnumerateObject())
            {
                if (schema.Value.TryGetProperty("end", out var end)
                    && TryTimestamp(end.GetString(), out var at))
                {
                    ends[schema.Name] = at;
                }
            }

            return ends;
        });
    }

    public async Task<Result<string>> SubmitWithinAsync(BatchRequest request, decimal maxCost,
        CancellationToken cancel)
    {
        var cost = await QuoteAsync(request, cancel).ConfigureAwait(false);

        if (!cost.IsSuccess)
            return cost.Error!;

        var limit = Math.Max(0m, maxCost);

        if (cost.Value > limit)
        {
            return new Error(Errors.OverLimit, string.Create(CultureInfo.InvariantCulture,
                $"{request.Schema} {string.Join(',', request.Symbols)} {request.Range} quotes ${cost.Value}, over the ${limit} allowed."));
        }

        var fields = Fields(request);

        fields["stype_out"] = "instrument_id";
        fields["encoding"] = BatchRequest.Encoding;
        fields["compression"] = "zstd";
        fields["split_duration"] = "day";
        fields["delivery"] = "download";

        using var message = new HttpRequestMessage(HttpMethod.Post, Api("batch.submit_job"))
        {
            Content = new FormUrlEncodedContent(fields)
        };

        var json = await SendJsonAsync(message, cancel).ConfigureAwait(false);

        if (!json.IsSuccess)
            return json.Error!;

        using var document = json.Value;

        return Parsed<string>("submit_job", () =>
            document.RootElement.TryGetProperty("id", out var id)
                && id.GetString() is { Length: > 0 } text
                    ? text
                    : new Error(Errors.Malformed, "submit_job returned no job id."));
    }

    public async Task<Result<JobState>> GetStateAsync(string jobId, DateTimeOffset submittedAt,
        CancellationToken cancel)
    {
        var since = submittedAt.UtcDateTime.AddDays(-1)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var json = await GetJsonAsync(Api($"batch.list_jobs?since={since}"), cancel)
            .ConfigureAwait(false);

        if (!json.IsSuccess)
            return json.Error!;

        using var document = json.Value;

        return Parsed<JobState>("list_jobs", () =>
        {
            foreach (var job in document.RootElement.EnumerateArray())
            {
                if (job.GetProperty("id").GetString() != jobId)
                    continue;

                return StateOf(job.GetProperty("state").GetString()) ?? JobState.Gone;
            }

            return JobState.Gone;
        });
    }

    /// <remarks>
    /// <c>list_jobs</c> gives only a job's id, state and time, and
    /// <c>get_job_details</c> the rest, so each job not known is asked about in turn.
    /// </remarks>
    public async Task<Result<IReadOnlyList<RemoteJob>>> ListJobsAsync(DateOnly since, IReadOnlySet<string> known,
        CancellationToken cancel)
    {
        var day = since.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var json = await GetJsonAsync(Api($"batch.list_jobs?since={day}"), cancel).ConfigureAwait(false);

        if (!json.IsSuccess)
            return json.Error!;

        using var document = json.Value;

        var listed = Parsed<IReadOnlyList<(string Id, DateTimeOffset At)>>("list_jobs", () =>
        {
            var jobs = new List<(string, DateTimeOffset)>();

            foreach (var job in document.RootElement.EnumerateArray())
            {
                var id = job.GetProperty("id").GetString();
                var state = StateOf(job.GetProperty("state").GetString());

                // A state this code has never seen may be a job that was paid for: refuse to guess.
                if (string.IsNullOrEmpty(id) || state is null)
                    return new Error(Errors.Malformed, $"list_jobs entry '{id}' has no id or a state unknown here.");

                if (state == JobState.Gone || known.Contains(id))
                    continue;

                jobs.Add((id, Time(job, "ts_received")));
            }

            return jobs;
        });

        if (!listed.IsSuccess)
            return listed.Error!;

        var found = new List<RemoteJob>();

        foreach (var (id, at) in listed.Value)
        {
            var details = await GetJobAsync(id, at, cancel).ConfigureAwait(false);

            if (!details.IsSuccess)
                return details.Error!;

            found.Add(details.Value);
        }

        return found;
    }

    private async Task<Result<RemoteJob>> GetJobAsync(string id, DateTimeOffset receivedAt, CancellationToken cancel)
    {
        var json = await GetJsonAsync(Api($"batch.get_job_details?job_id={Escape(id)}"), cancel).ConfigureAwait(false);

        if (!json.IsSuccess)
            return json.Error!;

        using var document = json.Value;

        return Parsed<RemoteJob>("get_job_details", () => ReadJob(id, receivedAt, document.RootElement));
    }

    public async Task<Result<IReadOnlyList<RemoteFile>>> ListFilesAsync(string jobId,
        CancellationToken cancel)
    {
        var json = await GetJsonAsync(Api($"batch.list_files?job_id={Escape(jobId)}"), cancel)
            .ConfigureAwait(false);

        if (!json.IsSuccess)
            return json.Error!;

        using var document = json.Value;

        return Parsed<IReadOnlyList<RemoteFile>>("list_files", () =>
        {
            var files = new List<RemoteFile>();

            foreach (var file in document.RootElement.EnumerateArray())
            {
                var name = file.GetProperty("filename").GetString() ?? "";
                var size = file.TryGetProperty("size", out var s) ? s.GetInt64() : -1;
                var hash = file.TryGetProperty("hash", out var h) ? h.GetString() ?? "" : "";

                var url = file.TryGetProperty("urls", out var urls)
                    && urls.TryGetProperty("https", out var https)
                        ? https.GetString() ?? "" : "";

                if (name.Length == 0 || url.Length == 0 || !hash.StartsWith("sha256:", StringComparison.Ordinal))
                    return new Error(Errors.Malformed, $"list_files entry '{name}' lacks a url or sha256.");

                files.Add(new RemoteFile(name, size, hash["sha256:".Length..], url));
            }

            return files;
        });
    }

    /// <summary>
    /// Streams to a <c>.partial</c> beside the target, hashing as it goes, and
    /// renames into place only once the size and SHA-256 both match what
    /// Databento listed. A short or altered file never looks downloaded.
    /// </summary>
    public async Task<Result<long>> DownloadAsync(RemoteFile file, string path,
        CancellationToken cancel)
    {
        var partial = path + ".partial";

        try
        {
            using var message = Authorized(new HttpRequestMessage(HttpMethod.Get, file.Url));

            using var response = await http.SendAsync(message,
                HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return await RejectedAsync(response, cancel).ConfigureAwait(false);

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            long written = 0;

            await using (var source = await response.Content.ReadAsStreamAsync(cancel)
                .ConfigureAwait(false))
            await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write,
                FileShare.None, 1 << 20, useAsync: true))
            {
                var buffer = new byte[1 << 20];
                int read;

                while ((read = await source.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
                {
                    sha.AppendData(buffer, 0, read);

                    await target.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);

                    written += read;
                }
            }

            var actual = Convert.ToHexStringLower(sha.GetHashAndReset());

            if (written != file.Size || !actual.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partial);

                return new Error(Errors.Corrupt, string.Create(CultureInfo.InvariantCulture,
                    $"{file.Name}: {written} bytes sha256 {actual}, expected {file.Size} bytes sha256 {file.Sha256}."));
            }

            File.Move(partial, path, overwrite: true);

            return written;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException
            || (ex is TaskCanceledException && !cancel.IsCancellationRequested))
        {
            File.Delete(partial);

            return new Error(Errors.Unreachable, $"{file.Name}: {ex.Message}");
        }
    }

    public async Task<Result<decimal>> QuoteAsync(BatchRequest request, CancellationToken cancel)
    {
        var query = string.Join('&', Fields(request).Select(f => $"{f.Key}={Escape(f.Value)}"));

        using var message = new HttpRequestMessage(HttpMethod.Get, Api($"metadata.get_cost?{query}"));

        var json = await SendJsonAsync(message, cancel).ConfigureAwait(false);

        if (!json.IsSuccess)
            return json.Error!;

        using var document = json.Value;

        return document.RootElement.ValueKind == JsonValueKind.Number
            && document.RootElement.TryGetDecimal(out var cost)
            && cost >= 0
                ? cost
                : new Error(Errors.Malformed, $"get_cost answered {document.RootElement}.");
    }

    /// <summary>
    /// Reads an answer that parsed as JSON. <see cref="JsonElement"/> throws on
    /// any other shape than the one asked of it; that is Databento changing its
    /// API, which should be an issue on one job and not the end of the pass.
    /// </summary>
    private static Result<T> Parsed<T>(string call, Func<Result<T>> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return new Error(Errors.Malformed, $"{call}: {ex.Message}");
        }
    }

    /// <summary>What Databento's word for a job's state means here, or null for one not known.</summary>
    private static JobState? StateOf(string? state) => state switch
    {
        "done" => JobState.Done,
        "received" or "queued" or "processing" => JobState.Working,
        "expired" or "failed" => JobState.Gone,
        _ => null
    };

    private static RemoteJob ReadJob(string id, DateTimeOffset receivedAt, JsonElement job) =>
        new(id, receivedAt,
            job.TryGetProperty("ts_expiration", out var expires) && expires.ValueKind != JsonValueKind.Null
                ? Time(job, "ts_expiration")
                : null,
            job.GetProperty("dataset").GetString() ?? "",
            job.GetProperty("schema").GetString() ?? "",
            (job.GetProperty("symbols").GetString() ?? "").Split(','),
            job.GetProperty("stype_in").GetString() ?? "",
            WholeDays(Time(job, "start"), Time(job, "end")),
            job.GetProperty("encoding").GetString() ?? "");

    /// <summary>The UTC days from a start to an exclusive end, or null when they are not whole days.</summary>
    private static DayRange? WholeDays(DateTimeOffset start, DateTimeOffset end) =>
        start.TimeOfDay == TimeSpan.Zero && end.TimeOfDay == TimeSpan.Zero && end > start
            ? new DayRange(DateOnly.FromDateTime(start.UtcDateTime), DateOnly.FromDateTime(end.UtcDateTime).AddDays(-1))
            : null;

    private static DateTimeOffset Time(JsonElement job, string field) =>
        TryTimestamp(job.GetProperty(field).GetString(), out var at)
            ? at
            : throw new FormatException($"{field} is not a time.");

    private static Dictionary<string, string> Fields(BatchRequest request) => new(StringComparer.Ordinal)
    {
        ["dataset"] = request.Dataset,
        ["schema"] = request.Schema,
        ["symbols"] = string.Join(',', request.Symbols),
        ["stype_in"] = request.StypeIn,
        ["start"] = request.Range.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),

        // Exclusive, as everywhere in this API.
        ["end"] = request.Range.Upto.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
    };

    private Task<Result<JsonDocument>> GetJsonAsync(Uri uri, CancellationToken cancel) =>
        SendJsonAsync(new HttpRequestMessage(HttpMethod.Get, uri), cancel);

    private async Task<Result<JsonDocument>> SendJsonAsync(HttpRequestMessage message,
        CancellationToken cancel)
    {
        try
        {
            using var response = await http.SendAsync(Authorized(message), cancel)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return await RejectedAsync(response, cancel).ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);

            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            return new Error(Errors.Malformed, ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException
            || (ex is TaskCanceledException && !cancel.IsCancellationRequested))
        {
            return new Error(Errors.Unreachable, ex.Message);
        }
        finally
        {
            message.Dispose();
        }
    }

    private static async Task<Error> RejectedAsync(HttpResponseMessage response,
        CancellationToken cancel)
    {
        var body = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);

        // Only whole-request failures: a request that resolves some of its
        // symbols, or finds some records, succeeds and returns what there is.
        var unprocessable = response.StatusCode == HttpStatusCode.UnprocessableEntity;

        // A 5xx is no refusal: the request may have taken effect, and only a
        // definite refusal may take a paid job back out of the spend ledger.
        var serverError = (int)response.StatusCode >= 500;

        var code = serverError
            ? Errors.ServerError
            : unprocessable && body.Contains(NoSymbolsResolved, StringComparison.OrdinalIgnoreCase)
                ? Errors.NoSymbols
                : unprocessable && body.Contains(NoDataFound, StringComparison.Ordinal)
                    ? Errors.NoData
                    : Errors.Rejected;

        return new Error(code, string.Create(CultureInfo.InvariantCulture,
            $"{(serverError ? "Databento had a server error: " : "")}{(int)response.StatusCode} {response.ReasonPhrase}: {body[..Math.Min(body.Length, 500)]}"));
    }

    private HttpRequestMessage Authorized(HttpRequestMessage message)
    {
        message.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($"{settings.ApiKey}:")));

        return message;
    }

    private Uri Api(string pathAndQuery) => new(settings.Gateway, $"/v0/{pathAndQuery}");

    private static string Escape(string value) => Uri.EscapeDataString(value);

    /// <summary>Databento stamps in nanoseconds; seven fractional digits is all a DateTimeOffset holds.</summary>
    private static bool TryTimestamp(string? text, out DateTimeOffset at)
    {
        at = default;

        if (string.IsNullOrEmpty(text))
            return false;

        var dot = text.IndexOf('.', StringComparison.Ordinal);

        if (dot > 0)
        {
            var zone = text.IndexOfAny(['Z', '+'], dot);
            var fraction = zone > 0 ? text[(dot + 1)..zone] : text[(dot + 1)..];

            text = text[..dot] + "." + fraction[..Math.Min(fraction.Length, 7)]
                + (zone > 0 ? text[zone..] : "");
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out at);
    }
}
