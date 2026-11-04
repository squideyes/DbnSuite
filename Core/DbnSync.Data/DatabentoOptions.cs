namespace DbnSync.Data;

/// <summary>Bound from <c>Databento</c>; the key falls back to <c>DATABENTO_API_KEY</c>.</summary>
public sealed class DatabentoOptions
{
    public const string Section = "Databento";
    public const string KeyVariable = "DATABENTO_API_KEY";

    public string ApiKey { get; set; } = "";

    public Uri Gateway { get; set; } = new("https://hist.databento.com");
}
