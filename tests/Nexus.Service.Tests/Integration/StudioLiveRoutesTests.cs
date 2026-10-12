using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Lighting;
using Nexus.Service.Tests.Lighting.CustomEffects;

namespace Nexus.Service.Tests.Integration;

public sealed class StudioLiveRoutesTests : IClassFixture<CustomEffectAppFactory>, IAsyncLifetime
{
    private readonly CustomEffectAppFactory _factory;

    public StudioLiveRoutesTests(CustomEffectAppFactory factory)
    {
        _factory = factory;
        _factory.CompileError = null;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await Desktop().DeleteAsync("/lighting/studio-live");

    private HttpClient Desktop()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    private static StringContent Body(string frag) =>
        new(JsonSerializer.Serialize(new { frag, speed = 50, intensity = 1, hue = 0, colorize = 0, saturation = 1, contrast = 1, @params = new Dictionary<string, float> { ["u_b1_gain"] = 0.4f } }),
            Encoding.UTF8, "application/json");

    private static async Task<JsonElement> Read(HttpResponseMessage res)
    {
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task Post_starts_a_session_and_answers_with_the_lease()
    {
        var res = await Desktop().PostAsync("/lighting/studio-live", Body(CustomFrags.Frag()));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = await Read(res);
        Assert.False(json.GetProperty("error").GetBoolean());
        Assert.True(json.GetProperty("active").GetBoolean());
        Assert.Equal(15_000, json.GetProperty("leaseMs").GetInt32());
        Assert.True(_factory.Services.GetRequiredService<ILightingProvider>().StudioLiveActive);
    }

    [Fact]
    public async Task Current_flags_the_session_and_delete_ends_it()
    {
        var http = Desktop();
        await http.PostAsync("/lighting/studio-live", Body(CustomFrags.Frag()));

        var during = await Read(await http.GetAsync("/lighting/current"));
        Assert.True(during.GetProperty("studioLive").GetBoolean());

        var del = await http.DeleteAsync("/lighting/studio-live");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        var after = await Read(await http.GetAsync("/lighting/current"));
        Assert.False(after.GetProperty("studioLive").GetBoolean());
    }

    [Fact]
    public async Task Delete_without_a_session_is_ok_twice()
    {
        var http = Desktop();

        Assert.Equal(HttpStatusCode.OK, (await http.DeleteAsync("/lighting/studio-live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.DeleteAsync("/lighting/studio-live")).StatusCode);
    }

    [Fact]
    public async Task A_fragment_the_store_rules_reject_is_a_400_with_the_reason_and_starts_nothing()
    {
        var res = await Desktop().PostAsync("/lighting/studio-live", Body("void main() {}"));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var json = await Read(res);
        Assert.True(json.GetProperty("error").GetBoolean());
        Assert.Contains("#version", json.GetProperty("msg").GetString());
        Assert.False(_factory.Services.GetRequiredService<ILightingProvider>().StudioLiveActive);
    }

    [Fact]
    public async Task A_missing_body_is_a_400()
    {
        var res = await Desktop().PostAsync("/lighting/studio-live", new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task A_fragment_that_fails_the_gpu_compile_is_a_400_with_the_log()
    {
        _factory.CompileError = "0:3: syntax error";

        var res = await Desktop().PostAsync("/lighting/studio-live", Body(CustomFrags.Frag()));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("0:3: syntax error", (await Read(res)).GetProperty("msg").GetString());
        Assert.False(_factory.Services.GetRequiredService<ILightingProvider>().StudioLiveActive);
    }

    [Fact]
    public async Task A_failed_update_leaves_the_running_session_alone()
    {
        var http = Desktop();
        await http.PostAsync("/lighting/studio-live", Body(CustomFrags.Frag()));
        _factory.CompileError = "bad";

        var res = await http.PostAsync("/lighting/studio-live", Body(CustomFrags.Frag(0.9f)));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.True(_factory.Services.GetRequiredService<ILightingProvider>().StudioLiveActive);
    }

    [Fact]
    public async Task Both_verbs_need_authentication()
    {
        var anon = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/lighting/studio-live", Body(CustomFrags.Frag()))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.DeleteAsync("/lighting/studio-live")).StatusCode);
    }
}
