using System.Text.RegularExpressions;

namespace DbnSync.Core;

/// <summary>
/// CME Globex futures. Groups are futures roots (<c>ES</c>, <c>MNQ</c>): the
/// cheap schemas are asked for by parent (<c>ES.FUT</c>, spreads included),
/// the book schemas for the leading outrights by open interest
/// (<c>ES.n.0</c>, <c>ES.n.1</c>), since spreads dominate their bytes.
/// </summary>
public sealed partial class GlbxMdp3 : IDatasetProfile
{
    public static GlbxMdp3 Instance { get; } = new();

    private GlbxMdp3()
    {
    }

    public string Dataset => "GLBX.MDP3";

    public ProfileStatus Status => ProfileStatus.Verified;

    public string Maintainer => "SquidEyes";

    public string GroupNoun => "root";

    public DateOnly Start { get; } = new(2010, 6, 6);

    public IReadOnlyDictionary<string, SchemaSpec> Schemas { get; } = new[]
    {
        new SchemaSpec("mbo", Level.L3, Selection.Leading, 5),
        new SchemaSpec("mbp-10", Level.L2, Selection.Leading, 2),
        new SchemaSpec("mbp-1", Level.L1, Selection.Leading, 10),
        new SchemaSpec("tbbo", Level.L1, Selection.Leading, 30),
        new SchemaSpec("bbo-1s", Level.L1, Selection.Leading, 30),
        new SchemaSpec("bbo-1m", Level.L1, Selection.Leading, 92),
        new SchemaSpec("trades", Level.L1, Selection.Parent, 30),
        new SchemaSpec("ohlcv-1s", Level.L0, Selection.Parent, 92),
        new SchemaSpec("ohlcv-1m", Level.L0, Selection.Parent, 366),
        new SchemaSpec("ohlcv-1h", Level.L0, Selection.Parent, 366),
        new SchemaSpec("ohlcv-1d", Level.L0, Selection.Parent, 366),
        new SchemaSpec("definition", Level.L0, Selection.Parent, 366),
        new SchemaSpec("statistics", Level.L0, Selection.Parent, 366),
        new SchemaSpec("status", Level.L0, Selection.Parent, 366)
    }.ToDictionary(s => s.Name, StringComparer.Ordinal);

    /// <summary>
    /// Standard Futures: 12 months of L1, one month of L2/L3, and 16+ years of
    /// L0. Measured 2026-08-19 with <c>metadata.get_cost</c>: <c>mbo</c> is free
    /// to 32 days back and <c>mbp-1</c> to 365, so each keeps a day of margin.
    /// </summary>
    public Entitlement Included { get; } = new(364, 31);

    public bool IsGroup(string group) => Root().IsMatch(group);

    public BatchRequest Request(JobRequest job, int depth) =>
        job.Schema.Selection == Selection.Parent
            ? new BatchRequest(Dataset, job.Schema.Name, [$"{job.Root}.FUT"], "parent", job.Range)
            : new BatchRequest(Dataset, job.Schema.Name,
                [.. Enumerable.Range(0, depth).Select(i => $"{job.Root}.n.{i}")],
                "continuous", job.Range);

    [GeneratedRegex("^[A-Z0-9]{1,6}$")]
    private static partial Regex Root();
}
