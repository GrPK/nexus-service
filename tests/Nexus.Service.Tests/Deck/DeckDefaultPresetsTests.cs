using System;
using System.IO;
using System.Linq;
using Nexus.Service.Deck;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Deck;

public sealed class DeckDefaultPresetsTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "nexus-test-deckdef-" + Guid.NewGuid().ToString("N")[..8]);
    private string SettingsPath => Path.Combine(_tempDir, "settings.json");

    public DeckDefaultPresetsTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static string KeyOf(DeckSlot slot) =>
        slot.Action?.Type == "power" ? slot.Action.PowerAction! : slot.Action?.SystemAction?.Op ?? "";

    private static string[] Keys(DeckPreset preset) =>
        preset.Deck.Pages[0].Slots.Where(s => s.Action is not null).Select(KeyOf).ToArray();

    [Fact]
    public void SeedOnce_AddsThreeFourKeyPresetsInOrder()
    {
        var settings = new NexusSettings();

        Assert.True(DeckDefaultPresets.SeedOnce(settings));

        var presets = settings.StreamDeck.Presets;
        Assert.Equal(["Essentials", "Media", "System"], presets.Select(p => p.Name));
        Assert.Equal(["volumeDown", "volumeUp", "screenshot", "taskManager"], Keys(presets[0]));
        Assert.Equal(["mediaPrev", "mediaPlayPause", "mediaNext", "muteToggle"], Keys(presets[1]));
        Assert.Equal(["lock", "sleep", "screenRecord", "openSettings"], Keys(presets[2]));
        Assert.All(presets, p =>
        {
            Assert.Equal(4, p.Deck.Pages[0].Slots.Count);
            Assert.Equal(4, p.Cols * p.Rows);
            Assert.False(DeckLayoutPolicy.PrivilegedActions(p.Deck).Any());
        });
        Assert.True(settings.DeckDefaultPresetsSeeded);
    }

    // Pinned to nexus-web src/panel/widgets/deck/deckPreviewData.test.ts: the Add widget preview shows these keys, in this order.
    [Fact]
    public void Essentials_KeysMatchTheAddWidgetPreview()
    {
        var settings = new NexusSettings();
        DeckDefaultPresets.SeedOnce(settings);

        Assert.Equal(["volumeDown", "volumeUp", "screenshot", "taskManager"], Keys(settings.StreamDeck.Presets[0]));
    }

    [Fact]
    public void SeedOnce_NeverIncludesShutdownRestartOrLogout()
    {
        var settings = new NexusSettings();
        DeckDefaultPresets.SeedOnce(settings);

        var power = settings.StreamDeck.Presets.SelectMany(p => p.Deck.Pages[0].Slots)
            .Where(s => s.Action?.Type == "power").Select(s => s.Action!.PowerAction).ToList();
        Assert.DoesNotContain("shutdown", power);
        Assert.DoesNotContain("restart", power);
        Assert.DoesNotContain("logout", power);
    }

    [Fact]
    public void SeedOnce_KeepsExistingPresetsUntouchedAndAppends()
    {
        var settings = new NexusSettings();
        settings.StreamDeck.Presets.Add(new DeckPreset { Id = "mine", Name = "Mine", Cols = 5, Rows = 3 });

        DeckDefaultPresets.SeedOnce(settings);

        Assert.Equal("mine", settings.StreamDeck.Presets[0].Id);
        Assert.Equal(4, settings.StreamDeck.Presets.Count);
    }

    [Fact]
    public void SeedOnce_NameCollisionGetsASuffix()
    {
        var settings = new NexusSettings();
        settings.StreamDeck.Presets.Add(new DeckPreset { Id = "mine", Name = "media", Cols = 2, Rows = 2 });

        DeckDefaultPresets.SeedOnce(settings);

        Assert.Contains(settings.StreamDeck.Presets, p => p.Id == DeckDefaultPresets.MediaId && p.Name == "Media (default)");
    }

    [Fact]
    public void SeedOnce_IsOnceOnly_ADeletedPresetStaysDeleted()
    {
        var settings = new NexusSettings();
        DeckDefaultPresets.SeedOnce(settings);
        settings.StreamDeck.Presets.RemoveAll(p => p.Id == DeckDefaultPresets.MediaId);

        Assert.False(DeckDefaultPresets.SeedOnce(settings));

        Assert.Equal(2, settings.StreamDeck.Presets.Count);
    }

    [Fact]
    public void SeedOnce_IsOnceOnly_AnEditedPresetIsNotOverwritten()
    {
        var settings = new NexusSettings();
        DeckDefaultPresets.SeedOnce(settings);
        var essentials = settings.StreamDeck.Presets[0];
        essentials.Name = "Mine now";
        essentials.Deck.Pages[0].Slots[0] = new DeckSlot();

        DeckDefaultPresets.SeedOnce(settings);

        Assert.Equal("Mine now", essentials.Name);
        Assert.Null(essentials.Deck.Pages[0].Slots[0].Action);
    }

    [Fact]
    public void SeedOnce_StopsAtThePresetCapButStillSetsTheFlag()
    {
        var settings = new NexusSettings();
        for (var i = 0; i < Nexus.Service.Routes.DeckRoutes.PresetCap - 1; i++)
        {
            settings.StreamDeck.Presets.Add(new DeckPreset { Id = $"p{i}", Name = $"P{i}", Cols = 2, Rows = 2 });
        }

        DeckDefaultPresets.SeedOnce(settings);

        Assert.Equal(Nexus.Service.Routes.DeckRoutes.PresetCap, settings.StreamDeck.Presets.Count);
        Assert.Equal(DeckDefaultPresets.EssentialsId, settings.StreamDeck.Presets[^1].Id);
        Assert.True(settings.DeckDefaultPresetsSeeded);
    }

    [Theory]
    [InlineData(4, 1)]
    [InlineData(2, 2)]
    [InlineData(4, 2)]
    [InlineData(4, 4)]
    [InlineData(2, 4)]
    public void EssentialsFor_PutsTheFourKeysFirstAndPadsToTheGrid(int cols, int rows)
    {
        var (c, r, deck) = DeckDefaultPresets.EssentialsFor(cols, rows);

        Assert.Equal((cols, rows), (c, r));
        var slots = Assert.Single(deck.Pages).Slots;
        Assert.Equal(cols * rows, slots.Count);
        Assert.Equal(["volumeDown", "volumeUp", "screenshot", "taskManager"], slots.Take(4).Select(KeyOf));
        Assert.All(slots.Skip(4), s => Assert.Null(s.Action));
    }

    [Fact]
    public void EssentialsFor_AGridUnderFourKeysFallsBackToTwoByTwo()
    {
        var (c, r, deck) = DeckDefaultPresets.EssentialsFor(1, 2);

        Assert.Equal((2, 2), (c, r));
        Assert.Equal(4, deck.Pages[0].Slots.Count(s => s.Action is not null));
    }

    [Fact]
    public void EssentialsFor_ReadsInOrderOnA4x1StripAnd2x2()
    {
        var seeded = new NexusSettings();
        DeckDefaultPresets.SeedOnce(seeded);
        var essentials = seeded.StreamDeck.Presets[0];

        var strip = DeckConfigNavigation.FitToGrid(essentials.Cols, essentials.Rows, essentials.Deck, 4, 1, DeckTargetKind.Widget);
        var square = DeckConfigNavigation.FitToGrid(essentials.Cols, essentials.Rows, essentials.Deck, 2, 2, DeckTargetKind.Widget);

        Assert.Equal(["volumeDown", "volumeUp", "screenshot", "taskManager"], strip.Pages[0].Slots.Select(KeyOf));
        Assert.Equal(strip.Pages[0].Slots.Select(KeyOf), square.Pages[0].Slots.Select(KeyOf));
        Assert.Single(strip.Pages);
    }

    [Fact]
    public void SeedOnce_ThroughTheStore_PersistsOnceAndRespectsALaterDelete()
    {
        var store = new Nexus.Service.Tests.InMemoryConfigStore();

        DeckDefaultPresets.SeedOnce(store);
        Assert.Equal(3, store.Load().StreamDeck.Presets.Count);
        Assert.True(store.Load().DeckDefaultPresetsSeeded);

        store.Update(s => s.StreamDeck.Presets.Clear());
        DeckDefaultPresets.SeedOnce(store);

        Assert.Empty(store.Load().StreamDeck.Presets);
    }

    [Fact]
    public void SeedOnce_FlagSurvivesAJsonRoundTrip()
    {
        File.WriteAllText(SettingsPath, $$"""
        {
          "schemaVersion": {{NexusSettings.CurrentSchemaVersion}},
          "deckDefaultPresetsSeeded": true,
          "streamDeck": { "presets": [ { "id": "mine", "name": "Mine", "cols": 5, "rows": 3 } ] }
        }
        """);

        using var store = new JsonConfigStore(SettingsPath);
        DeckDefaultPresets.SeedOnce(store);

        Assert.Equal(["Mine"], store.Load().StreamDeck.Presets.Select(p => p.Name));
    }
}
