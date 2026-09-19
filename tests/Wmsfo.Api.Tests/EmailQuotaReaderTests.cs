using System.Reflection;
using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Wmsfo.Api.Email;

namespace Wmsfo.Api.Tests;

// A43: unit tests for the SES account reader that GET /admin/email/quota
// stands on. Covers pass-through of the three SendQuota numbers, the -1
// contract (no limit), the 60 s cache for a good answer, the 10 s cache
// for an unavailable answer, and a SES failure returning "unavailable".
public sealed class EmailQuotaReaderTests
{
    [Fact]
    public async Task Read_returns_the_three_ses_numbers_when_available()
    {
        var fake = new FakeSes { NextResponse = Response(max: 50000, sent: 1000, rate: 14) };
        var clock = new ManualTime();
        var reader = new EmailQuotaReader(clock, NullLogger<EmailQuotaReader>.Instance, fake.AsClient());

        var reading = await reader.ReadAsync(default);

        Assert.True(reading.Available);
        Assert.Equal(50000d, reading.Max24HourSend);
        Assert.Equal(1000d, reading.SentLast24Hours);
        Assert.Equal(14d, reading.MaxSendRate);
        Assert.Equal(1, fake.CallCount);
    }

    [Fact]
    public async Task Read_maps_max_of_negative_one_to_null()
    {
        var fake = new FakeSes { NextResponse = Response(max: -1, sent: 42, rate: 1) };
        var clock = new ManualTime();
        var reader = new EmailQuotaReader(clock, NullLogger<EmailQuotaReader>.Instance, fake.AsClient());

        var reading = await reader.ReadAsync(default);

        Assert.True(reading.Available);
        Assert.Null(reading.Max24HourSend);
        Assert.Equal(42d, reading.SentLast24Hours);
        Assert.Equal(1d, reading.MaxSendRate);
    }

    [Fact]
    public async Task Second_read_inside_60_s_returns_the_cached_answer()
    {
        var fake = new FakeSes { NextResponse = Response(max: 100, sent: 10, rate: 5) };
        var clock = new ManualTime();
        var reader = new EmailQuotaReader(clock, NullLogger<EmailQuotaReader>.Instance, fake.AsClient());

        var first = await reader.ReadAsync(default);
        clock.Advance(TimeSpan.FromSeconds(59));
        var second = await reader.ReadAsync(default);

        Assert.Equal(1, fake.CallCount);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Read_past_60_s_refreshes_from_ses()
    {
        var fake = new FakeSes { NextResponse = Response(max: 100, sent: 10, rate: 5) };
        var clock = new ManualTime();
        var reader = new EmailQuotaReader(clock, NullLogger<EmailQuotaReader>.Instance, fake.AsClient());

        _ = await reader.ReadAsync(default);
        clock.Advance(TimeSpan.FromSeconds(61));
        fake.NextResponse = Response(max: 200, sent: 20, rate: 6);
        var second = await reader.ReadAsync(default);

        Assert.Equal(2, fake.CallCount);
        Assert.Equal(200d, second.Max24HourSend);
    }

    [Fact]
    public async Task Ses_exception_gives_unavailable_and_retries_after_10_s()
    {
        var fake = new FakeSes { NextException = new AmazonSimpleEmailServiceV2Exception("boom") };
        var clock = new ManualTime();
        var reader = new EmailQuotaReader(clock, NullLogger<EmailQuotaReader>.Instance, fake.AsClient());

        var first = await reader.ReadAsync(default);
        Assert.False(first.Available);
        Assert.Null(first.Max24HourSend);
        Assert.Null(first.SentLast24Hours);
        Assert.Null(first.MaxSendRate);
        Assert.Equal(1, fake.CallCount);

        // Still cached at 9 s.
        clock.Advance(TimeSpan.FromSeconds(9));
        _ = await reader.ReadAsync(default);
        Assert.Equal(1, fake.CallCount);

        // After 10 s the reader tries SES again; this time the call succeeds.
        clock.Advance(TimeSpan.FromSeconds(2));
        fake.NextException = null;
        fake.NextResponse = Response(max: 1000, sent: 100, rate: 10);
        var third = await reader.ReadAsync(default);
        Assert.True(third.Available);
        Assert.Equal(1000d, third.Max24HourSend);
        Assert.Equal(2, fake.CallCount);
    }

    [Fact]
    public async Task Reader_without_a_client_answers_unavailable_without_calling()
    {
        var reader = new EmailQuotaReader(new ManualTime(), NullLogger<EmailQuotaReader>.Instance, client: null);

        var reading = await reader.ReadAsync(default);

        Assert.False(reading.Available);
        Assert.Null(reading.Max24HourSend);
        Assert.Null(reading.SentLast24Hours);
        Assert.Null(reading.MaxSendRate);
    }

    private static GetAccountResponse Response(double max, double sent, double rate)
    {
        return new GetAccountResponse
        {
            SendQuota = new SendQuota
            {
                Max24HourSend = max,
                SentLast24Hours = sent,
                MaxSendRate = rate,
            },
        };
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }

    // The SES v2 interface is broad, so this fake uses DispatchProxy to answer
    // only the one method the reader calls. Every other method throws, which is
    // fine as the reader touches nothing else.
    public sealed class FakeSes
    {
        public int CallCount { get; private set; }
        public GetAccountResponse? NextResponse { get; set; }
        public Exception? NextException { get; set; }

        public IAmazonSimpleEmailServiceV2 AsClient()
        {
            var proxy = DispatchProxy.Create<IAmazonSimpleEmailServiceV2, DispatchStub>();
            ((DispatchStub)(object)proxy).Owner = this;
            return proxy;
        }

        internal Task<GetAccountResponse> InvokeGetAccountAsync()
        {
            CallCount++;
            if (NextException is not null)
            {
                return Task.FromException<GetAccountResponse>(NextException);
            }
            return Task.FromResult(NextResponse ?? new GetAccountResponse());
        }
    }

    public class DispatchStub : DispatchProxy
    {
        internal FakeSes? Owner;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null) return null;
            if (targetMethod.Name == nameof(IAmazonSimpleEmailServiceV2.GetAccountAsync))
            {
                return Owner!.InvokeGetAccountAsync();
            }
            if (targetMethod.Name == "Dispose") return null;
            throw new NotImplementedException(targetMethod.Name);
        }
    }
}
