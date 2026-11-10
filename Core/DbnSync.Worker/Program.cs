using DbnSync.Core;
using DbnSync.Data;
using DbnSync.Worker;
using DbnSync.Worker.Components;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Serilog writes the console; the default console provider would print every
// line twice. AddServiceDefaults then adds OpenTelemetry back for the dashboard.
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

builder.Services.AddSingleton<IValidateOptions<SyncOptions>, SyncOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<StoreOptions>, StoreOptionsValidator>();

builder.Services.AddOptions<SyncOptions>()
    .Bind(builder.Configuration.GetSection(SyncOptions.Section))
    .ValidateOnStart();

builder.Services.AddOptions<StoreOptions>()
    .Bind(builder.Configuration.GetSection(StoreOptions.Section))
    .ValidateOnStart();

builder.Services.AddOptions<DatabentoOptions>()
    .Bind(builder.Configuration.GetSection(DatabentoOptions.Section))
    .PostConfigure(o =>
    {
        if (string.IsNullOrWhiteSpace(o.ApiKey))
            o.ApiKey = builder.Configuration[DatabentoOptions.KeyVariable] ?? "";
    })
    .Validate(o => !string.IsNullOrWhiteSpace(o.ApiKey),
        $"No Databento key: set Databento:ApiKey or {DatabentoOptions.KeyVariable}.")
    .ValidateOnStart();

builder.Services.AddOptions<DashboardOptions>()
    .Bind(builder.Configuration.GetSection(DashboardOptions.Section))
    .Validate(o => string.IsNullOrWhiteSpace(o.LogsUrl)
            || Uri.TryCreate(o.LogsUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https",
        "Dashboard:LogsUrl must be an absolute http(s) address.")
    .ValidateOnStart();

builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<SyncOptions>>().Value);
builder.Services.AddSingleton(TimeProvider.System);

// A download is a file of up to a few GB, not a request: no resilience
// pipeline with its 30-second total timeout, and a failed file is retried by
// the next pass rather than inside this one. The removal API is still marked
// experimental, and is the supported way to undo AddServiceDefaults for one client.
#pragma warning disable EXTEXP0001
builder.Services.AddHttpClient<IDatabento, DatabentoClient>(http => http.Timeout = TimeSpan.FromHours(2))
    .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

builder.Services.AddSingleton(sp =>
    S3ArchiveStore.CreateClient(sp.GetRequiredService<IOptions<StoreOptions>>().Value));
builder.Services.AddSingleton<S3ArchiveStore>();
builder.Services.AddSingleton<IArchiveStore>(sp => sp.GetRequiredService<S3ArchiveStore>());

builder.Services.AddScoped<SyncRunner>();
builder.Services.AddSingleton<SyncStatus>();
builder.Services.AddSingleton<CorpusIndex>();
builder.Services.AddHostedService<SyncWorker>();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// Antiforgery keys outlive the container on the scratch volume, so a restart
// does not invalidate every open dashboard. Jobs only ever delete their own folders.
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(
    builder.Configuration[$"{SyncOptions.Section}:{nameof(SyncOptions.ScratchPath)}"]
        ?? new SyncOptions().ScratchPath, ".keys")));

var app = builder.Build();

app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.MapDefaultEndpoints();

// The template maps health only in Development; a container needs it everywhere.
if (!app.Environment.IsDevelopment())
    app.MapHealthChecks("/health");

app.MapGet("/status", (SyncStatus status) => TypedResults.Ok(status.Snapshot));

app.Run();
