using Microsoft.EntityFrameworkCore;
using Wmsfo.Api.Config;
using Wmsfo.Api.Contracts;
using Wmsfo.Api.Data;
using Wmsfo.Api.Http;
using Wmsfo.Api.Objects;

if (args.Length > 0)
{
    switch (args[0])
    {
        case "export-openapi":
            var openapiPath = args.Length > 1 ? args[1] : "contracts/openapi.json";
            await OpenApiExport.WriteAsync(openapiPath);
            return;
        case "export-contracts":
            var root = args.Length > 1 ? args[1] : "contracts";
            Directory.CreateDirectory(root);
            SchemaExport.WriteAll(Path.Combine(root, "schema"));
            FixtureExport.WriteAll(Path.Combine(root, "fixtures"));
            // Starter content mirrors the content-document fixture byte-for-byte; the seed loader reads this file.
            await File.WriteAllBytesAsync(
                Path.Combine(root, "starter-content.json"),
                CanonicalJson.SerializeToUtf8Bytes(FixtureData.BuildContentDocument()));
            AdminThresholds.WriteTo(Path.Combine(root, "admin-thresholds.json"));
            await OpenApiExport.WriteAsync(Path.Combine(root, "openapi.json"));
            return;
    }
}

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables();

// api.md 3 step 1: read configuration and fail fast.
var options = WmsfoOptions.Bind(builder.Configuration);
var validation = new WmsfoOptionsValidator().Validate(null, options);
if (validation.Failed)
{
    Console.Error.WriteLine(validation.FailureMessage);
    Environment.Exit(2);
    return;
}
Console.WriteLine($"wmsfo boot: env={options.Env} service={options.ServiceName} region={options.AwsRegion} cdn={options.CdnBaseUrl} api={options.PublicApiBaseUrl} hub={options.HubUrl}");

// api.md 3 step 2: build the two connection strings and register the contexts.
var connections = WmsfoConnectionStrings.Build(options);
builder.Services.AddSingleton(connections);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<WmsfoReadinessGate>();
builder.Services.AddDbContextFactory<WmsfoDbContext>(o => o
    .UseNpgsql(connections.Migrate)
    .UseSnakeCaseNamingConvention());
builder.Services.AddDbContext<WmsfoDbContext>(o => o
    .UseNpgsql(connections.App)
    .UseSnakeCaseNamingConvention());

// api.md 3 step 5 hook: the migrator plus the noop for the first-boot steps of
// sql.md 8.16 (starter content, icon library, content version 1, snapshot v1),
// which land in later tasks.
builder.Services.AddSingleton<IFirstBootHook, NoOpFirstBootHook>();
builder.Services.AddScoped<DatabaseMigrator>(sp => new DatabaseMigrator(
    connections.Migrate,
    sp.GetRequiredService<IDbContextFactory<WmsfoDbContext>>(),
    sp.GetRequiredService<IFirstBootHook>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<DatabaseMigrator>()));
builder.Services.AddHostedService<MigrationHostedService>();

// api.md 5 pipeline services: forwarded headers, CORS, auth, rate limits.
builder.Services.ConfigureForwardedHeaders(options.TrustedProxyHops);
builder.Services.AddWmsfoCors(options);
builder.Services.AddWmsfoAuth(options);
builder.Services.ConfigureRateLimits();

builder.Services.AddOpenApi();

var app = builder.Build();
var readiness = app.Services.GetRequiredService<WmsfoReadinessGate>();

// api.md 5 step 1: readiness middleware answers 503 to every non-health request
// until the migration completes.
app.Use(async (context, next) =>
{
    if (!readiness.IsReady && context.Request.Path != "/api/health")
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        var body = Wmsfo.Api.Http.ExceptionHandlingMiddleware.SerializeError(
            Wmsfo.Api.Http.ApiErrorCodes.Unavailable, "service starting", null, context.TraceIdentifier);
        await context.Response.Body.WriteAsync(body);
        return;
    }
    await next();
});

// api.md 5 steps 2-8: forwarded headers, request id, exception handler, no-store,
// body limits, routing, CORS, both-headers guard, auth, rate limiting.
app.UseWmsfoPipeline();

// api.md 17: /api/health answers 503 until ready, then `select 1` with a 2 s timeout.
app.MapGet("/api/health", async (WmsfoConnectionStrings cs, WmsfoReadinessGate gate, CancellationToken ct) =>
{
    if (!gate.IsReady)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
    try
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(2));
        await using var conn = new Npgsql.NpgsqlConnection(cs.App);
        await conn.OpenAsync(cts.Token);
        await using var command = new Npgsql.NpgsqlCommand("select 1", conn) { CommandTimeout = 2 };
        _ = await command.ExecuteScalarAsync(cts.Token);
        return Results.Ok(new { status = "ok" });
    }
    catch
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
})
.DisableRateLimiting();

EndpointStubs.MapAll(app);
app.MapOpenApi();

app.Run();

public partial class Program;
