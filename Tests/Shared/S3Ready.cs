using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;

namespace DbnSuite.Testing;

/// <summary>
/// Waits until RustFS takes writes. Its <c>/health</c> answers first, and so does
/// an unsigned request, while a signed call made then gets a 503; under load that
/// failed test fixtures at random, so only a signed write proves the server ready.
/// </summary>
internal sealed class S3Ready(string accessKey, string secretKey, int port) : IWaitUntil
{
    private const string Probe = "ready-probe";

    public async Task<bool> UntilAsync(IContainer container)
    {
        using var s3 = new AmazonS3Client(new BasicAWSCredentials(accessKey, secretKey), new AmazonS3Config
        {
            ServiceURL = $"http://{container.Hostname}:{container.GetMappedPublicPort(port)}",
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
            MaxErrorRetry = 0
        });

        try
        {
            await s3.PutBucketAsync(Probe);

            return true;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.Conflict)
        {
            // An earlier try got through and only its answer was lost.
            return true;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }
}
