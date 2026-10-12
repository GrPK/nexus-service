using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Cloud;
using Nexus.Service.Models.Cloud;
using Nexus.Service.Persistence;
using Nexus.Service.Tests.Cloud;

namespace Nexus.Service.Tests.Integration;

public sealed class EffectMarketAppFactory : NexusAppFactory
{
    public FakeCloudApiClient Api { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICloudApiClient>();
            services.AddSingleton<ICloudApiClient>(Api);
        });
    }
}

public sealed class EffectMarketRoutesTests : IClassFixture<EffectMarketAppFactory>
{
    private const string Guid1 = "3f2504e0-4f89-41d3-9a0c-0305e82c3301";
    private readonly EffectMarketAppFactory _factory;

    public EffectMarketRoutesTests(EffectMarketAppFactory factory)
    {
        _factory = factory;
        _factory.Api.OnSendRaw = (_, _, body, _) => CloudApiResult<CloudRawResponse>.Ok(new CloudRawResponse { Body = body ?? "" });
        SignOut();
    }

    private HttpClient Desktop()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    private void SignOut() =>
        _factory.Services.GetRequiredService<IConfigStore>().Update(s =>
        {
            s.Auth ??= new AuthSettings();
            s.Auth.ActiveCloudAccountId = null;
        });

