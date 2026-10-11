using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Integration;

public sealed class AnimateLockRoutesTests : IClassFixture<StubDeviceHostFactory>
{
    private readonly StubDeviceHostFactory _factory;
    private readonly HttpClient _client;

    public AnimateLockRoutesTests(StubDeviceHostFactory factory)
    {
        _factory = factory;
        _factory.ResetSettings();
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                _factory.Services.GetRequiredService<TokenService>().Token);
    }

    // Each test uses its own device id: the host's trackers are shared singletons
    // that ResetSettings does not clear.
    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private IConfigStore Store => _factory.Services.GetRequiredService<IConfigStore>();

    private Task<HttpResponseMessage> Lock(string id, string? effect) =>
        _client.PostAsync(
            "/devices/lighting-devices/animate-lock",
            Json(effect is null ? $$"""{"id":"{{id}}","effect":null}""" : $$"""{"id":"{{id}}","effect":"{{effect}}"}"""));

    [Fact]
    public async Task Locking_stores_the_effect_and_the_get_route_lists_it()
    {
        var res = await Lock("dev-Locking_stores_the_effect_and_the_get_route_lists_it", "plasma");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("plasma", Store.Load().Lighting.AnimateDeviceLocks["dev-Locking_stores_the_effect_and_the_get_route_lists_it"]);

        var get = await _client.GetAsync("/devices/lighting-devices/animate-locks");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        using var doc = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        Assert.Equal("plasma", doc.RootElement.GetProperty("locks").GetProperty("dev-Locking_stores_the_effect_and_the_get_route_lists_it").GetString());
    }

    [Fact]
    public async Task Locking_again_replaces_and_a_null_effect_unlocks()
    {
        await Lock("dev-Locking_again_replaces_and_a_null_effect_unlocks", "plasma");
        await Lock("dev-Locking_again_replaces_and_a_null_effect_unlocks", "fire");
        Assert.Equal("fire", Store.Load().Lighting.AnimateDeviceLocks["dev-Locking_again_replaces_and_a_null_effect_unlocks"]);

        var res = await Lock("dev-Locking_again_replaces_and_a_null_effect_unlocks", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Empty(Store.Load().Lighting.AnimateDeviceLocks);
    }

    [Theory]
    [InlineData("gradientlinear")]
    [InlineData("simplered")]
    [InlineData("not-an-effect")]
    public async Task A_key_that_is_not_an_animate_shader_is_refused(string effect)
    {
        var res = await Lock("dev-A_key_that_is_not_an_animate_shader_is_refused", effect);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Empty(Store.Load().Lighting.AnimateDeviceLocks);
    }

    [Fact]
    public async Task A_device_holding_a_static_lock_cannot_take_an_animation_lock()
    {
        await _client.PostAsync(
            "/devices/lighting-devices/color",
            Json("""{"id":"dev-A_device_holding_a_static_lock_cannot_take_an_animation_lock","hue":0.5,"saturation":1,"effect":"flat","color":"#abcdef"}"""));
        await _client.PostAsync("/devices/lighting-devices/static-lock", Json("""{"id":"dev-A_device_holding_a_static_lock_cannot_take_an_animation_lock","locked":true}"""));

        var res = await Lock("dev-A_device_holding_a_static_lock_cannot_take_an_animation_lock", "plasma");
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Empty(Store.Load().Lighting.AnimateDeviceLocks);
    }

    [Fact]
    public async Task A_device_holding_an_animation_lock_cannot_take_a_static_lock()
    {
        await _client.PostAsync(
            "/devices/lighting-devices/color",
            Json("""{"id":"dev-A_device_holding_an_animation_lock_cannot_take_a_static_lock","hue":0.5,"saturation":1,"effect":"flat","color":"#abcdef"}"""));
        var lockRes = await Lock("dev-A_device_holding_an_animation_lock_cannot_take_a_static_lock", "plasma");
        Assert.Equal(HttpStatusCode.OK, lockRes.StatusCode);

        var res = await _client.PostAsync("/devices/lighting-devices/static-lock", Json("""{"id":"dev-A_device_holding_an_animation_lock_cannot_take_a_static_lock","locked":true}"""));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.False(Store.Load().Lighting.StaticDeviceLooks["dev-A_device_holding_an_animation_lock_cannot_take_a_static_lock"].Locked);

        // Unlocking a static lock is never blocked.
        var unlock = await _client.PostAsync("/devices/lighting-devices/static-lock", Json("""{"id":"dev-A_device_holding_an_animation_lock_cannot_take_a_static_lock","locked":false}"""));
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
    }

    [Fact]
    public async Task A_preset_round_trips_the_animation_locks()
    {
        await Lock("dev-A_preset_round_trips_the_animation_locks", "plasma");
        var createRes = await _client.PostAsync("/devices/lighting-devices/layout-presets", Json("""{"name":"Anim"}"""));
        using var createDoc = JsonDocument.Parse(await createRes.Content.ReadAsStringAsync());
        var id = createDoc.RootElement.GetProperty("preset").GetProperty("id").GetString()!;
        Assert.Equal("plasma", Store.Load().Lighting.LayoutPresets.Find(p => p.Id == id)!.AnimateDeviceLocks!["dev-A_preset_round_trips_the_animation_locks"]);

        // A second preset takes the unlocked live state; activating the first restores the lock.
        await _client.PostAsync("/devices/lighting-devices/layout-presets", Json("""{"name":"Open"}"""));
        await Lock("dev-A_preset_round_trips_the_animation_locks", null);
        Assert.Empty(Store.Load().Lighting.AnimateDeviceLocks);

        await _client.PostAsync($"/devices/lighting-devices/layout-presets/{id}/activate", Json("{}"));
        Assert.Equal("plasma", Store.Load().Lighting.AnimateDeviceLocks["dev-A_preset_round_trips_the_animation_locks"]);
    }
}
