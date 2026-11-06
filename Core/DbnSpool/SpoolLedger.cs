using System.Text.Json;

namespace DbnSpool;

/// <summary>One trade date on the shelf.</summary>
/// <param name="Ticks">True when <c>mbo</c> covered the date, so its tick tapes were built.</param>
public sealed record SpooledDate(DateOnly Date, int Tapes, bool Ticks);

/// <summary>
/// What a root has on the shelf, kept on the shelf itself, so a restart or a new
/// machine carries on from where the last one stopped.
/// </summary>
public sealed class SpoolLedger
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Dictionary<DateOnly, SpooledDate> dates = [];

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

    public SpooledDate? Spooled(DateOnly date) => dates.GetValueOrDefault(date);

    public void Add(SpooledDate date) => dates[date.Date] = date;

    public static string KeyOf(string dataset, string root) => $"{dataset}/spool/{root}.json";

    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, Json);

    public static SpoolLedger From(byte[]? bytes) =>
        bytes is null ? new SpoolLedger() : JsonSerializer.Deserialize<SpoolLedger>(bytes, Json) ?? new SpoolLedger();
}