    private void SignIn(string accountId = "acct-1", string accessToken = "tok-1")
    {
        _factory.Services.GetRequiredService<IConfigStore>().Update(s =>
        {
            s.Auth ??= new AuthSettings();
            if (!s.Auth.CloudAccounts.Any(a => a.AccountId == accountId))
            {
                s.Auth.CloudAccounts.Add(new CloudAccountRecord { AccountId = accountId, RefreshToken = "refresh-" + accountId });
            }
            s.Auth.ActiveCloudAccountId = accountId;
        });
        _factory.Api.OnRefresh = _ => CloudApiResult<CloudAuthSession>.Ok(new CloudAuthSession
        {
            AccessToken = accessToken,
            RefreshToken = "refresh-" + accountId,
            Account = new CloudAccountDto { Id = accountId, Email = "x@example.com", Username = "x", EmailVerified = true },
        });
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> Read(HttpResponseMessage res)
    {
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task List_forwards_only_allowlisted_keys_escaped_and_anonymous()
    {
        string? path = null;
        string? token = "unset";
        HttpMethod? method = null;
        _factory.Api.OnSendRaw = (m, p, _, t) =>
        {
            method = m;
            path = p;
            token = t;
            return CloudApiResult<CloudRawResponse>.Ok(new CloudRawResponse { Body = "{\"items\":[]}" });
        };
        SignIn();

        var res = await Desktop().GetAsync("/lighting/effect-market?q=neon%20%26lights%3Dx&sort=popular&evil=1&author=bob_1&page=2&limit=10&Sort=new");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(HttpMethod.Get, method);
        Assert.Equal("/effects?sort=popular&author=bob_1&q=neon%20%26lights%3Dx&page=2&limit=10", path);
        Assert.Null(token);
        Assert.Equal("{\"items\":[]}", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task List_without_a_query_forwards_a_bare_path()
    {
        string? path = null;
        _factory.Api.OnSendRaw = (_, p, _, _) =>
        {
            path = p;
            return CloudApiResult<CloudRawResponse>.Ok(new CloudRawResponse { Body = "{}" });
        };

        await Desktop().GetAsync("/lighting/effect-market");

        Assert.Equal("/effects", path);
    }

    [Fact]
    public async Task Get_one_forwards_the_guid()
    {
        string? path = null;
        _factory.Api.OnSendRaw = (_, p, _, _) =>
        {
            path = p;
            return CloudApiResult<CloudRawResponse>.Ok(new CloudRawResponse { Body = "{\"id\":\"x\"}" });
        };

        var res = await Desktop().GetAsync("/lighting/effect-market/" + Guid1);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("/effects/" + Guid1, path);
    }

    [Theory]
    [InlineData("GET", "nope")]
    [InlineData("GET", "3f2504e04f8941d39a0c0305e82c3301")]
    [InlineData("GET", "..%2Faccount")]
    [InlineData("PUT", "nope")]
    [InlineData("DELETE", "nope")]
    [InlineData("POST", "nope/download")]
    public async Task A_non_guid_id_is_400_and_never_reaches_the_cloud(string method, string idPart)
    {
        SignIn();
        var calls = _factory.Api.SendRawCalls;

        var req = new HttpRequestMessage(new HttpMethod(method), "/lighting/effect-market/" + idPart);
        if (method == "PUT") req.Content = Json("{}");
        var res = await Desktop().SendAsync(req);

        Assert.True(res.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound);
        Assert.Equal(calls, _factory.Api.SendRawCalls);
        if (res.StatusCode == HttpStatusCode.BadRequest)
        {
            Assert.True((await Read(res)).GetProperty("error").GetBoolean());
        }
    }

    [Fact]
    public async Task Post_uses_the_account_token_and_relays_status_and_body()
    {
        string? token = null;
        HttpMethod? method = null;
        string? path = null;
        string? body = null;
        _factory.Api.OnSendRaw = (m, p, b, t) =>
        {
            method = m;
            path = p;
            body = b;
            token = t;
            return CloudApiResult<CloudRawResponse>.Ok(new CloudRawResponse { Body = "{\"id\":\"new\"}" }, 201);
        };
        SignIn("acct-2", "tok-2");

        var res = await Desktop().PostAsync("/lighting/effect-market", Json("{\"name\":\"n\",\"recipe\":{}}"));

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        Assert.Equal("{\"id\":\"new\"}", await res.Content.ReadAsStringAsync());
        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("/effects", path);
        Assert.Equal("tok-2", token);
        Assert.Equal("{\"name\":\"n\",\"recipe\":{}}", body);
    }

    [Fact]
    public async Task Put_delete_and_download_map_to_the_cloud_routes()
    {
        var seen = new List<(HttpMethod, string, string?)>();
        _factory.Api.OnSendRaw = (m, p, b, _) =>
        {
            seen.Add((m, p, b));
            return CloudApiResult<CloudRawResponse>.Ok(new CloudRawResponse { Body = m == HttpMethod.Delete ? "" : "{}" }, m == HttpMethod.Delete ? 204 : 200);
        };
        SignIn();
        var http = Desktop();

        var put = await http.PutAsync("/lighting/effect-market/" + Guid1, Json("{\"name\":\"n\"}"));
        var del = await http.DeleteAsync("/lighting/effect-market/" + Guid1);
        var dl = await http.PostAsync($"/lighting/effect-market/{Guid1}/download", null);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        Assert.Equal(HttpStatusCode.OK, dl.StatusCode);
        Assert.Equal((HttpMethod.Put, "/effects/" + Guid1, "{\"name\":\"n\"}"), seen[0]);
        Assert.Equal((HttpMethod.Delete, "/effects/" + Guid1, null), seen[1]);
        Assert.Equal(HttpMethod.Post, seen[2].Item1);
        Assert.Equal($"/effects/{Guid1}/download", seen[2].Item2);
    }

    [Fact]
    public async Task Writes_without_a_linked_account_are_403_sign_in_required_and_not_forwarded()
    {
        SignOut();
        var calls = _factory.Api.SendRawCalls;
        var http = Desktop();

        var post = await http.PostAsync("/lighting/effect-market", Json("{}"));
        var del = await http.DeleteAsync("/lighting/effect-market/" + Guid1);

        foreach (var res in new[] { post, del })
        {
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
            var json = await Read(res);
            Assert.True(json.GetProperty("error").GetBoolean());
            Assert.Equal("sign_in_required", json.GetProperty("msg").GetString());
        }
        Assert.Equal(calls, _factory.Api.SendRawCalls);
    }

    [Fact]
    public async Task A_cloud_401_on_a_write_is_403_sign_in_required()
    {
        _factory.Api.OnSendRaw = (_, _, _, _) => CloudApiResult<CloudRawResponse>.Ok(
            new CloudRawResponse { Body = "{\"statusCode\":401,\"message\":\"Unauthorized\"}" }, 401);
        SignIn();

        var res = await Desktop().PostAsync("/lighting/effect-market", Json("{}"));

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal("sign_in_required", (await Read(res)).GetProperty("msg").GetString());
    }

    [Fact]
    public async Task An_unreachable_cloud_during_token_refresh_is_503_not_sign_in_required()
    {
        SignIn();
        _factory.Api.OnRefresh = _ => CloudApiResult<CloudAuthSession>.NetworkError("down");
        // A different account id has no cached access token, forcing a refresh.
        _factory.Services.GetRequiredService<IConfigStore>().Update(s =>
        {
            s.Auth!.CloudAccounts.Add(new CloudAccountRecord { AccountId = "acct-cold", RefreshToken = "r" });
            s.Auth.ActiveCloudAccountId = "acct-cold";
        });

        var res = await Desktop().PostAsync("/lighting/effect-market", Json("{}"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        Assert.Equal("market_unavailable", (await Read(res)).GetProperty("msg").GetString());
    }

    [Fact]
    public async Task A_refused_token_refresh_is_403_sign_in_required()
    {
        SignIn();
        _factory.Api.OnRefresh = _ => CloudApiResult<CloudAuthSession>.Fail(401, "revoked", "revoked");
        _factory.Services.GetRequiredService<IConfigStore>().Update(s =>
        {
            s.Auth!.CloudAccounts.Add(new CloudAccountRecord { AccountId = "acct-cold2", RefreshToken = "r" });
            s.Auth.ActiveCloudAccountId = "acct-cold2";
        });

        var res = await Desktop().PostAsync("/lighting/effect-market", Json("{}"));

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal("sign_in_required", (await Read(res)).GetProperty("msg").GetString());
    }

    [Theory]
    [InlineData(502, "text/html", "<html>bad gateway</html>")]
    [InlineData(502, "application/json", "<html>bad gateway</html>")]
    [InlineData(403, "application/json", "<html>edge block</html>")]
    [InlineData(429, "application/json", "Too many requests")]
    [InlineData(503, "application/json", "")]
    public async Task A_non_json_or_empty_cloud_5xx_is_503_market_unavailable(int status, string contentType, string body)
    {
        _factory.Api.OnSendRaw = (_, _, _, _) => CloudApiResult<CloudRawResponse>.Ok(
            new CloudRawResponse { Body = body, ContentType = contentType }, status);

        var res = await Desktop().GetAsync("/lighting/effect-market");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        Assert.Equal("market_unavailable", (await Read(res)).GetProperty("msg").GetString());
    }

    [Fact]
    public async Task A_json_cloud_5xx_with_a_body_is_relayed_unchanged()
    {
        const string body = "{\"statusCode\":500,\"message\":\"boom\"}";
        _factory.Api.OnSendRaw = (_, _, _, _) => CloudApiResult<CloudRawResponse>.Ok(
            new CloudRawResponse { Body = body, ContentType = "application/json" }, 500);

        var res = await Desktop().GetAsync("/lighting/effect-market");

        Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        Assert.Equal(body, await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_json_body_labeled_text_html_is_relayed_as_application_json()
    {
        _factory.Api.OnSendRaw = (_, _, _, _) => CloudApiResult<CloudRawResponse>.Ok(
            new CloudRawResponse { Body = "{\"message\":\"x\"}", ContentType = "text/html" }, 404);

        var res = await Desktop().GetAsync("/lighting/effect-market/" + Guid1);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal("application/json", res.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task A_cloud_403_passes_through_unchanged()
    {
        const string body = "{\"statusCode\":403,\"message\":\"listing_limit\",\"error\":\"Forbidden\"}";
        _factory.Api.OnSendRaw = (_, _, _, _) => CloudApiResult<CloudRawResponse>.Ok(new CloudRawResponse { Body = body }, 403);
        SignIn();

        var res = await Desktop().PostAsync("/lighting/effect-market", Json("{}"));

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal(body, await res.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task A_network_failure_is_503_market_unavailable(string method)
    {
        _factory.Api.OnSendRaw = (_, _, _, _) => CloudApiResult<CloudRawResponse>.NetworkError("down");
        SignIn();

        var res = method == "GET"
            ? await Desktop().GetAsync("/lighting/effect-market")
            : await Desktop().PostAsync("/lighting/effect-market", Json("{}"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        var json = await Read(res);
        Assert.True(json.GetProperty("error").GetBoolean());
        Assert.Equal("market_unavailable", json.GetProperty("msg").GetString());
    }

    [Theory]
    [InlineData(404)]
    [InlineData(400)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task Cloud_error_statuses_and_bodies_pass_through_unchanged(int status)
    {
        const string body = "{\"statusCode\":1,\"message\":\"nope\",\"error\":\"E\"}";
        _factory.Api.OnSendRaw = (_, _, _, _) => CloudApiResult<CloudRawResponse>.Ok(new CloudRawResponse { Body = body }, status);
        SignIn();
        var http = Desktop();

        var get = await http.GetAsync("/lighting/effect-market/" + Guid1);
        var post = await http.PostAsync("/lighting/effect-market", Json("{}"));

        foreach (var res in new[] { get, post })
        {
            Assert.Equal(status, (int)res.StatusCode);
            Assert.Equal(body, await res.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task A_body_over_the_cap_is_413_and_not_forwarded()
    {
        SignIn();
        var calls = _factory.Api.SendRawCalls;
        var big = Json("{\"x\":\"" + new string('a', 32 * 1024) + "\"}");
        var http = Desktop();

        var post = await http.PostAsync("/lighting/effect-market", big);
        var put = await http.PutAsync("/lighting/effect-market/" + Guid1, big);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, post.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, put.StatusCode);
        Assert.True((await Read(post)).GetProperty("error").GetBoolean());
        Assert.Equal(calls, _factory.Api.SendRawCalls);
    }

    [Fact]
    public async Task A_chunked_body_over_the_cap_is_413()
    {
        SignIn();
        var calls = _factory.Api.SendRawCalls;
        var content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(new string('a', 40 * 1024))));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var req = new HttpRequestMessage(HttpMethod.Post, "/lighting/effect-market") { Content = content };
        req.Headers.TransferEncodingChunked = true;

        var res = await Desktop().SendAsync(req);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, res.StatusCode);
        Assert.Equal(calls, _factory.Api.SendRawCalls);
    }

    [Fact]
    public async Task A_body_at_the_cap_is_forwarded()
    {
        SignIn();
        string? seen = null;
        _factory.Api.OnSendRaw = (_, _, b, _) =>
        {
            seen = b;
            return CloudApiResult<CloudRawResponse>.Ok(new CloudRawResponse { Body = "{}" });
        };
        var exact = new string('a', 32 * 1024);

        var res = await Desktop().PostAsync("/lighting/effect-market", Json(exact));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(exact, seen);
    }

    [Fact]
    public async Task Routes_require_the_desktop_token()
    {
        var res = await _factory.CreateClient().GetAsync("/lighting/effect-market");

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/lighting/effect-market")]
    [InlineData("GET", "/lighting/effect-market/{id}")]
    [InlineData("POST", "/lighting/effect-market")]
    [InlineData("PUT", "/lighting/effect-market/{id}")]
    [InlineData("DELETE", "/lighting/effect-market/{id}")]
    [InlineData("POST", "/lighting/effect-market/{id}/download")]
    public void Routes_are_not_reachable_from_a_panel_session(string method, string pattern)
    {
        var endpoint = _factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .FirstOrDefault(e =>
                string.Equals(e.RoutePattern.RawText, pattern, StringComparison.Ordinal)
                && (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(method) ?? false));

        Assert.NotNull(endpoint);
        Assert.Null(endpoint!.Metadata.GetMetadata<AllowPanelAccess>());
    }
}
