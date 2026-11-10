using System.Net;
using System.Net.Sockets;
using Amazon.S3;
using DbnClient;
using DbnClient.Archive;
using Shouldly;

namespace DbnSync.UnitTests.ClientTests;

public sealed class ArchiveExceptionTests
{
    private const string Trouble = "It went wrong.";

    private static AmazonS3Exception S3(HttpStatusCode status, string code) =>
        new(Trouble) { StatusCode = status, ErrorCode = code };

    [Fact]
    public void A_forbidden_response_is_a_refused_key()
    {
        var error = ArchiveException.Map(S3(HttpStatusCode.Forbidden, "AccessDenied"));

        error!.Code.ShouldBe(Errors.Denied);
        error.Message.ShouldContain("AccessDenied");
    }

    [Theory]
    [InlineData("InvalidAccessKeyId")]
    [InlineData("SignatureDoesNotMatch")]
    public void A_bad_key_is_refused_whatever_its_status(string code)
    {
        var error = ArchiveException.Map(S3(HttpStatusCode.BadRequest, code));

        error!.Code.ShouldBe(Errors.Denied);
        error.Message.ShouldContain(code);
    }

    [Fact]
    public void A_clock_too_far_out_says_so()
    {
        var error = ArchiveException.Map(S3(HttpStatusCode.Forbidden, "RequestTimeTooSkewed"));

        error!.Code.ShouldBe(Errors.Denied);
        error.Message.ShouldContain("clock");
    }

    [Fact]
    public void A_missing_bucket_says_so()
    {
        var error = ArchiveException.Map(S3(HttpStatusCode.NotFound, "NoSuchBucket"));

        error!.Code.ShouldBe(Errors.Denied);
        error.Message.ShouldContain("bucket");
    }

    public static TheoryData<Exception> Unreachable() => new()
    {
        new HttpRequestException(Trouble),
        new SocketException(),
        new TimeoutException(Trouble),
        new IOException(Trouble, new SocketException()),
        new AmazonS3Exception(Trouble) { StatusCode = HttpStatusCode.InternalServerError, ErrorCode = "InternalError" },
        new AmazonS3Exception(Trouble) { StatusCode = HttpStatusCode.ServiceUnavailable, ErrorCode = "SlowDown" }
    };

    [Theory]
    [MemberData(nameof(Unreachable))]
    public void A_network_failure_is_an_unreachable_archive(Exception failure)
    {
        ArchiveException.Map(failure)!.Code.ShouldBe(Errors.Unreachable);
    }

    public static TheoryData<Exception> Bugs() => new()
    {
        new InvalidOperationException(Trouble),
        new IOException(Trouble),
        new AmazonS3Exception(Trouble) { StatusCode = HttpStatusCode.BadRequest, ErrorCode = "InvalidArgument" }
    };

    [Theory]
    [MemberData(nameof(Bugs))]
    public void Anything_else_is_not_an_expected_failure(Exception failure)
    {
        ArchiveException.Map(failure).ShouldBeNull();
    }

    [Fact]
    public void The_exception_carries_its_error_and_what_caused_it()
    {
        var error = new Error(Errors.Denied, Trouble);
        var cause = new InvalidOperationException();

        var exception = new ArchiveException(error, cause);

        exception.Error.ShouldBe(error);
        exception.Message.ShouldBe(Trouble);
        exception.InnerException.ShouldBe(cause);
    }
}
