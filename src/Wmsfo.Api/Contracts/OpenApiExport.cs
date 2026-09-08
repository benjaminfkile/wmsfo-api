using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Wmsfo.Api.Contracts;

// api.md 21: the export mode builds the endpoint table and exits before configuration
// validation, so it needs no database, bucket, or secret. Called from `export-contracts`
// (and the deprecated `export-openapi` alias), it writes the OpenAPI document at
// `<contracts-root>/openapi.json`.
public static class OpenApiExport
{
    public static async Task WriteAsync(string outputPath, CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(OpenApiExport).Assembly.GetName().Name,
        });
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        // Bind to an ephemeral loopback port so nothing collides with a running instance.
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddOpenApi(o =>
        {
            // Fix the document so byte output is stable across runs; the port varies.
            o.AddDocumentTransformer((document, ctx, ct) =>
            {
                document.Servers = null;
                document.Info.Title = "WMSFO API";
                document.Info.Version = "1";
                return Task.CompletedTask;
            });
        });

        var app = builder.Build();
        EndpointStubs.MapHealthStub(app);
        EndpointStubs.MapAll(app);
        app.MapOpenApi();

        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var server = app.Services.GetRequiredService<IServer>();
            var addressesFeature = server.Features.Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("no server address");
            var baseUrl = addressesFeature.Addresses.First().TrimEnd('/');
            using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
            var json = await http.GetStringAsync("/openapi/v1.json", cancellationToken).ConfigureAwait(false);

            var full = Path.GetFullPath(outputPath);
            var directory = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(full, json, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await app.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
