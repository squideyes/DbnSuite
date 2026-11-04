using DbnSync.Core;

namespace DbnSync.Worker;

/// <summary>What <c>/status</c> reports: the last pass and when the next one starts.</summary>
public sealed record StatusSnapshot(
    bool Running,
    DateTimeOffset? NextPassAt,
    PassSummary? LastPass,
    IReadOnlyList<string> Roots,
    IReadOnlyList<string> Schemas,
    string SpendPolicy);

/// <summary>Shared between the worker, which writes it, and the endpoint and pages, which read it.</summary>
public sealed class SyncStatus(SyncOptions options)
{
    private volatile StatusSnapshot snapshot = new(false, null, null,
        options.RootList, options.SchemaList, options.Spend.Describe());

    public StatusSnapshot Snapshot => snapshot;

    /// <summary>Raised when a pass starts or finishes.</summary>
    public event Action? Changed;

    public void Started()
    {
        snapshot = snapshot with { Running = true, NextPassAt = null };

        Changed?.Invoke();
    }

    public void Finished(PassSummary? pass, DateTimeOffset nextPassAt)
    {
        snapshot = snapshot with { Running = false, LastPass = pass ?? snapshot.LastPass, NextPassAt = nextPassAt };

        Changed?.Invoke();
    }
}
