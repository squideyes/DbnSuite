namespace DbnSpool;

/// <summary>What <c>/status</c> reports.</summary>
/// <param name="ThisPass">The roots the running pass has finished, so a long pass shows its progress and problems as they happen.</param>
public sealed record SpoolSnapshot(
    bool Running,
    DateTimeOffset? LastPassAt,
    IReadOnlyList<RootResult> LastPass,
    string? LastProblem,
    IReadOnlyList<RootResult> ThisPass);

/// <summary>The worker's latest state, for <c>/status</c>.</summary>
public sealed class SpoolStatus
{
    private readonly Lock gate = new();

    public SpoolSnapshot Snapshot { get; private set; } = new(false, null, [], null, []);

    public void Started()
    {
        lock (gate)
            Snapshot = Snapshot with { Running = true, ThisPass = [] };
    }

    /// <summary>Notes a root's result as soon as it is done.</summary>
    public void RootDone(RootResult result)
    {
        lock (gate)
            Snapshot = Snapshot with { ThisPass = [.. Snapshot.ThisPass, result], LastProblem = result.Problem ?? Snapshot.LastProblem };
    }

    /// <summary>Notes a problem as soon as it happens.</summary>
    public void Problem(string problem)
    {
        lock (gate)
            Snapshot = Snapshot with { LastProblem = problem };
    }

    public void Finished(DateTimeOffset at, IReadOnlyList<RootResult> results, string? problem)
    {
        lock (gate)
            Snapshot = new SpoolSnapshot(false, at, results, problem, []);
    }
}
