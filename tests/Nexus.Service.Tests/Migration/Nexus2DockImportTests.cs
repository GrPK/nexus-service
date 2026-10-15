using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Deck;
using Nexus.Service.Gallery;
using Nexus.Service.Media;
using Nexus.Service.Migration;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Tests.Integration;
using Xunit;

namespace Nexus.Service.Tests.Migration;

/// <summary>The Nexus 2 dock import through the real /migration/nexus2/apply route: the deck widget lands on
/// the Y70 record, its preset and instance are stored, and a re-import rewrites them instead of adding more.</summary>
public sealed class Nexus2DockImportTests : IDisposable
{
    private const string Config = """
        { "profiles": [ { "name": "Profile 1", "active": true, "widgets": { "faces": { "y70": {
          "hideDock": false,
          "pages": [ { "widgets": [ { "type": "clock", "size": "2x2" } ] } ],
          "dock": [
            { "type": "macros", "position": 0, "positionHorizontal": 0, "size": "1x1", "macro": { "type": "shortcut", "targetId": "C:\\WINDOWS\\explorer.exe" } },
            { "type": "macros", "position": 1, "positionHorizontal": 1, "size": "1x1", "macro": { "type": "website", "url": "https://example.com/" } },
            { "type": "blocks", "position": 2, "positionHorizontal": 2, "size": "1x1" }
          ] } } } } ] }
        """;

    private readonly string _tempDir;
    private readonly NexusAppFactory _baseFactory;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public Nexus2DockImportTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus2-dock-itest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        var reader = new FakeNexus2ConfigReader { ConfigText = Config, ConfigDir = _tempDir };
        _baseFactory = new NexusAppFactory();
        _factory = _baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<INexus2ConfigReader>();
                services.AddSingleton<INexus2ConfigReader>(reader);
                services.RemoveAll<GalleryLibrary>();
                services.AddSingleton(new GalleryLibrary(Path.Combine(_tempDir, "gallery")));
                services.RemoveAll<PanelBgLibrary>();
                services.AddSingleton(new PanelBgLibrary(Path.Combine(_tempDir, "panel-bg")));
            }));
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        _baseFactory.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private IConfigStore Store => _factory.Services.GetRequiredService<IConfigStore>();
    private PanelDeviceRegistry Panels => _factory.Services.GetRequiredService<PanelDeviceRegistry>();

    private async Task<string> Apply(bool replace)
    {
        var res = await _client.PostAsJsonAsync("/migration/nexus2/apply", new { categories = new[] { "y70Layout" }, replaceCustomizedLayout = replace });
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("results")[0].GetProperty("status").GetString()!;
    }

    private PanelWidgetDto[] Decks(string recordId) =>
        Panels.Get(recordId)!.Layout!.Pages.SelectMany(p => p.Widgets).Where(w => w.Type == "deck").ToArray();

    [Fact]
    public async Task Apply_PlacesTheDockDeckAndStoresItsKeys()
    {
        var y70 = Panels.Allocate(null, new PanelDeviceCapabilities { Surface = PanelSurfaces.Y70 });

        Assert.Equal("applied", await Apply(false));

        var deck = Assert.Single(Decks(y70.Id));
        Assert.Equal(("4x1", 0, 15), (deck.Size, deck.Col, deck.Row));
        var settings = Store.Load().StreamDeck;
        var instance = settings.Instances[DeckInstanceResolver.WidgetInstanceId(deck.Id)];
        var preset = settings.Presets.Single(p => p.Id == instance.ActivePresetId);
        Assert.Equal((Nexus2MigrationService.DockPresetName, 4, 1), (preset.Name, preset.Cols, preset.Rows));
        var slots = preset.Deck.Pages.Single().Slots;
        Assert.Equal(new[] { "openFile", "openUrl", null, null }, slots.Select(s => s.Action?.Type));
    }

    [Fact]
    public async Task ReImport_ReplacesTheDeckInsteadOfDuplicatingIt()
    {
        var y70 = Panels.Allocate(null, new PanelDeviceCapabilities { Surface = PanelSurfaces.Y70 });
        Assert.Equal("applied", await Apply(false));
        Assert.Equal("needsConfirm", await Apply(false));
        Assert.Equal("applied", await Apply(true));
        Assert.Equal("applied", await Apply(true));

        var deck = Assert.Single(Decks(y70.Id));
        var settings = Store.Load().StreamDeck;
        Assert.Single(settings.Presets, p => p.Name == Nexus2MigrationService.DockPresetName);
        Assert.Equal(
            settings.Presets.Single(p => p.Name == Nexus2MigrationService.DockPresetName).Id,
            settings.Instances[DeckInstanceResolver.WidgetInstanceId(deck.Id)].ActivePresetId);
    }
}
