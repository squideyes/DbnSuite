using DbnSuite.Testing;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace DbnSuite.IntegrationTests.Support;

/// <summary>The S3 server of Deploy/docker-compose.yml, started once for the collection.</summary>
public sealed class RustFs : IAsyncLifetime
{
    public const string AccessKey = "dbnsuite-integration";
    public const string SecretKey = "dbnsuite-integration-secret";

    private const string Image = "rustfs/rustfs:1.0.4";
    private const int S3Port = 9000;

    private readonly IContainer server = new ContainerBuilder(Image)
        .WithEnvironment("RUSTFS_ACCESS_KEY", AccessKey)
        .WithEnvironment("RUSTFS_SECRET_KEY", SecretKey)
        .WithPortBinding(S3Port, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(r => r.ForPort(S3Port).ForPath("/health"))
            .AddCustomWaitStrategy(new S3Ready(AccessKey, SecretKey, S3Port)))
        .Build();

    public string Endpoint => $"http://{server.Hostname}:{server.GetMappedPublicPort(S3Port)}";

    public Task InitializeAsync() => server.StartAsync();

    public async Task DisposeAsync() => await server.DisposeAsync();
}

/// <summary>Scenarios against the shared S3 server, one bucket each, one at a time.</summary>
[CollectionDefinition(Name)]
public sealed class ArchiveCollection : ICollectionFixture<RustFs>
{
    public const string Name = "Archive";
}
