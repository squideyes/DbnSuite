namespace DbnSync.Core;

/// <summary>One batch job, as Databento will be asked for it. Built by <see cref="IDatasetProfile.Request"/>.</summary>
public sealed record BatchRequest(
    string Dataset,
    string Schema,
    IReadOnlyList<string> Symbols,
    string StypeIn,
    DayRange Range)
{
    /// <summary>What every job is delivered as.</summary>
    public const string Encoding = "dbn";
}

/// <summary>A job Databento holds, and what it was asked for.</summary>
/// <param name="ExpiresAt">When its files go, or null while Databento hasn't said.</param>
/// <param name="Range">The days asked for, or null when it did not ask for whole UTC days.</param>
public sealed record RemoteJob(
    string Id,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? ExpiresAt,
    string Dataset,
    string Schema,
    IReadOnlyList<string> Symbols,
    string StypeIn,
    DayRange? Range,
    string Encoding)
{
    /// <summary>Whether this job is exactly what <paramref name="request"/> would buy.</summary>
    public bool Fills(BatchRequest request) =>
        Dataset == request.Dataset
        && Schema == request.Schema
        && StypeIn == request.StypeIn
        && Range == request.Range
        && Encoding == BatchRequest.Encoding
        && Symbols.ToHashSet(StringComparer.Ordinal).SetEquals(request.Symbols);
}

/// <summary>A file a finished job produced.</summary>
public sealed record RemoteFile(string Name, long Size, string Sha256, string Url);

public enum JobState
{
    /// <summary>Received, queued or processing.</summary>
    Working,

    Done,

    /// <summary>Expired, failed, or unknown to the account: plan its days again.</summary>
    Gone
}

/// <summary>A job submitted and not yet stored. Persisted, since a job outlives the process.</summary>
public sealed record PendingJob(
    string Id,
    string Root,
    string Schema,
    DayRange Range,
    DateTimeOffset SubmittedAt);

/// <summary>Databento's historical API, as far as DbnSync uses it.</summary>
public interface IDatabento
{
    /// <summary>Where each schema's data currently ends.</summary>
    Task<Result<IReadOnlyDictionary<string, DateTimeOffset>>> GetAvailableEndsAsync(
        string dataset, CancellationToken cancel);

    /// <summary>What Databento quotes for the job, in US dollars. Submits nothing.</summary>
    Task<Result<decimal>> QuoteAsync(BatchRequest request, CancellationToken cancel);

    /// <summary>
    /// Quotes the job again and submits it only if the quote is at most
    /// <paramref name="maxCost"/>, which is zero unless a spend policy says
    /// otherwise; above it, fails with <see cref="Errors.OverLimit"/> and
    /// submits nothing. A negative limit counts as zero.
    /// </summary>
    Task<Result<string>> SubmitWithinAsync(BatchRequest request, decimal maxCost, CancellationToken cancel);

    Task<Result<JobState>> GetStateAsync(string jobId, DateTimeOffset submittedAt,
        CancellationToken cancel);

    /// <summary>
    /// Every job received since <paramref name="since"/> that has not expired or
    /// failed, leaving out <paramref name="known"/>. Costs nothing, but takes a call
    /// for each job, so keep <paramref name="since"/> recent. Any answer that cannot
    /// be read, about any job, fails the whole list.
    /// </summary>
    Task<Result<IReadOnlyList<RemoteJob>>> ListJobsAsync(DateOnly since, IReadOnlySet<string> known,
        CancellationToken cancel);

    Task<Result<IReadOnlyList<RemoteFile>>> ListFilesAsync(string jobId,
        CancellationToken cancel);

    /// <summary>Downloads to <paramref name="path"/>, checking size and SHA-256.</summary>
    Task<Result<long>> DownloadAsync(RemoteFile file, string path, CancellationToken cancel);
}

/// <summary>The S3 archive.</summary>
public interface IArchiveStore
{
    /// <summary>Days held for one schema and root, empty markers included.</summary>
    Task<IReadOnlySet<DateOnly>> ListHeldAsync(string schema, string root,
        CancellationToken cancel);

    /// <summary>Every stored day of one schema and root, with its size.</summary>
    Task<IReadOnlyList<StoredDay>> ListDaysAsync(string schema, string root,
        CancellationToken cancel);

    Task SavePassAsync(PassSummary pass, CancellationToken cancel);

    /// <summary>Recorded passes from <paramref name="since"/> on, newest first.</summary>
    Task<IReadOnlyList<PassSummary>> ListPassesAsync(DateOnly since, DateOnly until,
        CancellationToken cancel);

    Task PutFileAsync(string schema, string root, DateOnly day, string path,
        CancellationToken cancel);

    Task PutEmptyAsync(string schema, string root, DateOnly day, CancellationToken cancel);

    Task<IReadOnlyList<PendingJob>> ListPendingAsync(CancellationToken cancel);

    Task SavePendingAsync(PendingJob job, CancellationToken cancel);

    Task DeletePendingAsync(string jobId, CancellationToken cancel);

    /// <summary>Every paid job recorded in the month that <paramref name="month"/> falls in.</summary>
    Task<IReadOnlyList<SpendEntry>> ListSpendAsync(DateOnly month, CancellationToken cancel);

    Task SaveSpendAsync(SpendEntry entry, CancellationToken cancel);

    Task DeleteSpendAsync(SpendEntry entry, CancellationToken cancel);
}
