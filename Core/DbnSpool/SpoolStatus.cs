namespace DbnSpool;

/// <summary>What <c>/status</c> reports.</summary>
public sealed record SpoolSnapshot(bool Running, DateTimeOffset? LastPassAt, IReadOnlyList<RootResult> LastPass, string? LastProblem);

/// <summary>The worker's latest state, for <c>/status</c>.</summary>
public sealed class SpoolStatus
{
    public SpoolSnapshot Snapshot { get; private set; } = new(false, null, [], null);

    public void Started() => Snapshot = Snapshot with { Running = true };

    public void Finished(DateTimeOffset at, IReadOnlyList<RootResult> results, string? problem) =>
        Snapshot = new SpoolSnapshot(false, at, results, problem);
}
