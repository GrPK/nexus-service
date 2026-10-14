using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;

namespace Nexus.Service.Update;

/// <summary>Thrown when a release API call is refused by a rate limit. <see cref="Until"/> is when to retry.</summary>
public sealed class RateLimitedException : HttpRequestException
{
    public DateTimeOffset Until { get; }

    public RateLimitedException(DateTimeOffset until)
        : base($"Rate limited; retry after {until.ToUniversalTime():u}")
    {
        Until = until;
    }
}

/// <summary>Rate-limit decisions for the update check.</summary>
public static class RateLimitBackoff
{
    public static readonly TimeSpan DefaultBackoff = TimeSpan.FromHours(1);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(6);
    public static readonly TimeSpan MinBackoff = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Retry time when the response is a rate-limit refusal, else null. 429 always counts; 403 counts
    /// with an exhausted quota header, a Retry-After header, or "rate limit" in the body.
    /// Retry-After wins, then X-RateLimit-Reset (unix seconds), else <see cref="DefaultBackoff"/>, clamped to [<see cref="MinBackoff"/>, <see cref="MaxBackoff"/>] so a skewed clock
    /// cannot yield a past time.
    /// </summary>
    public static DateTimeOffset? TryGetRetryTime(HttpResponseMessage resp, string? body, DateTimeOffset now)
    {
        var headers = resp.Headers;
        var remaining = Header(resp, "X-RateLimit-Remaining");
        var reset = Header(resp, "X-RateLimit-Reset");
        var retryAfter = headers.RetryAfter;

        var limited = resp.StatusCode == HttpStatusCode.TooManyRequests
            || (resp.StatusCode == HttpStatusCode.Forbidden
                && (remaining == "0"
                    || retryAfter is not null
                    || (body?.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ?? false)));
        if (!limited)
        {
            return null;
        }

        DateTimeOffset until = now + DefaultBackoff;
        if (retryAfter?.Delta is { } delta)
        {
            until = now + delta;
        }
        else if (retryAfter?.Date is { } date)
        {
            until = date;
        }
        else if (long.TryParse(reset, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix))
        {
            until = DateTimeOffset.FromUnixTimeSeconds(unix);
        }

        var min = now + MinBackoff;
        var max = now + MaxBackoff;
        return until < min ? min : (until > max ? max : until);
    }

    private static string? Header(HttpResponseMessage resp, string name) =>
        resp.Headers.TryGetValues(name, out var v) ? v.FirstOrDefault() : null;
}
