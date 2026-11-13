namespace DbnSuite.Sync.Data;

/// <summary>Bound from <c>Databento</c>; the key falls back to <c>DATABENTO_API_KEY</c>.</summary>
public sealed class DatabentoOptions
{
    public const string Section = "Databento";
    public const string KeyVariable = "DATABENTO_API_KEY";

    public string ApiKey { get; set; } = "";

    public Uri Gateway { get; set; } = new("https://hist.databento.com");

    /// <summary>
    /// How long a download may go without a byte arriving before it is given up, for a later pass to
    /// fetch again. Without it a connection that goes silent would hold the worker for ever.
    /// </summary>
    public TimeSpan StallTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a call that answers in JSON may take, from sending it to reading its last byte.</summary>
    public TimeSpan CallTimeout { get; set; } = TimeSpan.FromMinutes(5);
}
