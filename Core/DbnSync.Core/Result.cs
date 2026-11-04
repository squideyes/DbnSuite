namespace DbnSync.Core;

/// <summary>An expected failure, named by a stable code.</summary>
public sealed record Error(string Code, string Message)
{
    public override string ToString() => $"{Code}: {Message}";
}

/// <summary>Error codes the sync loop branches on.</summary>
public static class Errors
{
    /// <summary>Databento quoted more than the spend policy allows; nothing was submitted.</summary>
    public const string OverLimit = "cost.over_limit";

    /// <summary>The code <see cref="OverLimit"/> had before spend policies, kept so old passes still explain themselves.</summary>
    public const string NotFree = "cost.not_free";

    /// <summary>No answer at all: a failed connection, a DNS failure, a timeout.</summary>
    public const string Unreachable = "databento.unreachable";

    /// <summary>
    /// A 5xx: Databento answered, but with a failure of its own. Like
    /// <see cref="Unreachable"/> it settles nothing, since the request may have taken effect.
    /// </summary>
    public const string ServerError = "databento.server_error";

    public const string Rejected = "databento.rejected";
    public const string Malformed = "databento.malformed";

    /// <summary>
    /// Not one requested symbol existed in the range: the root had not been
    /// listed yet, as MCL had not before July 2021. There is no data to fetch.
    /// </summary>
    public const string NoSymbols = "databento.no_symbols";

    /// <summary>
    /// The symbols existed but Databento holds no records for them in the
    /// range, as for MCL ohlcv-1s in the weeks before its first trade.
    /// </summary>
    public const string NoData = "databento.no_data";
    public const string Corrupt = "download.corrupt";
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
