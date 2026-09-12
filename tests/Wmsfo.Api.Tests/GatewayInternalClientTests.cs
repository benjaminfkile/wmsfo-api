using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Wmsfo.Api.Config;
using Wmsfo.Api.Realtime;

namespace Wmsfo.Api.Tests;

// api.md 12.4 / A27: GatewayInternalClient.LastInstanceId is set from every
// successful /internal/leader answer's `instanceId`. GET /admin/live surfaces
// it as `node.instance`.
public class GatewayInternalClientTests
{
    [Fact]
    public async Task LastInstanceId_updates_from_successful_leader_answer()
    {
        var handler = new StubHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"isLeader\":true,\"evaluatedAt\":\"2026-12-22T01:31:07Z\",\"instanceId\":\"i-abc123\"}",
                    Encoding.UTF8, "application/json"),
            });
        using var http = new HttpClient(handler);
        var client = new GatewayInternalClient(http, TestOptions.Build(), NullLogger<GatewayInternalClient>.Instance);

        Assert.Null(client.LastInstanceId);
        var answer = await client.GetLeaderAsync(CancellationToken.None);
        Assert.True(answer.IsLeader);
        Assert.Equal("i-abc123", answer.InstanceId);
        Assert.Equal("i-abc123", client.LastInstanceId);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;
        public StubHandler(HttpResponseMessage response) { _response = response; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_response);
    }

    private static class TestOptions
    {
        public static WmsfoOptions Build() => new()
        {
            Env = "dev",
            ServiceName = "wmsfo-api-test",
            DbConnection = "Host=localhost;Database=x;Username=x;SSL Mode=Require",
            DbMigrationConnection = "Host=localhost;Database=x;Username=x;SSL Mode=Require",
            AwsRegion = "us-east-2",
            S3Bucket = "wmsfo-test",
            CdnBaseUrl = "https://cdn.example.com",
            PublicApiBaseUrl = "https://api.example.com",
            SiteBaseUrl = "https://site.example.com",
            HubUrl = "wss://gateway.example.com/hub",
            GatewayInternalUrl = "http://127.0.0.1:9099",
            GatewayRealtimeToken = "test-token",
            CorsOrigins = "https://site.example.com",
            EnrollmentEncryptionKey = Convert.ToBase64String(new byte[32]),
            SesFromAddress = "alerts@example.com",
            ContactNotifyEmail = "inbox@example.com",
            CognitoIssuer = "https://cognito-idp.us-east-2.amazonaws.com/us-east-2_pool",
            CognitoClientIds = "site-client-id,admin-client-id",
            CognitoUserPoolId = "us-east-2_pool",
            AdminGroup = "admin",
            EditorGroup = "editor",
        };
    }
}
