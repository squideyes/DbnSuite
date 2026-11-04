using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace DbnSuite.IntegrationTests.Support;

/// <summary>The worker's real Program, in this process, with the settings a scenario gives it.</summary>
public sealed class WorkerHost(string environment, IReadOnlyDictionary<string, string?> settings,
    Action<IServiceCollection> services) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);

        // UseSetting, not ConfigureAppConfiguration: Program reads the scratch
        // path while it builds, before anything added that way would be seen.
        foreach (var (key, value) in settings)
            builder.UseSetting(key, value);

        builder.ConfigureTestServices(services);
    }
}
