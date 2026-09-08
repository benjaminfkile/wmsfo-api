using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wmsfo.Api.Contracts;

namespace Wmsfo.Api.IntegrationTests;

// Acceptance criterion 688: no endpoint does more than return 501.
// Spins up an in-process host with the stubs mapped and sanity-checks a handful.
public sealed class EndpointStubTests : IAsyncLifetime
{
    private WebApplication? _app;
    private HttpClient? _http;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(EndpointStubTests).Assembly.GetName().Name,
        });
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddOpenApi();
        _app = builder.Build();
        EndpointStubs.MapAll(_app);
        await _app.StartAsync();

        var server = _app.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>()!;
        var url = addresses.Addresses.First().TrimEnd('/');
        _http = new HttpClient { BaseAddress = new Uri(url) };
    }

    public async Task DisposeAsync()
    {
        _http?.Dispose();
        if (_app is not null) await _app.DisposeAsync();
    }

    [Theory]
    [InlineData("GET",  "/api/health")]
    [InlineData("POST", "/beacons/enroll")]
    [InlineData("GET",  "/beacons/me")]
    [InlineData("POST", "/locations")]
    [InlineData("POST", "/beacons/heartbeat")]
    [InlineData("POST", "/contact")]
    [InlineData("GET",  "/me")]
    [InlineData("POST", "/cookies")]
    [InlineData("GET",  "/admin/events")]
    [InlineData("POST", "/admin/events")]
    [InlineData("GET",  "/admin/routes")]
    [InlineData("GET",  "/admin/beacons")]
    [InlineData("GET",  "/admin/sponsors")]
    [InlineData("GET",  "/admin/cookie-types")]
    [InlineData("GET",  "/admin/pages")]
    [InlineData("GET",  "/admin/content/kinds")]
    [InlineData("GET",  "/admin/site-settings")]
    [InlineData("GET",  "/admin/content/status")]
    [InlineData("GET",  "/admin/media")]
    [InlineData("GET",  "/admin/icons")]
    [InlineData("GET",  "/admin/settings")]
    [InlineData("GET",  "/admin/contact-messages")]
    [InlineData("GET",  "/admin/subscribers")]
    [InlineData("GET",  "/admin/people")]
    [InlineData("GET",  "/admin/snapshot")]
    [InlineData("GET",  "/admin/live")]
    [InlineData("POST", "/admin/live/republish")]
    [InlineData("POST", "/realtime/authorize")]
    [InlineData("POST", "/realtime/message")]
    public async Task Every_endpoint_returns_501(string method, string path)
    {
        Assert.NotNull(_http);
        var req = new HttpRequestMessage(new HttpMethod(method), path)
        {
            // Route parameter placeholders may exist; only test path-parameter-free routes above.
            Content = method is "POST" or "PUT" or "PATCH"
                ? new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
                : null,
        };
        var res = await _http!.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotImplemented, res.StatusCode);
    }
}
