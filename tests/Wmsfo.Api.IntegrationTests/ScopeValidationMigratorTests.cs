using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Wmsfo.Api.Config;
using Wmsfo.Api.Content;
using Wmsfo.Api.Data;
using Wmsfo.Api.Http;
using Wmsfo.Api.Icons;
using Wmsfo.Api.Node;
using Wmsfo.Api.Objects;
using Wmsfo.Api.Realtime;

namespace Wmsfo.Api.IntegrationTests;

// The Development host validates scopes and validates the container on build.
// This host registers the contexts and the migrator the way Program.cs does,
// with both checks on, and runs the migration hosted service against the test
// database until the readiness gate opens.
public sealed class ScopeValidationMigratorTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public ScopeValidationMigratorTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Migrator_and_first_boot_hook_run_with_scope_validation_on()
    {
        var storeRoot = Path.Combine(Path.GetTempPath(), "wmsfo-scopes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storeRoot);
        try
        {
            var options = new WmsfoOptions
            {
                Env = "dev",
                ServiceName = "wmsfo-api-test",
                DbConnection = _fixture.AppConnectionString,
                DbMigrationConnection = _fixture.MigrateConnectionString,
                CdnBaseUrl = "https://cdn.example",
                PublicApiBaseUrl = "https://api.example.com",
                SiteBaseUrl = "https://site.example.com",
                GatewayInternalUrl = "http://127.0.0.1:1",
                ObjectStoreDir = storeRoot,
            };
            var connections = TestConnections.For(_fixture.ConnectionString);

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(ScopeValidationMigratorTests).Assembly.GetName().Name,
            });
            builder.Host.UseDefaultServiceProvider(o =>
            {
                o.ValidateScopes = true;
                o.ValidateOnBuild = true;
            });
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);

            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton(connections);
            builder.Services.AddSingleton<WmsfoReadinessGate>();
            builder.Services.AddWmsfoDbContexts(connections.App);
            builder.Services.AddSingleton<IObjectStore>(new LocalObjectStore(storeRoot, options.PublicApiBaseUrl));
            builder.Services.AddSingleton<IGatewayInternalClient>(new FakeGatewayClient());
            builder.Services.AddSingleton<NodeStateService>();
            builder.Services.AddSingleton<NodeCounters>();
            builder.Services.AddSingleton<IServerClock, SystemServerClock>();
            builder.Services.AddSingleton(IconLibrary.Load(TestPaths.IconsDir, options.CdnBaseUrl));
            builder.Services.AddSingleton(SharedContent.Registry);
            builder.Services.AddSingleton(SharedContent.Validator);
            builder.Services.AddSingleton<DocumentBuilder>();
            builder.Services.AddSingleton<SnapshotBuilder>();
            builder.Services.AddSingleton<LiveObjectWriter>();
            builder.Services.AddSingleton<StarterContent>();
            builder.Services.AddSingleton<Publisher>();
            builder.Services.AddSingleton<SnapshotBootstrap>();
            builder.Services.AddWmsfoMigrator(connections.Migrate);

            await using var app = builder.Build();
            var gate = app.Services.GetRequiredService<WmsfoReadinessGate>();
            await app.StartAsync();
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(60);
                while (!gate.IsReady && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(100);
                }
                Assert.True(gate.IsReady, "the migrator did not open the readiness gate");
            }
            finally
            {
                await app.StopAsync();
            }

            await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
            await conn.OpenAsync();
            await using var command = new NpgsqlCommand("select count(*) from snapshot", conn);
            Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
        }
        finally
        {
            Directory.Delete(storeRoot, recursive: true);
        }
    }
}
