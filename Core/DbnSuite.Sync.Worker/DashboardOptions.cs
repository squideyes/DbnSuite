using System.Globalization;

namespace DbnSuite.Sync.Worker;

/// <summary>Settings that only the dashboard reads.</summary>
public sealed class DashboardOptions
{
    public const string Section = "Dashboard";

    /// <summary>
    /// Seq's address as a browser reaches it (not as the worker does). When
    /// empty, the dashboard shows no links to the logs.
    /// </summary>
    public string? LogsUrl { get; set; }

    /// <summary>Every log line about one job: they all carry it as <c>Job</c>.</summary>
    public string? LogsForJob(string jobId) => Logs($"Job = '{jobId}'");

    /// <summary>Everything logged in the few minutes around a moment.</summary>
    public string? LogsAround(DateTimeOffset at) => Logs(string.Create(CultureInfo.InvariantCulture,
        $"@Timestamp >= DateTime('{at.AddMinutes(-5).UtcDateTime:O}') and @Timestamp <= DateTime('{at.AddMinutes(1).UtcDateTime:O}')"));

    private string? Logs(string filter) => string.IsNullOrWhiteSpace(LogsUrl)
        ? null
        : $"{LogsUrl.TrimEnd('/')}/#/events?filter={Uri.EscapeDataString(filter)}";
}
