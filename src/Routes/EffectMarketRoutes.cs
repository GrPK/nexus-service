using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Cloud;
using Nexus.Service.Models;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

/// <summary>
/// Dashboard-only passthrough to the cloud effects marketplace. The service
/// never parses a recipe: the cloud bounds its shape and the web compiles it,
/// so every response the cloud sends is relayed with its status and body.
/// </summary>
public static class EffectMarketRoutes
{
    public const int MaxBodyBytes = 32 * 1024;
    private static readonly string[] ListQueryKeys = { "sort", "author", "q", "page", "limit" };

    public static void Map(WebApplication app)
    {
        app.MapGet("/lighting/effect-market", (HttpRequest req, ICloudApiClient api, CancellationToken ct) =>
            Anonymous(api, "/effects" + ListQuery(req), ct));
        // The signed-in account's downloads; a literal segment, so it never reaches the {id} route.
        app.MapGet("/lighting/effect-market/downloads", (ICloudApiClient api, CloudAccountService accounts, CancellationToken ct) =>
            Account(api, accounts, HttpMethod.Get, "/effects/downloads", null, ct));
        app.MapGet("/lighting/effect-market/{id}", (string id, ICloudApiClient api, CancellationToken ct) =>
            WithId(id, guid => Anonymous(api, $"/effects/{guid}", ct)));
        app.MapPost("/lighting/effect-market", async (HttpRequest req, ICloudApiClient api, CloudAccountService accounts, CancellationToken ct) =>
        {
            var (body, tooLarge) = await ReadBodyAsync(req, ct).ConfigureAwait(false);
            return tooLarge ? TooLarge() : await Account(api, accounts, HttpMethod.Post, "/effects", body, ct).ConfigureAwait(false);
        });
        app.MapPut("/lighting/effect-market/{id}", async (string id, HttpRequest req, ICloudApiClient api, CloudAccountService accounts, CancellationToken ct) =>
        {
            if (!TryGuid(id, out var guid))
            {
                return BadId();
            }
            var (body, tooLarge) = await ReadBodyAsync(req, ct).ConfigureAwait(false);
            return tooLarge ? TooLarge() : await Account(api, accounts, HttpMethod.Put, $"/effects/{guid}", body, ct).ConfigureAwait(false);
        });
        app.MapDelete("/lighting/effect-market/{id}", (string id, ICloudApiClient api, CloudAccountService accounts, CancellationToken ct) =>
            WithId(id, guid => Account(api, accounts, HttpMethod.Delete, $"/effects/{guid}", null, ct)));
        app.MapPost("/lighting/effect-market/{id}/download", (string id, ICloudApiClient api, CloudAccountService accounts, CancellationToken ct) =>
            WithId(id, guid => Account(api, accounts, HttpMethod.Post, $"/effects/{guid}/download", "", ct)));
    }

    private static bool TryGuid(string id, out string guid)
    {
        guid = "";
        if (id.Length != 36 || !Guid.TryParseExact(id, "D", out var parsed))
        {
            return false;
        }
        guid = parsed.ToString("D");
        return true;
    }

    private static async Task<IResult> WithId(string id, Func<string, Task<IResult>> forward) =>
        TryGuid(id, out var guid) ? await forward(guid).ConfigureAwait(false) : BadId();

    /// <summary>Only the listing's own keys travel, each value escaped; anything else the caller sent is dropped.</summary>
    internal static string ListQuery(HttpRequest req)
    {
        var parts = new List<string>();
        foreach (var key in ListQueryKeys)
        {
            var value = req.Query[key].FirstOrDefault() ?? "";
            if (value.Length > 0)
            {
                parts.Add($"{key}={Uri.EscapeDataString(value)}");
            }
        }
        return parts.Count == 0 ? "" : "?" + string.Join('&', parts);
    }

    private static async Task<(string? Body, bool TooLarge)> ReadBodyAsync(HttpRequest req, CancellationToken ct)
    {
        if (req.ContentLength is long declared && declared > MaxBodyBytes)
        {
            return (null, true);
        }
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await req.Body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + read > MaxBodyBytes)
            {
                return (null, true);
            }
            ms.Write(buffer, 0, read);
        }
        return (Encoding.UTF8.GetString(ms.ToArray()), false);
    }

    private static async Task<IResult> Anonymous(ICloudApiClient api, string path, CancellationToken ct) =>
        Relay(await api.SendRawAsync(HttpMethod.Get, path, null, null, ct).ConfigureAwait(false), signInOn401: false);

    private static async Task<IResult> Account(
        ICloudApiClient api, CloudAccountService accounts, HttpMethod method, string path, string? body, CancellationToken ct)
    {
        var accountId = accounts.ActiveAccountId;
        if (string.IsNullOrEmpty(accountId))
        {
            return SignInRequired();
        }
        var result = await accounts.WithAuthAsync(
            accountId, token => api.SendRawAsync(method, path, body, token, ct), ct).ConfigureAwait(false);
        return Relay(result, signInOn401: true);
    }

    private static IResult Relay(CloudApiResult<CloudRawResponse> result, bool signInOn401)
    {
        if (result.Offline)
        {
            return Fail(StatusCodes.Status503ServiceUnavailable, "market_unavailable");
        }
        if (signInOn401 && result.StatusCode == StatusCodes.Status401Unauthorized)
        {
            return SignInRequired();
        }
        if (result.Value is not { } value)
        {
            return Fail(StatusCodes.Status503ServiceUnavailable, "market_unavailable");
        }
        if (value.Body.Length == 0)
        {
            return result.StatusCode >= 500
                ? Fail(StatusCodes.Status503ServiceUnavailable, "market_unavailable")
                : Results.StatusCode(result.StatusCode);
        }
        // Decided by the body, not the header: the client labels a reply with no Content-Type as JSON.
        // An edge or proxy page is not ours to relay onto the dashboard origin.
        if (!IsJson(value.Body))
        {
            return Fail(StatusCodes.Status503ServiceUnavailable, "market_unavailable");
        }
        return Results.Text(value.Body, "application/json", statusCode: result.StatusCode);
    }

    private static bool IsJson(string body)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static IResult Fail(int status, string msg) =>
        Results.Json(ApiResponse.Fail(msg), AppJsonContext.Default.ApiResponse, statusCode: status);

    // 403, not 401: the web treats any 401 from the local service as an expired service token and runs its refresh path.
    private static IResult SignInRequired() => Fail(StatusCodes.Status403Forbidden, "sign_in_required");

    private static IResult BadId() => Fail(StatusCodes.Status400BadRequest, "invalid_id");

    private static IResult TooLarge() => Fail(StatusCodes.Status413PayloadTooLarge, "body_too_large");
}
