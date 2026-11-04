var builder = DistributedApplication.CreateBuilder(args);

// dotnet user-secrets set "Parameters:databento-api-key" "<key>" --project Aspire/AppHost
var databentoKey = builder.AddParameter("databento-api-key", secret: true);

var s3User = builder.AddParameter("s3-access-key", "dbnsync");
var s3Secret = builder.AddParameter("s3-secret-key",
    new GenerateParameterDefault { MinLength = 24, Special = false }, secret: true, persist: true);

// Any S3 server will do; DbnSync speaks only the S3 API. RustFS because
// MinIO no longer publishes public images. Development pulls only what
// appsettings.Development.json names, so the volume stays small.
var s3 = builder.AddContainer("s3", "rustfs/rustfs", "1.0.4")
    .WithEnvironment("RUSTFS_ACCESS_KEY", s3User)
    .WithEnvironment("RUSTFS_SECRET_KEY", s3Secret)
    .WithEnvironment("RUSTFS_CONSOLE_ENABLE", "true")
    .WithHttpEndpoint(targetPort: 9000, name: "s3")
    .WithHttpEndpoint(targetPort: 9001, name: "console")
    .WithHttpHealthCheck("/health", endpointName: "s3")
    .WithVolume("dbnsync-s3", "/data");

var seq = builder.AddSeq("seq").WithDataVolume();

builder.AddProject<Projects.DbnSync_Worker>("dbnsync")
    .WithEnvironment("Databento__ApiKey", databentoKey)
    .WithEnvironment("Store__ServiceUrl", s3.GetEndpoint("s3"))
    .WithEnvironment("Store__AccessKey", s3User)
    .WithEnvironment("Store__SecretKey", s3Secret)
    .WithEnvironment("Dashboard__LogsUrl", seq.GetEndpoint("http"))
    .WithReference(seq)
    .WaitFor(s3)
    .WaitFor(seq);

builder.Build().Run();
