namespace DbnSync.Core;

/// <summary>Databento's licensing tiers, which decide how far back a schema is free.</summary>
public enum Level
{
    L0,
    L1,
    L2,
    L3
}

/// <summary>Which instruments of a root a schema is fetched for.</summary>
public enum Selection
{
    /// <summary>
    /// Every instrument under the root, spreads included (<c>ES.FUT</c>).
    /// Cheap on disk for the L0 schemas and the tape, and complete.
    /// </summary>
    Parent,

    /// <summary>
    /// The leading outrights by open interest (<c>ES.n.0</c>, <c>ES.n.1</c>).
    /// For the book schemas, where spreads dominate the bytes: CL's parent
    /// MBO is ten times its front month.
    /// </summary>
    Leading
}

/// <param name="Name">Databento's schema name.</param>
/// <param name="Level">Licensing tier.</param>
/// <param name="Selection">Which instruments to ask for.</param>
/// <param name="ChunkDays">Days per batch job, sized so one job is a few GB at most.</param>
public sealed record SchemaSpec(string Name, Level Level, Selection Selection, int ChunkDays);
