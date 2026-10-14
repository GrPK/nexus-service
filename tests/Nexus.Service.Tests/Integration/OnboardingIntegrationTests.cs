using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// Drives the real /onboarding routes through Program.cs. LocalhostOnly +
/// token-gated, so requests set a loopback RemoteIpAddress (as the
/// auth-middleware tests do). Effect is asserted via the shared IConfigStore
/// rather than the response body. Split into two classes (read-only vs the
/// one mutating test) because IClassFixture shares a single NexusAppFactory
/// across every test method in a class, and xUnit does not order them.
/// </summary>
public sealed class OnboardingIntegrationTests : IClassFixture<NexusAppFactory>
{
    private readonly NexusAppFactory _factory;

    public OnboardingIntegrationTests(NexusAppFactory factory) => _factory = factory;

    private string Token => _factory.Services.GetRequiredService<TokenService>().Token;

    private async Task<int> Send(string method, string path, bool withToken = true)
    {
        var ctx = await _factory.Server.SendAsync(c =>
        {
            c.Request.Method = method;
            c.Request.Path = path;
            c.Connection.RemoteIpAddress = IPAddress.Loopback;
            if (withToken)
            {
                c.Request.Headers.Authorization = "Bearer " + Token;
            }
        });
        return ctx.Response.StatusCode;
    }

    [Fact]
    public async Task Status_is_reachable_on_loopback_with_token_and_defaults_incomplete()
    {
        var store = _factory.Services.GetRequiredService<IConfigStore>();
        Assert.False(store.Load().OnboardingCompleted);
        Assert.False(store.Load().FeaturesOnboardingCompleted);
        Assert.False(store.Load().LightingOnboardingCompleted);
        Assert.Equal(StatusCodes.Status200OK, await Send("GET", "/onboarding"));
    }

    [Fact]
    public async Task Status_requires_a_token()
        => Assert.Equal(StatusCodes.Status401Unauthorized, await Send("GET", "/onboarding", withToken: false));

    [Fact]
    public async Task Complete_requires_a_token()
        => Assert.Equal(StatusCodes.Status401Unauthorized, await Send("POST", "/onboarding/complete", withToken: false));

    [Fact]
    public async Task FeaturesComplete_requires_a_token()
        => Assert.Equal(StatusCodes.Status401Unauthorized, await Send("POST", "/onboarding/features-complete", withToken: false));

    [Fact]
    public async Task LightingComplete_requires_a_token()
        => Assert.Equal(StatusCodes.Status401Unauthorized, await Send("POST", "/onboarding/lighting-complete", withToken: false));

    [Fact]
    public async Task PanelSwipe_status_is_reachable_and_defaults_incomplete()
    {
        var store = _factory.Services.GetRequiredService<IConfigStore>();
        Assert.False(store.Load().PanelSwipeOnboardingCompleted);
        Assert.Equal(StatusCodes.Status200OK, await Send("GET", "/onboarding/panel-swipe"));
    }

    [Fact]
    public async Task PanelSwipe_status_requires_a_token()
        => Assert.Equal(StatusCodes.Status401Unauthorized, await Send("GET", "/onboarding/panel-swipe", withToken: false));

    [Fact]
    public async Task PanelSwipeComplete_requires_a_token()
        => Assert.Equal(StatusCodes.Status401Unauthorized, await Send("POST", "/onboarding/panel-swipe/complete", withToken: false));

    [Fact]
    public async Task ImmersiveSwipeComplete_requires_a_token()
        => Assert.Equal(StatusCodes.Status401Unauthorized, await Send("POST", "/onboarding/panel-swipe/immersive/complete", withToken: false));

