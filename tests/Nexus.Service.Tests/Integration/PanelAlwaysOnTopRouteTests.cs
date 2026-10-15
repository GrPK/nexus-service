using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Tests.Integration;

public sealed class PanelAlwaysOnTopRouteTests : IClassFixture<NexusAppFactory>
{
    private readonly NexusAppFactory _factory;

    public PanelAlwaysOnTopRouteTests(NexusAppFactory factory) => _factory = factory;

    private HttpClient AuthedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    private static async Task<bool> ReadBool(HttpResponseMessage res, params string[] path)
    {
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var el = doc.RootElement;
        foreach (var p in path) el = el.GetProperty(p);
        return el.GetBoolean();
    }

    private static Task<HttpResponseMessage> PostPanel(HttpClient client, bool value) =>
        client.PostAsync("/preferences",
            new StringContent($"{{\"panel\":{{\"alwaysOnTop\":{(value ? "true" : "false")}}}}}", Encoding.UTF8, "application/json"));

    [Fact]
    public void Settings_without_the_key_default_to_on()
    {
        var settings = JsonSerializer.Deserialize("{\"panel\":{\"reserveMonitor\":false}}", PersistenceJsonContext.Default.NexusSettings)!;
        Assert.True(settings.Panel.AlwaysOnTop);
        Assert.True(new PanelSettings().AlwaysOnTop);
    }

    [Fact]
    public async Task Setting_round_trips_and_reaches_the_overlay_state()
    {
        var client = AuthedClient();
        Assert.True(await ReadBool(await client.GetAsync("/overlay/state"), "panelAlwaysOnTop"));

        Assert.Equal(HttpStatusCode.OK, (await PostPanel(client, false)).StatusCode);
        Assert.False(await ReadBool(await client.GetAsync("/preferences"), "panel", "alwaysOnTop"));
        Assert.False(await ReadBool(await client.GetAsync("/overlay/state"), "panelAlwaysOnTop"));

        Assert.Equal(HttpStatusCode.OK, (await PostPanel(client, true)).StatusCode);
        Assert.True(await ReadBool(await client.GetAsync("/overlay/state"), "panelAlwaysOnTop"));
    }
}
