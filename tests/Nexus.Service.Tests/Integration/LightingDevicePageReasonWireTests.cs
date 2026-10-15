using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Devices;
using Nexus.Service.Models.Devices;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Integration;

public sealed class DevicePageReasonHostFactory : NexusAppFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ILightingDeviceProvider>();
            services.AddSingleton<ILightingDeviceProvider>(sp =>
                new DevicePageReasonProvider(new StubDeviceProvider(sp.GetRequiredService<IConfigStore>())));
        });
    }
}

/// <summary>Wraps the stub provider and returns one Lighting-page card and one device-page card.</summary>
internal sealed class DevicePageReasonProvider(StubDeviceProvider inner) : ILightingDeviceProvider
{
    public bool IsConnected => true;

    public GetLightingDevicesResponse GetAll() => new()
    {
        IsInit = true,
        Devices =
        [
            new LightingDevice { Id = "wire-lighting-page", Name = "Lighting page card", LedCount = 8 },
            new LightingDevice
            {
                Id = "wire-device-page",
                Name = "Device page card",
                LedCount = 8,
                DevicePageReason = LightingDevice.ReasonFirmware,
            },
        ],
    };

    public void SetDisabled(IReadOnlyList<string> ids) => inner.SetDisabled(ids);
    public void SetPower(string id, bool on) => inner.SetPower(id, on);
    public void SetBrightness(string id, int brightness) => inner.SetBrightness(id, brightness);
    public void SetHue(string id, float hue) => inner.SetHue(id, hue);
    public void SetSaturation(string id, float saturation) => inner.SetSaturation(id, saturation);
    public void SetZoneLedCount(string id, int count) => inner.SetZoneLedCount(id, count);
    public void Identify(string id, int durationMs) => inner.Identify(id, durationMs);
}

/// <summary>
/// The SPA reads a present devicePageReason (even null) as a device-page card, so the
/// real HTTP serializer must omit it for a card the Lighting page drives.
/// </summary>
public sealed class LightingDevicePageReasonWireTests : IClassFixture<DevicePageReasonHostFactory>
{
    private readonly DevicePageReasonHostFactory _factory;

    public LightingDevicePageReasonWireTests(DevicePageReasonHostFactory factory)
    {
        _factory = factory;
        _factory.ResetSettings();
    }

    [Fact]
    public async Task LightingDevicesAll_OmitsNullReasonAndKeepsDevicePageReason()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.Services.GetRequiredService<TokenService>().Token);

        var res = await client.GetAsync("/devices/lighting-devices/all");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        JsonElement? lightingPage = null;
        JsonElement? devicePage = null;
        foreach (var d in doc.RootElement.GetProperty("devices").EnumerateArray())
        {
            var id = d.GetProperty("id").GetString();
            if (id == "wire-lighting-page") lightingPage = d.Clone();
            if (id == "wire-device-page") devicePage = d.Clone();
        }
        Assert.NotNull(lightingPage);
        Assert.NotNull(devicePage);
        Assert.False(lightingPage!.Value.TryGetProperty("devicePageReason", out _));
        Assert.Equal("firmware", devicePage!.Value.GetProperty("devicePageReason").GetString());
    }
}
