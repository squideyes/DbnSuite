using System.Net;
using System.Net.Sockets;
using Amazon.S3;

namespace DbnSuite.Client.Archive;

/// <summary>
/// A store call or a read that failed in a way callers expect: a refused key,
/// an unreachable endpoint, a damaged file. <see cref="DbnArchive"/> turns it
/// into a result; a reader it opened throws it when the damage is further in.
/// </summary>
public sealed class ArchiveException(Error error, Exception? inner = null) : Exception(error.Message, inner)
{
    public Error Error { get; } = error;

    /// <summary>The error an exception from a store means, or null when it is a bug.</summary>
    internal static Error? Map(Exception e) => e switch
    {
        // S3 answers a skewed clock with a 403, so it is asked about before the 403 that means a bad key.
        AmazonS3Exception { ErrorCode: "RequestTimeTooSkewed" } => new Error(Errors.Denied,
            "The archive refused the request because this computer's clock is off by more than 15 minutes."),
        AmazonS3Exception { StatusCode: HttpStatusCode.Forbidden } s3 => new Error(Errors.Denied,
            $"The archive refused the key ({s3.ErrorCode}). Check the access key and secret."),
        AmazonS3Exception { ErrorCode: "InvalidAccessKeyId" or "SignatureDoesNotMatch" } s3 => new Error(Errors.Denied,
            $"The archive refused the key ({s3.ErrorCode}). Check the access key and secret."),
        AmazonS3Exception { ErrorCode: "NoSuchBucket" } s3 => new Error(Errors.Denied,
            $"The archive has no such bucket ({s3.Message})."),
        AmazonS3Exception { StatusCode: >= HttpStatusCode.InternalServerError } s3 => new Error(Errors.Unreachable,
            $"The archive failed to answer ({(int)s3.StatusCode} {s3.ErrorCode}). Try again in a while."),
        HttpRequestException or SocketException or TimeoutException => new Error(Errors.Unreachable,
            $"The archive could not be reached: {e.Message}"),
        IOException when e.InnerException is SocketException => new Error(Errors.Unreachable,
            $"The archive could not be reached: {e.Message}"),
        _ => null
    };
}
