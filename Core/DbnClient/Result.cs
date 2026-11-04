namespace DbnClient;

/// <summary>An expected failure, named by a stable code.</summary>
public sealed record Error(string Code, string Message)
{
    public override string ToString() => Message;
}

/// <summary>Error codes callers can branch on.</summary>
public static class Errors
{
    /// <summary>The archive has no file for that day; it was never fetched.</summary>
    public const string NotStored = "archive.not_stored";

    /// <summary>The archive holds the day, and it had no data.</summary>
    public const string NoData = "archive.no_data";

    /// <summary>The key was refused, or does not reach the bucket.</summary>
    public const string Denied = "archive.denied";

    /// <summary>The archive could not be reached at all.</summary>
    public const string Unreachable = "archive.unreachable";

    /// <summary>The archive's files are not in a layout this version knows.</summary>
    public const string UnknownLayout = "archive.unknown_layout";

    /// <summary>A day's file is damaged: it cannot be decompressed, or is not the DBN it should be.</summary>
    public const string Corrupt = "archive.corrupt";

    /// <summary>An adjusted series needs a roll's settlements, and the archive lacks them.</summary>
    public const string NoSettlement = "export.no_settlement";

    /// <summary>A symbol that is neither a root, a contract nor a continuous name.</summary>
    public const string BadSymbol = "input.bad_symbol";

    /// <summary>The archive's dataset has no trading day this version knows, so it cannot be exported.</summary>
    public const string UnsupportedDataset = "input.unsupported_dataset";
}

/// <summary>A value, or the <see cref="Error"/> that stopped it.</summary>
public readonly struct Result<T>
{
    private readonly T? value;

    private Result(T? value, Error? error)
    {
        this.value = value;
        Error = error;
    }

    public Error? Error { get; }

    public bool IsSuccess => Error is null;

    public T Value => IsSuccess
        ? value!
        : throw new InvalidOperationException($"No value: {Error}");

    public static implicit operator Result<T>(T value) => new(value, null);

    public static implicit operator Result<T>(Error error) => new(default, error);
}
