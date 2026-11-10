using DbnClient.Archive;
using Serilog;

namespace DbnSpool;

/// <summary>Written out in full so it stays internal: a top-level Program is public, and the test projects also see DbnSync's.</summary>
internal static class Program
{
    private static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Serilog writes the console; the default console provider would print every line twice.
        builder.Logging.ClearProviders();

        builder.AddServiceDefaults();
        builder.AddAlertMail();

        builder.Services.AddSerilog((services, logger) =>
        {
            logger.ReadFrom.Configuration(builder.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext()
                .WriteTo.Console();

            if (builder.Configuration.GetConnectionString("seq") is { Length: > 0 } seq)
                logger.WriteTo.Seq(seq);
        }, writeToProviders: true);

        var options = builder.Configuration.GetSection(SpoolOptions.Section).Get<SpoolOptions>() ?? new SpoolOptions();
        var source = Required(builder.Configuration, SpoolOptions.ArchiveSection);
        var tapes = Required(builder.Configuration, SpoolOptions.TapesSection);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(DbnArchive.Open(source, options.CacheFolder));
        builder.Services.AddSingleton<ISpoolEngine, SpoolEngine>();
        builder.Services.AddSingleton<ITapeShelf>(tapes.IsFolder ? new FolderTapeShelf(tapes.Location) : new S3TapeShelf(tapes));
        builder.Services.AddSingleton<SpoolPass>();
        builder.Services.AddSingleton<SpoolStatus>();
        builder.Services.AddHostedService<SpoolWorker>();

        var app = builder.Build();

        app.MapDefaultEndpoints();

        // The template maps health only in Development; a container needs it everywhere.
        if (!app.Environment.IsDevelopment())
            app.MapHealthChecks("/health");

        app.MapGet("/status", (SpoolStatus status) => TypedResults.Ok(status.Snapshot));

        app.Run();
    }

    private static ArchiveOptions Required(IConfiguration configuration, string section) =>
        configuration.GetSection(section).Get<ArchiveOptions>() is { Location.Length: > 0 } found
            ? found
            : throw new InvalidOperationException($"{section}:Location is not set: an S3 address or a folder.");
}
