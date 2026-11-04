namespace DbnSync.Data;

/// <summary>Any S3-compatible store: RustFS, MinIO, Garage, DigitalOcean Spaces. Bound from <c>Store</c>.</summary>
public sealed class StoreOptions
{
    public const string Section = "Store";

    public string ServiceUrl { get; set; } = "";

    public string Bucket { get; set; } = "dbn";

    /// <summary>A folder inside the bucket that confines every read and write.</summary>
    public string Prefix { get; set; } = "";

    public string AccessKey { get; set; } = "";

    public string SecretKey { get; set; } = "";

    public string Region { get; set; } = "us-east-1";

    /// <summary>The object key for an archive-relative path, under <see cref="Prefix"/>.</summary>
    public string Key(string relative)
    {
        var prefix = Prefix.Trim('/');

        return prefix.Length == 0 ? relative : $"{prefix}/{relative}";
    }
}