    private async Task<int> SendJson(string path, string json)
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var res = await client.PostAsync(path, content);
        return (int)res.StatusCode;
    }

    [Fact]
    public async Task ConflictsStep_accepts_a_known_action()
        => Assert.Equal(StatusCodes.Status200OK, await SendJson("/onboarding/conflicts-step",
            "{\"action\":\"resolveAll\",\"listed\":[\"signalrgb\"],\"whitelisted\":[],\"alreadyEnded\":[]}"));

    [Fact]
    public void ConflictsStep_line_names_catalog_ids_only()
    {
        var line = Nexus.Service.Routes.OnboardingRoutes.ConflictStepLine(new Nexus.Service.Routes.OnboardingConflictsStepBody
        {
            Action = "resolveAll",
            Listed = { "signalrgb", "icue", "not-an-app\nINF forged" },
            Whitelisted = { "icue" },
        });

        Assert.Equal("[onboarding] conflicts step: resolveAll listed=signalrgb,icue whitelisted=icue alreadyEnded=-", line);
    }

    [Fact]
    public async Task ConflictsStep_rejects_an_unknown_action()
        => Assert.Equal(StatusCodes.Status400BadRequest, await SendJson("/onboarding/conflicts-step", "{\"action\":\"wipe\"}"));

    [Fact]
    public async Task ConflictsStep_requires_a_token()
        => Assert.Equal(StatusCodes.Status401Unauthorized, await Send("POST", "/onboarding/conflicts-step", withToken: false));
}

public sealed class OnboardingPanelSwipeCompleteIntegrationTests : IClassFixture<NexusAppFactory>
{
    private readonly NexusAppFactory _factory;

    public OnboardingPanelSwipeCompleteIntegrationTests(NexusAppFactory factory) => _factory = factory;

    [Fact]
    public async Task PanelSwipeComplete_sets_only_the_panel_swipe_flag()
    {
        var store = _factory.Services.GetRequiredService<IConfigStore>();
        var token = _factory.Services.GetRequiredService<TokenService>().Token;

        var ctx = await _factory.Server.SendAsync(c =>
        {
            c.Request.Method = "POST";
            c.Request.Path = "/onboarding/panel-swipe/complete";
            c.Connection.RemoteIpAddress = IPAddress.Loopback;
            c.Request.Headers.Authorization = "Bearer " + token;
        });

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.True(store.Load().PanelSwipeOnboardingCompleted);
        Assert.False(store.Load().ImmersiveSwipeOnboardingCompleted);
        Assert.False(store.Load().OnboardingCompleted);
        Assert.False(store.Load().FeaturesOnboardingCompleted);
        Assert.False(store.Load().LightingOnboardingCompleted);
    }
}

public sealed class OnboardingImmersiveSwipeCompleteIntegrationTests : IClassFixture<NexusAppFactory>
{
    private readonly NexusAppFactory _factory;

    public OnboardingImmersiveSwipeCompleteIntegrationTests(NexusAppFactory factory) => _factory = factory;

    [Fact]
    public async Task ImmersiveSwipeComplete_sets_only_the_immersive_flag_and_reports_it()
    {
        var store = _factory.Services.GetRequiredService<IConfigStore>();
        var token = _factory.Services.GetRequiredService<TokenService>().Token;

        var ctx = await _factory.Server.SendAsync(c =>
        {
            c.Request.Method = "POST";
            c.Request.Path = "/onboarding/panel-swipe/immersive/complete";
            c.Connection.RemoteIpAddress = IPAddress.Loopback;
            c.Request.Headers.Authorization = "Bearer " + token;
        });

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.True(store.Load().ImmersiveSwipeOnboardingCompleted);
        Assert.False(store.Load().PanelSwipeOnboardingCompleted);
        using var body = await System.Text.Json.JsonDocument.ParseAsync(ctx.Response.Body);
        Assert.True(body.RootElement.GetProperty("immersiveCompleted").GetBoolean());
        Assert.False(body.RootElement.GetProperty("completed").GetBoolean());
    }
}

public sealed class OnboardingCompleteIntegrationTests : IClassFixture<NexusAppFactory>
{
    private readonly NexusAppFactory _factory;

    public OnboardingCompleteIntegrationTests(NexusAppFactory factory) => _factory = factory;

    [Fact]
    public async Task Complete_sets_the_flag()
    {
        var store = _factory.Services.GetRequiredService<IConfigStore>();
        var token = _factory.Services.GetRequiredService<TokenService>().Token;

        var ctx = await _factory.Server.SendAsync(c =>
        {
            c.Request.Method = "POST";
            c.Request.Path = "/onboarding/complete";
            c.Connection.RemoteIpAddress = IPAddress.Loopback;
            c.Request.Headers.Authorization = "Bearer " + token;
        });

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.True(store.Load().OnboardingCompleted);
    }
}

