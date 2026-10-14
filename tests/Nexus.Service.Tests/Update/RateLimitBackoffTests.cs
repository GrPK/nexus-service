using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Nexus.Service.Update;
using Xunit;

namespace Nexus.Service.Tests.Update;

public sealed class RateLimitBackoffTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static HttpResponseMessage Resp(HttpStatusCode code, string? remaining = null, long? reset = null, RetryConditionHeaderValue? retryAfter = null)
    {
        var r = new HttpResponseMessage(code);
        if (remaining is not null) r.Headers.Add("X-RateLimit-Remaining", remaining);
        if (reset is not null) r.Headers.Add("X-RateLimit-Reset", reset.Value.ToString());
        r.Headers.RetryAfter = retryAfter;
        return r;
    }

    [Fact]
    public void Forbidden_with_exhausted_quota_uses_reset_header()
    {
        var reset = Now.AddMinutes(25).ToUnixTimeSeconds();
        using var r = Resp(HttpStatusCode.Forbidden, "0", reset);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(reset), RateLimitBackoff.TryGetRetryTime(r, null, Now));
    }

    [Fact]
    public void Retry_after_wins_over_reset()
    {
        using var r = Resp(HttpStatusCode.TooManyRequests, "0", Now.AddMinutes(50).ToUnixTimeSeconds(), new RetryConditionHeaderValue(TimeSpan.FromMinutes(2)));
        Assert.Equal(Now.AddMinutes(2), RateLimitBackoff.TryGetRetryTime(r, null, Now));
    }

    [Fact]
    public void Forbidden_with_rate_limit_body_and_no_headers_uses_default()
    {
        using var r = Resp(HttpStatusCode.Forbidden);
        Assert.Equal(Now + RateLimitBackoff.DefaultBackoff, RateLimitBackoff.TryGetRetryTime(r, "API Rate Limit exceeded", Now));
    }

    [Fact]
    public void Plain_forbidden_is_not_a_rate_limit()
    {
        using var r = Resp(HttpStatusCode.Forbidden, "42");
        Assert.Null(RateLimitBackoff.TryGetRetryTime(r, "Resource not accessible", Now));
    }

    [Fact]
    public void Server_error_is_not_a_rate_limit()
    {
        using var r = Resp(HttpStatusCode.InternalServerError);
        Assert.Null(RateLimitBackoff.TryGetRetryTime(r, "rate limit", Now));
    }

    [Fact]
    public void Far_future_reset_is_clamped()
    {
        using var r = Resp(HttpStatusCode.TooManyRequests, "0", Now.AddDays(3).ToUnixTimeSeconds());
        Assert.Equal(Now + RateLimitBackoff.MaxBackoff, RateLimitBackoff.TryGetRetryTime(r, null, Now));
    }

    [Fact]
    public void Past_reset_is_clamped_to_the_floor()
    {
        using var r = Resp(HttpStatusCode.Forbidden, "0", Now.AddHours(-2).ToUnixTimeSeconds());
        Assert.Equal(Now + RateLimitBackoff.MinBackoff, RateLimitBackoff.TryGetRetryTime(r, null, Now));
    }
}
