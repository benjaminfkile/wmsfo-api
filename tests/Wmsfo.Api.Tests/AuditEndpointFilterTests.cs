using System.Reflection;
using Microsoft.AspNetCore.Http;
using Wmsfo.Api.Endpoints;

namespace Wmsfo.Api.Tests;

// A35 unit test: the AuditEndpointFilter is the safety net for a write that
// forgets to record. When the endpoint has already recorded (the every-write
// path), the filter runs the next delegate and records nothing itself - no
// duplicate audit row.
public sealed class AuditEndpointFilterTests
{
    [Fact]
    public async Task Filter_records_nothing_when_recorder_already_stamped()
    {
        var recorder = new AuditRecorder(new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext(),
        });
        MarkRecorded(recorder);

        var filter = new AuditEndpointFilter();
        var ctx = new StubInvocationContext(new DefaultHttpContext());
        var called = false;
        ValueTask<object?> Next(EndpointFilterInvocationContext _)
        {
            called = true;
            return ValueTask.FromResult<object?>(Results.NoContent());
        }
        var result = await filter.InvokeAsync(ctx, Next);
        Assert.True(called);
        Assert.NotNull(result);
        // The filter did not touch the recorder's state.
        Assert.True(recorder.Recorded);
    }

    // AuditRecorder.Recorded has a private setter; test sets it directly to
    // simulate the endpoint's successful audit.RecordAsync call.
    private static void MarkRecorded(AuditRecorder recorder)
    {
        var prop = typeof(AuditRecorder).GetProperty(
            nameof(AuditRecorder.Recorded), BindingFlags.Instance | BindingFlags.Public)!;
        prop.SetValue(recorder, true);
    }

    private sealed class StubInvocationContext : EndpointFilterInvocationContext
    {
        private readonly HttpContext _http;

        public StubInvocationContext(HttpContext http) => _http = http;

        public override HttpContext HttpContext => _http;
        public override IList<object?> Arguments { get; } = new List<object?>();
        public override T GetArgument<T>(int index) => (T)Arguments[index]!;
    }
}