public sealed class OnboardingLightingCompleteIntegrationTests : IClassFixture<NexusAppFactory>
{
    private readonly NexusAppFactory _factory;

    public OnboardingLightingCompleteIntegrationTests(NexusAppFactory factory) => _factory = factory;

    [Fact]
    public async Task LightingComplete_sets_only_the_lighting_flag()
    {
        var store = _factory.Services.GetRequiredService<IConfigStore>();
        var token = _factory.Services.GetRequiredService<TokenService>().Token;

        var ctx = await _factory.Server.SendAsync(c =>
        {
            c.Request.Method = "POST";
            c.Request.Path = "/onboarding/lighting-complete";
            c.Connection.RemoteIpAddress = IPAddress.Loopback;
            c.Request.Headers.Authorization = "Bearer " + token;
        });

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.True(store.Load().LightingOnboardingCompleted);
        Assert.False(store.Load().OnboardingCompleted);
        Assert.False(store.Load().FeaturesOnboardingCompleted);
    }
}

public sealed class OnboardingFeaturesCompleteIntegrationTests : IClassFixture<NexusAppFactory>
{
    private readonly NexusAppFactory _factory;

    public OnboardingFeaturesCompleteIntegrationTests(NexusAppFactory factory) => _factory = factory;

    [Fact]
    public async Task FeaturesComplete_sets_only_the_features_flag()
    {
        var store = _factory.Services.GetRequiredService<IConfigStore>();
        var token = _factory.Services.GetRequiredService<TokenService>().Token;

        var ctx = await _factory.Server.SendAsync(c =>
        {
            c.Request.Method = "POST";
            c.Request.Path = "/onboarding/features-complete";
            c.Connection.RemoteIpAddress = IPAddress.Loopback;
            c.Request.Headers.Authorization = "Bearer " + token;
        });

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.True(store.Load().FeaturesOnboardingCompleted);
        Assert.False(store.Load().OnboardingCompleted);
        Assert.False(store.Load().LightingOnboardingCompleted);
    }
}

public sealed class OnboardingResetIntegrationTests : IClassFixture<NexusAppFactory>
{
    private readonly NexusAppFactory _factory;

    public OnboardingResetIntegrationTests(NexusAppFactory factory) => _factory = factory;

    [Fact]
    public async Task Reset_clears_completed_features_and_lighting_flags()
    {
        var store = _factory.Services.GetRequiredService<IConfigStore>();
        var token = _factory.Services.GetRequiredService<TokenService>().Token;
        store.Update(s =>
        {
            s.OnboardingCompleted = true;
            s.FeaturesOnboardingCompleted = true;
            s.LightingOnboardingCompleted = true;
            s.PanelSwipeOnboardingCompleted = true;
            s.ImmersiveSwipeOnboardingCompleted = true;
        });

        var ctx = await _factory.Server.SendAsync(c =>
        {
            c.Request.Method = "POST";
            c.Request.Path = "/onboarding/reset";
            c.Connection.RemoteIpAddress = IPAddress.Loopback;
            c.Request.Headers.Authorization = "Bearer " + token;
        });

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.False(store.Load().OnboardingCompleted);
        Assert.False(store.Load().FeaturesOnboardingCompleted);
        Assert.False(store.Load().LightingOnboardingCompleted);
        Assert.False(store.Load().PanelSwipeOnboardingCompleted);
        Assert.False(store.Load().ImmersiveSwipeOnboardingCompleted);
    }
}

public sealed class OnboardingBannerIntegrationTests : IClassFixture<NexusAppFactory>
{
    private readonly NexusAppFactory _factory;

    public OnboardingBannerIntegrationTests(NexusAppFactory factory) => _factory = factory;

