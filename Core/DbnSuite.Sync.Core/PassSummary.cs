using System.Collections.Concurrent;

namespace DbnSuite.Sync.Core;

public enum IssueLevel
{
    Warning,
    Error
}

/// <summary>Something a pass could not do, with enough context to find it again.</summary>
public sealed record Issue(
    DateTimeOffset At,
    IssueLevel Level,
    string Code,
    string Message,
    string? JobId = null,
    string? Schema = null,
    string? Root = null,
    DayRange? Range = null);

/// <summary>What one pass did, and every issue it met.</summary>
public sealed record PassSummary(
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    int JobsCollected,
    int JobsSubmitted,
    int JobsPending,
    int JobsOwed,
    int FilesStored,
    long BytesStored,
    int EmptyDays,
    int QuotesRefused,
    int Errors,
    string? Failure,
    IReadOnlyList<Issue> Issues,
    decimal Spent = 0m)
{
    /// <summary>True while anything is pending or still owed, so the next pass should come soon.</summary>
    public bool Busy => JobsPending > 0 || JobsOwed > 0;

    /// <summary>
    /// Worth keeping in the pass history: it changed the archive or met a
    /// problem. A quiet pass that found nothing to do is not recorded.
    /// </summary>
    public bool Eventful => JobsCollected > 0 || JobsSubmitted > 0 || FilesStored > 0
        || Issues.Count > 0 || Failure is not null;

    /// <summary>A pass that died before it could do anything.</summary>
    public static PassSummary Crashed(DateTimeOffset startedAt, DateTimeOffset at, Exception ex) =>
        new(startedAt, at, 0, 0, 0, 0, 0, 0, 0, 0, 1, ex.Message,
            [new Issue(at, IssueLevel.Error, IssueCodes.Crashed, $"{ex.GetType().Name}: {ex.Message}")]);
}

/// <summary>Counts for a pass in progress; files are stored concurrently.</summary>
internal sealed class PassTally(DateTimeOffset startedAt, TimeProvider clock)
{
    private readonly ConcurrentQueue<Issue> issues = new();

    private int filesStored;
    private long bytesStored;

    public int JobsCollected { get; set; }
    public int JobsSubmitted { get; set; }
    public int JobsPending { get; set; }
    public int JobsOwed { get; set; }
    public int EmptyDays { get; set; }
    public int QuotesRefused { get; set; }

    /// <summary>What paid jobs submitted this pass were quoted at, together.</summary>
    public decimal Spent { get; set; }

    public void Stored(long bytes)
    {
        Interlocked.Increment(ref filesStored);
        Interlocked.Add(ref bytesStored, bytes);
    }

    public void Issue(IssueLevel level, string code, string message, PendingJob? job = null) =>
        issues.Enqueue(new Issue(clock.GetUtcNow(), level, code, message,
            job?.Id, job?.Schema, job?.Root, job?.Range));

    public void Issue(IssueLevel level, string code, string message, JobRequest request, DayRange range) =>
        issues.Enqueue(new Issue(clock.GetUtcNow(), level, code, message,
            null, request.Schema.Name, request.Root, range));

    public PassSummary Finish(DateTimeOffset finishedAt, string? failure = null)
    {
        var all = issues.ToArray();

        return new(startedAt, finishedAt, JobsCollected, JobsSubmitted, JobsPending, JobsOwed,
            filesStored, bytesStored, EmptyDays, QuotesRefused,
            all.Count(i => i.Level == IssueLevel.Error), failure, all, Spent);
    }
}
