using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
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

    private NexusSettings Settings => _factory.Services.GetRequiredService<IConfigStore>().Load();

    [Theory]
    [InlineData(30)]
    [InlineData(10)]
    [InlineData(1)]
    public async Task Allowed_rate_is_stored(int hz)
    {
        var res = await Post($$"""{"id":"openrgb-s-X","maxRateHz":{{hz}}}""");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(hz, Settings.Devices.LightingDevicePrefs["openrgb-s-X"].MaxRateHz);
    }

    [Fact]
    public async Task Null_clears_the_cap()
    {
        await Post("""{"id":"openrgb-s-X","maxRateHz":10}""");
        var res = await Post("""{"id":"openrgb-s-X","maxRateHz":null}""");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Null(Settings.Devices.LightingDevicePrefs["openrgb-s-X"].MaxRateHz);
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
        Assert.False(Settings.Devices.LightingDevicePrefs.ContainsKey("openrgb-s-X"));
    }
}