    private async Task<HttpContext> Send(string method, string path, string? jsonBody = null, bool withToken = true)
    {
        var token = _factory.Services.GetRequiredService<TokenService>().Token;
        return await _factory.Server.SendAsync(c =>
        {
            c.Request.Method = method;
            c.Request.Path = path;
            c.Connection.RemoteIpAddress = IPAddress.Loopback;
            if (withToken)
                c.Request.Headers.Authorization = "Bearer " + token;
            if (jsonBody is not null)
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(jsonBody);
                c.Request.ContentType = "application/json";
                c.Request.Body = new MemoryStream(bytes);
                c.Request.ContentLength = bytes.Length;
            }
        });
    }

    [Fact]
    public async Task Dismiss_stores_the_key_and_status_returns_it()
    {
        var store = _factory.Services.GetRequiredService<IConfigStore>();

        var dismiss = await Send("POST", "/onboarding/banner/dismiss/3.0.19");
        Assert.Equal(StatusCodes.Status200OK, dismiss.Response.StatusCode);
        Assert.Equal("3.0.19", store.Load().DashboardBannerDismissed);

        var status = await Send("GET", "/onboarding/banner");
        Assert.Equal(StatusCodes.Status200OK, status.Response.StatusCode);
        var body = await new StreamReader(status.Response.Body).ReadToEndAsync();
        Assert.Contains("\"dismissedKey\":\"3.0.19\"", body);
    }

    [Fact]
    public async Task Dismiss_rejects_an_empty_key()
    {
        var ctx = await Send("POST", "/onboarding/banner/dismiss/%20");
        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Banner_routes_require_a_token()
    {
        Assert.Equal(StatusCodes.Status401Unauthorized, (await Send("GET", "/onboarding/banner", withToken: false)).Response.StatusCode);
        Assert.Equal(StatusCodes.Status401Unauthorized, (await Send("POST", "/onboarding/banner/dismiss/x", withToken: false)).Response.StatusCode);
    }
}

public sealed class OnboardingHintsIntegrationTests : IClassFixture<NexusAppFactory>
{
    private readonly NexusAppFactory _factory;

    public OnboardingHintsIntegrationTests(NexusAppFactory factory) => _factory = factory;

    private async Task<(int Status, string Body)> Send(string method, string path, string? jsonBody = null, bool withToken = true)
    {
        using var client = _factory.CreateClient();
        if (withToken)
        {
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
        }
        using var req = new HttpRequestMessage(new HttpMethod(method), path);
        if (jsonBody is not null)
        {
            req.Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");
        }
        using var res = await client.SendAsync(req);
        return ((int)res.StatusCode, await res.Content.ReadAsStringAsync());
    }

    private Task<(int Status, string Body)> Dismiss(string id)
        => Send("POST", "/onboarding/hints/dismiss", $"{{\"id\":\"{id}\"}}");

    private void Clear() => _factory.Services.GetRequiredService<IConfigStore>()
        .Update(s => s.DismissedOnboardingHints = new List<string>());

    [Fact]
    public async Task Get_defaults_to_an_empty_list()
    {
        Clear();
        var (status, body) = await Send("GET", "/onboarding");
        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Contains("\"dismissedHints\":[]", body);
    }

