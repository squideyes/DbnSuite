using System.Net.Http.Headers;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;

namespace DbnSuite.IntegrationTests.Support;

/// <summary>
/// Deploy/Caddyfile, unchanged, in the image the compose file names, set up as
/// Deploy/.env.example says, with a stand-in for the worker behind it. Started
/// once for the class.
/// </summary>
public sealed class CaddyProxy : IAsyncLifetime
{
    public const string User = "owner";
    public const string Password = "test-only-password";
    public const string BehindIt = "the dashboard";

    private const string Image = "caddy:2.10.2";
    private const string DashboardName = "dashboard.test";
    private const string S3Name = "s3.test";
    private const string WorkerAlias = "dbnsync";
    private const string SolutionFile = "DbnSuite.slnx";
    private const int WorkerPort = 8080;
    private const int HttpPort = 80;

    private readonly INetwork network = new NetworkBuilder().Build();

    private IContainer worker = null!;
    private IContainer proxy = null!;

    private static FileInfo Caddyfile()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);

        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, SolutionFile)))
            folder = folder.Parent;

        return new FileInfo(Path.Combine(folder!.FullName, "Deploy", "Caddyfile"));
    }

    public async Task InitializeAsync()
    {
        await network.CreateAsync();

        worker = new ContainerBuilder(Image)
            .WithNetwork(network)
            .WithNetworkAliases(WorkerAlias)
            .WithCommand("caddy", "respond", "--listen", $":{WorkerPort}", "--body", BehindIt)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(WorkerPort))
            .Build();

        await worker.StartAsync();

        var hash = await worker.ExecAsync(["caddy", "hash-password", "--plaintext", Password]);

        proxy = new ContainerBuilder(Image)
            .WithNetwork(network)
            .WithResourceMapping(Caddyfile(), "/etc/caddy/")
            .WithEnvironment("S3_DOMAIN", $"http://{S3Name}")
            .WithEnvironment("DASHBOARD_DOMAIN", $"http://{DashboardName}")
            .WithEnvironment("DASHBOARD_USER", User)
            .WithEnvironment("DASHBOARD_PASSWORD_HASH", Convert.ToBase64String(Encoding.ASCII.GetBytes(hash.Stdout.Trim())))
            .WithPortBinding(HttpPort, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(HttpPort))
            .Build();

        await proxy.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await proxy.DisposeAsync();
        await worker.DisposeAsync();
        await network.DisposeAsync();
    }

    /// <summary>Asks for the dashboard by its name, as a browser would, with these credentials if any.</summary>
    public async Task<HttpResponseMessage> GetDashboardAsync(string? user = null, string? password = null)
    {
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://{proxy.Hostname}:{proxy.GetMappedPublicPort(HttpPort)}/");

        request.Headers.Host = DashboardName;

        if (user is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.ASCII.GetBytes($"{user}:{password}")));
        }

        return await client.SendAsync(request);
    }
}
