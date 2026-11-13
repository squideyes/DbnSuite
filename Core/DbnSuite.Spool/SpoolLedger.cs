using System.Text.Json;

namespace DbnSuite.Spool;

/// <summary>One trade date on the shelf.</summary>
/// <param name="Ticks">The date's tick tapes (L1 and Trade) were made.</param>
/// <param name="Mbo">
/// When <c>mbo</c> covered the date but held no tape of its contract, a stamp of those <c>mbo</c> files,
/// so the date is tried again once they change, and not before. Null otherwise.
/// </param>
public sealed record SpooledDate(DateOnly Date, int Tapes, bool Ticks, string? Mbo = null);

/// <summary>A trade date that was started and never finished: DbnSpool stopped while working on it.</summary>
/// <param name="Count">Starts that never finished, under this build.</param>
/// <param name="Build">The code that made them, from <see cref="ISpoolEngine.Build"/>.</param>
public sealed record SpoolAttempt(DateOnly Date, int Count, string Build);

/// <summary>
/// What a root has on the shelf, kept on the shelf itself, so a restart or a new
/// machine carries on from where the last one stopped.
/// </summary>
public sealed class SpoolLedger
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Dictionary<DateOnly, SpooledDate> dates = [];
    private readonly Dictionary<DateOnly, SpoolAttempt> attempts = [];

    /// <summary>What the archive held at the end of the last pass; while it stays the same, there is nothing to do.</summary>
    public string Seen { get; set; } = "";

    public IReadOnlyList<SpooledDate> Dates
    {
        get => [.. dates.Values.OrderBy(d => d.Date)];
        init
        {
            foreach (var date in value)
                dates[date.Date] = date;
        }
    }

    /// <summary>Dates started and not finished.</summary>
    public IReadOnlyList<SpoolAttempt> Attempts
    {
        get => [.. attempts.Values.OrderBy(a => a.Date)];
        init
        {
            foreach (var attempt in value)
                attempts[attempt.Date] = attempt;
        }
    }

    public SpooledDate? Spooled(DateOnly date) => dates.GetValueOrDefault(date);

    public void Add(SpooledDate date) => dates[date.Date] = date;

    /// <summary>Forgets a date whose tapes have left the shelf.</summary>
    public void Remove(DateOnly date)
    {
        dates.Remove(date);
        attempts.Remove(date);
    }

    public SpoolAttempt? Attempt(DateOnly date) => attempts.GetValueOrDefault(date);

    /// <summary>Notes that a date is starting. A start under the same build adds to the count; a new build starts it afresh.</summary>
    public void Begin(DateOnly date, string build) =>
        attempts[date] = new SpoolAttempt(date, attempts.TryGetValue(date, out var before) && before.Build == build ? before.Count + 1 : 1, build);

    /// <summary>Notes that a date finished, whether it passed or failed.</summary>
    public void End(DateOnly date) => attempts.Remove(date);

    /// <summary>True when this build has already stopped on the date as often as it may.</summary>
    public bool IsSetAside(DateOnly date, string build, int mostAttempts) =>
        attempts.TryGetValue(date, out var attempt) && attempt.Build == build && attempt.Count >= mostAttempts;

    public static string KeyOf(string dataset, string root) => $"{dataset}/spool/{root}.json";

    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, Json);

    public static SpoolLedger From(byte[]? bytes) =>
        bytes is null ? new SpoolLedger() : JsonSerializer.Deserialize<SpoolLedger>(bytes, Json) ?? new SpoolLedger();
}