    [Fact]
    public async Task Dismiss_then_get_returns_the_id_and_is_idempotent()
    {
        Clear();
        Assert.Equal(StatusCodes.Status200OK, (await Dismiss("calibrate-fans")).Status);
        Assert.Equal(StatusCodes.Status200OK, (await Dismiss("calibrate-fans")).Status);
        var (_, body) = await Send("GET", "/onboarding");
        Assert.Contains("\"dismissedHints\":[\"calibrate-fans\"]", body);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Upper")]
    [InlineData("-lead")]
    [InlineData("has space")]
    [InlineData("under_score")]
    [InlineData("calibrate-fans\n")]
    public async Task Dismiss_rejects_invalid_ids(string id)
    {
        Clear();
        var json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["id"] = id });
        Assert.Equal(StatusCodes.Status400BadRequest, (await Send("POST", "/onboarding/hints/dismiss", json)).Status);
        Assert.Empty(_factory.Services.GetRequiredService<IConfigStore>().Load().DismissedOnboardingHints);
    }

    [Fact]
    public async Task Dismiss_rejects_an_overlong_id_and_a_body_without_id()
    {
        Clear();
        Assert.Equal(StatusCodes.Status400BadRequest, (await Dismiss(new string('a', 65))).Status);
        Assert.Equal(StatusCodes.Status400BadRequest, (await Send("POST", "/onboarding/hints/dismiss", "{}")).Status);
        Assert.Empty(_factory.Services.GetRequiredService<IConfigStore>().Load().DismissedOnboardingHints);
    }

    [Fact]
    public async Task Dismiss_with_a_json_null_body_returns_400_from_the_route()
    {
        Clear();
        var (status, _) = await Send("POST", "/onboarding/hints/dismiss", "null");
        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Empty(_factory.Services.GetRequiredService<IConfigStore>().Load().DismissedOnboardingHints);
    }

    [Fact]
    public async Task Dismiss_with_no_body_at_all_is_rejected_without_storing()
    {
        Clear();
        var (status, _) = await Send("POST", "/onboarding/hints/dismiss");
        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Empty(_factory.Services.GetRequiredService<IConfigStore>().Load().DismissedOnboardingHints);
    }

    [Fact]
    public async Task Dismiss_accepts_a_64_char_id()
    {
        Clear();
        Assert.Equal(StatusCodes.Status200OK, (await Dismiss(new string('a', 64))).Status);
    }

    [Fact]
    public async Task Dismiss_refuses_new_ids_past_the_cap_but_accepts_repeats()
    {
        Clear();
        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(StatusCodes.Status200OK, (await Dismiss($"hint-{i}")).Status);
        }
        Assert.Equal(StatusCodes.Status409Conflict, (await Dismiss("hint-extra")).Status);
        Assert.Equal(StatusCodes.Status200OK, (await Dismiss("hint-0")).Status);
        Assert.Equal(100, _factory.Services.GetRequiredService<IConfigStore>().Load().DismissedOnboardingHints.Count);
    }

    [Fact]
    public async Task Reset_leaves_dismissed_hints_intact()
    {
        Clear();
        await Dismiss("calibrate-fans");
        Assert.Equal(StatusCodes.Status200OK, (await Send("POST", "/onboarding/reset")).Status);
        Assert.Equal(new[] { "calibrate-fans" }, _factory.Services.GetRequiredService<IConfigStore>().Load().DismissedOnboardingHints);
    }

    [Fact]
    public async Task Dismiss_requires_a_token()
        => Assert.Equal(StatusCodes.Status401Unauthorized, (await Send("POST", "/onboarding/hints/dismiss", "{\"id\":\"x\"}", withToken: false)).Status);

    [Fact]
    public void Dismissed_hints_survive_a_store_reload()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus-hints-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        try
        {
            var first = new JsonConfigStore(path);
            first.Update(s => s.DismissedOnboardingHints = new List<string> { "calibrate-fans" });
            first.Dispose();

            var second = new JsonConfigStore(path);
            try
            {
                Assert.Equal(new[] { "calibrate-fans" }, second.Load().DismissedOnboardingHints);
            }
            finally
            {
                second.Dispose();
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}

public sealed class OnboardingHintsProfileIsolationTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "nexus-hints-prof-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly JsonConfigStore _store;
    private readonly ProfileManager _profiles;

    public OnboardingHintsProfileIsolationTests()
    {
        Directory.CreateDirectory(_tempDir);
        _store = new JsonConfigStore(Path.Combine(_tempDir, "settings.json"));
        _profiles = new ProfileManager(_store);
        _profiles.Initialize();
    }

    public void Dispose()
    {
        _profiles.Dispose();
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Profile_switch_leaves_dismissed_hints_untouched()
    {
        _store.Update(s => s.DismissedOnboardingHints = new List<string> { "calibrate-fans" });
        var other = _profiles.CreateProfile("Other");

        _profiles.SwitchProfile(other.Id);

        Assert.Equal(new[] { "calibrate-fans" }, _store.Load().DismissedOnboardingHints);
    }

    [Fact]
    public void Library_replace_leaves_dismissed_hints_untouched()
    {
        _store.Update(s => s.DismissedOnboardingHints = new List<string> { "calibrate-fans" });

        _profiles.ReplaceLibrary(new List<(string Id, string Name, NexusSettings Data)>
        {
            ("incoming-1", "Incoming", new NexusSettings()),
        });

        Assert.Equal(new[] { "calibrate-fans" }, _store.Load().DismissedOnboardingHints);
    }
}
