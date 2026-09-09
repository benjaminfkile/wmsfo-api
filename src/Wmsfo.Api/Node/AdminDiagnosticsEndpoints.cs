using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wmsfo.Api.Contracts.Dtos;
using Wmsfo.Api.Data;
using Wmsfo.Api.Objects;
using Wmsfo.Api.Realtime;

namespace Wmsfo.Api.Node;

// api.md 10.3: the four diagnostics endpoints.
//   GET  /admin/snapshot            — SnapshotInfo (snapshot row)
//   POST /admin/snapshot/rebuild    — rebuild frame with no write
//   GET  /admin/live                — live_state row + this node's memory
//   POST /admin/live/republish      — WriteFromState("republish"); returns the object
// Auth policies are not enforced here yet — A5 wires Admin on top of these when
// the real handlers land; the routes are open for the tests to exercise them.
public static class AdminDiagnosticsEndpoints
{
    public static void MapAdminDiagnostics(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/snapshot", async (WmsfoDbContext db, CancellationToken ct) =>
        {
            var row = await db.Snapshot.FirstOrDefaultAsync(x => x.Id == 1, ct);
            if (row is null) return Results.NotFound();
            return Results.Ok(new SnapshotInfoDto
            {
                Version = row.Version,
                Url = row.Url,
                S3Key = row.S3Key,
                BuiltAt = row.BuiltAt,
            });
        }).WithTags("AdminDiagnostics");

        app.MapPost("/admin/snapshot/rebuild", async (SnapshotBuilder builder, LiveObjectWriter writer, CancellationToken ct) =>
        {
            var info = await builder.RebuildAsync(ct);
            _ = writer.WriteFromStateAsync("admin", ct);
            return Results.Ok(new SnapshotInfoDto
            {
                Version = info.Version,
                Url = info.Url,
                S3Key = info.Key,
                BuiltAt = info.BuiltAt,
            });
        }).WithTags("AdminDiagnostics");

        app.MapGet("/admin/live", async (WmsfoDbContext db, NodeStateService state, IGatewayInternalClient gateway, LiveObjectWriter writer, NodeCounters counters, CancellationToken ct) =>
        {
            var row = await db.LiveState.FirstOrDefaultAsync(x => x.Id == 1, ct);
            var live = writer.LastWrittenObject ?? BuildInMemoryObject(state, writer);
            var response = new AdminLiveResponse
            {
                LastWriteAt = row?.LastWriteAt,
                LastWriteSeq = row?.LastWriteSeq,
                LastWriteVersion = row?.LastWriteVersion,
                LastWriteError = row?.LastWriteError,
                LastWriteNode = row?.LastWriteNode,
                Node = new AdminLiveNode
                {
                    Instance = gateway.LastInstanceId,
                    IsLeader = state.Leader.IsCurrentlyLeader(DateTimeOffset.UtcNow),
                    LeaderEvaluatedAt = state.Leader.EvaluatedAt == DateTimeOffset.MinValue ? null : state.Leader.EvaluatedAt,
                    CacheRefreshedAt = state.Current.RefreshedAt,
                    Live = live,
                    Counters = counters.Snapshot(),
                },
            };
            return Results.Ok(response);
        }).WithTags("AdminDiagnostics");

        app.MapPost("/admin/live/republish", async (LiveObjectWriter writer, CancellationToken ct) =>
        {
            await writer.WriteFromStateAsync("republish", ct);
            return Results.Ok(writer.LastWrittenObject ?? new LiveObject());
        }).WithTags("AdminDiagnostics");
    }

    private static LiveObject BuildInMemoryObject(NodeStateService state, LiveObjectWriter writer)
    {
        var (obj, _) = writer.Build(state.Current, location: null);
        return obj;
    }
}
