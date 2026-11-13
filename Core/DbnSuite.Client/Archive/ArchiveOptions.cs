namespace DbnSuite.Client.Archive;

/// <summary>Where an archive is and the key that reads it.</summary>
public sealed record ArchiveOptions
{
    /// <summary>An S3 endpoint, such as <c>https://s3.example.com</c>, or a local folder.</summary>
    public required string Location { get; init; }

    public string Bucket { get; init; } = "dbn";

    /// <summary>A folder inside the bucket that holds the archive.</summary>
    public string Prefix { get; init; } = "";

    public string AccessKey { get; init; } = "";

    public string SecretKey { get; init; } = "";

    public string Region { get; init; } = "us-east-1";

    public string Dataset { get; init; } = "GLBX.MDP3";

    /// <summary>Leaves the keys out: a record's own <c>ToString</c> would print them.</summary>
    public override string ToString() => $"{Location} ({Bucket})";

    /// <summary>True when <see cref="Location"/> is a folder on this machine, not an S3 endpoint.</summary>
    public bool IsFolder => !Location.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        && !Location.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}
