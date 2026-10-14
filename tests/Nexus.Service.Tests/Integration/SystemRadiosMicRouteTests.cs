using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Activity;
using Nexus.Service.Auth;
using Nexus.Service.Models.Activity;
using Nexus.Service.Models.Radios;
using Nexus.Service.Platform.Radios;

namespace Nexus.Service.Tests.Integration;

/// <summary>Panel control center routes: /system/radios and /system/mic*.</summary>
public sealed class SystemRadiosMicRouteTests : IDisposable
{
    private readonly NexusAppFactory _baseFactory;
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory;
    private readonly FakeRadios _radios = new();
    private readonly FakeMic _mic = new();

    public SystemRadiosMicRouteTests()
    {
        _baseFactory = new NexusAppFactory();
        _factory = _baseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<IRadioProvider>();
            s.AddSingleton<IRadioProvider>(_radios);
            s.RemoveAll<IVolumeProvider>();
            s.AddSingleton<IVolumeProvider>(_mic);
        }));
    }

    public void Dispose()
    {
        _factory.Dispose();
        _baseFactory.Dispose();
    }

    private HttpClient PanelClient() => TestPhoneSession.CreateClient(_factory);

    private HttpClient DesktopClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    [Fact]
    public async Task GetRadios_AbsentRadiosSerializeAsExplicitNull()
    {
        _radios.State = new RadiosState { Bluetooth = new RadioToggle { On = true } };
        var res = await PanelClient().GetAsync("/system/radios");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("wifi").ValueKind);
        Assert.True(doc.RootElement.GetProperty("bluetooth").GetProperty("on").GetBoolean());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("airplane").ValueKind);
    }

    [Fact]
    public async Task PostRadios_AppliesThenReturnsReadBackState()
    {
        _radios.State = new RadiosState { Wifi = new RadioToggle { On = false } };
        var res = await PanelClient().PostAsJsonAsync("/system/radios", new SetRadioBody { Kind = "wifi", On = true });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("wifi").GetProperty("on").GetBoolean());
        Assert.Equal(("wifi", true), _radios.LastSet);
    }

    [Fact]
    public async Task PostRadios_Airplane_ReturnsReadBackState()
    {
        _radios.State = new RadiosState { Airplane = new RadioToggle { On = false }, Bluetooth = new RadioToggle { On = true } };
        var res = await PanelClient().PostAsJsonAsync("/system/radios", new SetRadioBody { Kind = "airplane", On = true });
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("airplane").GetProperty("on").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("bluetooth").GetProperty("on").GetBoolean());
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("")]
    public async Task PostRadios_UnknownOrAbsentKind_IsFailEnvelope(string kind)
    {
        var res = await PanelClient().PostAsJsonAsync("/system/radios", new SetRadioBody { Kind = kind, On = true });
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("error").GetBoolean());
    }

    [Fact]
    public async Task Mic_NoDevice_MatchesVolumeNoDeviceShape()
    {
        var client = PanelClient();
        var mic = await client.GetFromJsonAsync<JsonElement>("/system/mic");
        var vol = await client.GetFromJsonAsync<JsonElement>("/system/volume");
        Assert.Equal(PropertyNames(vol), PropertyNames(mic));
        Assert.False(mic.GetProperty("supported").GetBoolean());
    }

    [Fact]
    public async Task Mic_SetVolumeAndMute_ReturnVolumeStateShape()
    {
        _mic.Capture = new VolumeState { Supported = true, Volume = 0.2, Muted = false };
        var client = PanelClient();

        var set = await client.PostAsJsonAsync("/system/mic", new SetVolumeBody { Volume = 0.7 });
        var setJson = await set.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0.7, setJson.GetProperty("volume").GetDouble());
        Assert.Equal(PropertyNames(await client.GetFromJsonAsync<JsonElement>("/system/volume")), PropertyNames(setJson));

        var mute = await client.PostAsJsonAsync("/system/mic/mute", new SetMutedBody { Muted = true });
        var muteJson = await mute.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(muteJson.GetProperty("muted").GetBoolean());
        Assert.Equal(0.7, muteJson.GetProperty("volume").GetDouble());
    }

    [Fact]
    public async Task Routes_RejectUnauthenticated()
    {
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/system/radios")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/system/mic")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await DesktopClient().GetAsync("/system/mic")).StatusCode);
    }

    private static string[] PropertyNames(JsonElement e) =>
        e.EnumerateObject().Select(p => p.Name).Order().ToArray();

    private sealed class FakeRadios : IRadioProvider
    {
        public RadiosState State { get; set; } = new();
        public (string Kind, bool On)? LastSet { get; private set; }

        public Task<RadiosState> GetStateAsync(CancellationToken ct = default) => Task.FromResult(State);

        public Task<bool> SetAsync(string kind, bool on, CancellationToken ct = default)
        {
            var toggle = kind switch { "wifi" => State.Wifi, "bluetooth" => State.Bluetooth, "airplane" => State.Airplane, _ => null };
            if (toggle is null)
            {
                return Task.FromResult(false);
            }
            toggle.On = on;
            if (kind == "airplane" && State.Bluetooth is not null)
            {
                State.Bluetooth.On = !on;
            }
            LastSet = (kind, on);
            return Task.FromResult(true);
        }
    }

    private sealed class FakeMic : IVolumeProvider
    {
        public VolumeState Capture { get; set; } = new() { Supported = false };
        public VolumeState GetState() => new() { Supported = false };
        public void SetVolume(double volume) { }
        public void SetMuted(bool muted) { }
        public VolumeState GetCaptureState() => new() { Supported = Capture.Supported, Volume = Capture.Volume, Muted = Capture.Muted };
        public void SetCaptureVolume(double volume) => Capture.Volume = volume;
        public void SetCaptureMuted(bool muted) => Capture.Muted = muted;
    }
}
