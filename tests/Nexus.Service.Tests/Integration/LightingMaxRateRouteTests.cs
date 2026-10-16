using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Integration;

public sealed class LightingMaxRateRouteTests : IClassFixture<StubDeviceHostFactory>
{
    private readonly StubDeviceHostFactory _factory;
    private readonly HttpClient _client;

    public LightingMaxRateRouteTests(StubDeviceHostFactory factory)
    {
        _factory = factory;
        _factory.ResetSettings();
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
    }

    private Task<HttpResponseMessage> Post(string body) =>
        _client.PostAsync("/devices/lighting-devices/max-rate", new StringContent(body, Encoding.UTF8, "application/json"));

    private IConfigStore Store => _factory.Services.GetRequiredService<IConfigStore>();

    private NexusSettings Settings => _factory.Services.GetRequiredService<IConfigStore>().Load();

    [Theory]
    [InlineData(30)]
    [InlineData(10)]
    [InlineData(1)]
    public async Task Allowed_rate_is_stored(int hz)
    {
        var res = await Post($$"""{"id":"openrgb-s-X","maxRateHz":{{hz}}}""");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(hz, Settings.Devices.LightingMaxRates["openrgb-s-X"]);
    }

    [Fact]
    public async Task Preset_activation_leaves_the_caps_intact()
    {
        Store.Update(s =>
        {
            s.Devices.LightingMaxRates["openrgb-s-X"] = 10;
            s.Devices.LightingDevicePrefs["openrgb-s-X"] = new LightingDevicePreference { Brightness = 40 };
        });
        var create = await _client.PostAsync(
            "/devices/lighting-devices/layout-presets",
            new StringContent("""{"name":"P"}""", Encoding.UTF8, "application/json"));
        using var doc = System.Text.Json.JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var id = doc.RootElement.GetProperty("preset").GetProperty("id").GetString();

        Store.Update(s => s.Devices.LightingMaxRates["openrgb-s-X"] = 5);
        var res = await _client.PostAsync(
            $"/devices/lighting-devices/layout-presets/{id}/activate",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(40, Settings.Devices.LightingDevicePrefs["openrgb-s-X"].Brightness);
        Assert.Equal(5, Settings.Devices.LightingMaxRates["openrgb-s-X"]);
    }

    [Fact]
    public async Task Null_clears_the_cap()
    {
        await Post("""{"id":"openrgb-s-X","maxRateHz":10}""");
        var res = await Post("""{"id":"openrgb-s-X","maxRateHz":null}""");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.False(Settings.Devices.LightingMaxRates.ContainsKey("openrgb-s-X"));
    }

    [Theory]
    [InlineData("""{"id":"openrgb-s-X","maxRateHz":25}""")]
    [InlineData("""{"id":"openrgb-s-X","maxRateHz":0}""")]
    [InlineData("""{"id":"openrgb-s-X","maxRateHz":60}""")]
    [InlineData("""{"id":"","maxRateHz":10}""")]
    public async Task Invalid_input_is_rejected(string body)
    {
        var res = await Post(body);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.False(Settings.Devices.LightingMaxRates.ContainsKey("openrgb-s-X"));
    }
}

public sealed class SiblingZoneHostFactory : NexusAppFactory
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<Nexus.Service.Devices.ILightingDeviceProvider>();
            services.AddSingleton<Nexus.Service.Devices.ILightingDeviceProvider>(new SiblingZoneProvider());
        });
    }
}

internal sealed class SiblingZoneProvider : Nexus.Service.Devices.ILightingDeviceProvider
{
    public bool IsConnected => true;

    public Nexus.Service.Models.Devices.GetLightingDevicesResponse GetAll() => new()
    {
        IsInit = true,
        Devices =
        [
            new() { Id = "openrgb-s-MB-0", ParentDeviceId = "openrgb-s-MB", RateCapSupported = true },
            new() { Id = "openrgb-s-MB-1", ParentDeviceId = "openrgb-s-MB", RateCapSupported = true },
            new() { Id = "openrgb-s-OTHER", RateCapSupported = true },
            new() { Id = "keeb-1", ParentDeviceId = "openrgb-s-MB" },
        ],
    };

    public void SetDisabled(IReadOnlyList<string> ids) { }
    public void SetPower(string id, bool on) { }
    public void SetBrightness(string id, int brightness) { }
    public void SetHue(string id, float hue) { }
    public void SetSaturation(string id, float saturation) { }
    public void SetZoneLedCount(string id, int count) { }
    public void Identify(string id, int durationMs) { }
}

public sealed class LightingMaxRateSiblingTests : IClassFixture<SiblingZoneHostFactory>
{
    private readonly SiblingZoneHostFactory _factory;
    private readonly HttpClient _client;

    public LightingMaxRateSiblingTests(SiblingZoneHostFactory factory)
    {
        _factory = factory;
        _factory.ResetSettings();
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
    }

    [Fact]
    public async Task Cap_is_written_to_every_zone_card_of_the_same_device_only()
    {
        var res = await _client.PostAsync(
            "/devices/lighting-devices/max-rate",
            new StringContent("""{"id":"openrgb-s-MB-1","maxRateHz":10}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var prefs = _factory.Services.GetRequiredService<IConfigStore>().Load().Devices.LightingMaxRates;
        Assert.Equal(10, prefs["openrgb-s-MB-0"]);
        Assert.Equal(10, prefs["openrgb-s-MB-1"]);
        Assert.False(prefs.ContainsKey("openrgb-s-OTHER"));
        Assert.False(prefs.ContainsKey("keeb-1"));
    }
}
