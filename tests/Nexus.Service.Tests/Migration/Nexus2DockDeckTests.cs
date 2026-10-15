using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Nexus.Service.Deck;
using Nexus.Service.Migration;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Xunit;

namespace Nexus.Service.Tests.Migration;

/// <summary>The Nexus 2 Y70 dock imported as a 4x1 deck. The dock shape is a scrubbed copy of a real
/// Nexus 2 config: three shortcut macros and a blocks widget, all 1x1.</summary>
public sealed class Nexus2DockDeckTests
{
    private const string RealShapeDock = """
        [
          { "id": "a", "isImmersive": false, "position": 0, "positionHorizontal": 0, "size": "1x1", "type": "macros",
            "macro": { "type": "shortcut", "targetId": "C:\\WINDOWS\\explorer.exe" }, "front": { "type": "default" },
            "title": { "value": "", "position": "top", "size": 12, "bold": false, "italic": false, "underline": false, "color": "#ffffff" } },
          { "id": "b", "position": 1, "positionHorizontal": 1, "size": "1x1", "type": "macros",
            "macro": { "type": "shortcut", "targetId": "C:\\WINDOWS\\System32\\Taskmgr.exe" }, "front": { "type": "default" },
            "title": { "value": "", "position": "top", "size": 12 } },
          { "id": "c", "position": 2, "positionHorizontal": 2, "size": "1x1", "type": "macros",
            "macro": { "type": "shortcut", "targetId": "Microsoft.Paint_8wekyb3d8bbwe!App" }, "front": { "type": "default" },
            "title": { "value": "", "position": "top", "size": 12 } },
          { "id": "d", "position": 3, "positionHorizontal": 3, "size": "1x1", "type": "blocks" }
        ]
        """;

    private static JsonElement Y70(string dock, string pagesJson = "[]", string extra = "")
    {
        var text = "{ \"hideDock\": false," + extra + " \"dock\": " + dock + ", \"pages\": " + pagesJson + " }";
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static string Page(params string[] widgets) => "[{ \"widgets\": [" + string.Join(",", widgets) + "] }]";

    private static string Widget(string type, string size) => "{ \"type\": \"" + type + "\", \"size\": \"" + size + "\" }";

    private static PanelWidgetDto? DeckOf(Nexus2Y70LayoutResult r) =>
        r.Layout?.Pages.SelectMany(p => p.Widgets).SingleOrDefault(w => w.Type == "deck");

    private static List<DeckSlot> Keys(Nexus2Y70LayoutResult r) => r.Dock!.Config.Pages.SelectMany(p => p.Slots).ToList();

    [Fact]
    public void RealDock_BecomesA4x1DeckAlongTheBottomRowOfPageOne()
    {
        var result = Nexus2Y70Translator.TranslateLayout(Y70(RealShapeDock, Page(Widget("clock", "2x2"))), _ => false);

        var deck = DeckOf(result)!;
        Assert.Equal("4x1", deck.Size);
        Assert.Equal((0, Y70LayoutPlacement.GridRows - 1), (deck.Col, deck.Row));
        Assert.Same(result.Layout!.Pages[0].Widgets.Single(w => w.Type == "deck"), deck);
        Assert.Equal(deck.Id, result.Dock!.WidgetId);
        Assert.Equal(4, result.Dock.Items);
        Assert.Equal(3, result.Dock.MappedItems);
    }

    [Fact]
    public void RealDock_MapsShortcutsToKeysAndTheBlocksWidgetToAnEmptyKey()
    {
        var keys = Keys(Nexus2Y70Translator.TranslateLayout(Y70(RealShapeDock), _ => false));

        Assert.Equal(4, keys.Count);
        Assert.Equal(new[] { "openFile", "openFile", "launchApp" }, keys.Take(3).Select(k => k.Action!.Type));
        Assert.Null(keys[3].Action);
        Assert.Null(keys[3].Label);
    }

    [Fact]
    public void ADockWithoutAnyKeyActionLeavesNoDeck()
    {
        var blocksOnly = """[{ "type": "blocks", "position": 0 }, { "type": "clock", "size": "1x1", "position": 1 }]""";
        Assert.Null(Nexus2Y70Translator.TranslateLayout(Y70(blocksOnly), _ => false).Dock);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    public void EmptyDock_AddsNothingAndLeavesTheOtherWidgetsWhereTheyWere(string dock)
    {
        var pages = Page(Widget("clock", "2x2"), Widget("weather", "4x2"));
        var without = Nexus2Y70Translator.TranslateLayout(Y70(dock, pages), _ => false);

        Assert.Null(without.Dock);
        Assert.Null(DeckOf(without));
        Assert.Equal(2, without.Layout!.Pages[0].Widgets.Count);
    }

    [Fact]
    public void MissingDockAndHiddenDock_AddNothing()
    {
        var noDock = JsonDocument.Parse("""{ "pages": [] }""").RootElement;
        Assert.Null(Nexus2Y70Translator.TranslateLayout(noDock, _ => false).Dock);

        var hidden = JsonDocument.Parse("{ \"hideDock\": true, \"dock\": " + RealShapeDock + ", \"pages\": [] }").RootElement;
        Assert.Null(Nexus2Y70Translator.TranslateLayout(hidden, _ => false).Dock);
    }

    [Fact]
    public void DockAlone_YieldsALayoutHoldingJustTheDeck()
    {
        var result = Nexus2Y70Translator.TranslateLayout(Y70(RealShapeDock), _ => false);

        Assert.Single(result.Layout!.Pages);
        Assert.Equal("deck", result.Layout.Pages[0].Widgets.Single().Type);
    }

    [Fact]
    public void OtherWidgetsKeepTheirCellsWithOrWithoutADock()
    {
        var pages = Page(Widget("clock", "2x2"), Widget("weather", "4x2"), Widget("media", "4x4"));
        var with = Nexus2Y70Translator.TranslateLayout(Y70(RealShapeDock, pages), _ => false);
        var without = Nexus2Y70Translator.TranslateLayout(Y70("[]", pages), _ => false);

        (string, int, int) Cell(PanelWidgetDto w) => (w.Type, w.Col, w.Row);
        Assert.Equal(
            without.Layout!.Pages.SelectMany(p => p.Widgets).Select(Cell),
            with.Layout!.Pages.SelectMany(p => p.Widgets).Where(w => w.Type != "deck").Select(Cell));
        Assert.Equal(without.Widgets, with.Widgets);
        Assert.Equal(without.MappedWidgets, with.MappedWidgets);
    }

    [Fact]
    public void AnOccupiedBottomRow_SendsTheDeckThroughTheNormalFirstFreeCellScan()
    {
        // Four 4x4 tiles fill all 16 rows of page 1.
        var tiles = Page(Widget("media", "4x4"), Widget("media", "4x4"), Widget("media", "4x4"), Widget("media", "4x4"));
        var result = Nexus2Y70Translator.TranslateLayout(Y70(RealShapeDock, tiles), _ => false);

        Assert.Equal(2, result.Layout!.Pages.Count);
        Assert.Equal(4, result.Layout.Pages[0].Widgets.Count);
        var deck = result.Layout.Pages[1].Widgets.Single();
        Assert.Equal(("deck", 0, 0), (deck.Type, deck.Col, deck.Row));
    }

    [Fact]
    public void WebsiteAndAudioMacros_MapToOpenUrlAndPlayAudio()
    {
        var dock = """
            [
              { "type": "macros", "positionHorizontal": 0, "macro": { "type": "website", "url": "https://example.com/" },
                "title": { "value": "Docs" } },
              { "type": "macros", "positionHorizontal": 1, "macro": { "type": "audio", "clipPath": "clips/ding.mp3", "loop": true, "volume": 40 } },
              { "type": "macros", "positionHorizontal": 2, "macro": { "type": "audio", "clipPath": "clips/gone.mp3", "volume": 40 } },
              { "type": "macros", "positionHorizontal": 3, "macro": { "type": "website", "url": "" } }
            ]
            """;
        var keys = Keys(Nexus2Y70Translator.TranslateLayout(Y70(dock), p => p == "clips/ding.mp3"));

        Assert.Equal(("openUrl", "https://example.com/", "Docs"), (keys[0].Action!.Type, keys[0].Action!.Url, keys[0].Label));
        Assert.Equal(("playAudio", "clips/ding.mp3", 40), (keys[1].Action!.Type, keys[1].Action!.Path, keys[1].Action!.Volume));
        Assert.Null(keys[2].Action);
        Assert.Null(keys[3].Action);
    }

    [Fact]
    public void KeystrokeRecordings_MapOnlyWhenTheyAreOneKeyPress()
    {
        static string Rec(params string[] strokes) =>
            "{ \"type\": \"macros\", \"macro\": { \"type\": \"recording\", \"strokes\": [" + string.Join(",", strokes) + "] } }";
        static string Down(string key, string mods = "") => "{ \"key\": \"" + key + "\", \"type\": \"keydown\"" + mods + " }";
        static string Up(string key) => "{ \"key\": \"" + key + "\", \"type\": \"keyup\" }";

        var dock = "[" + string.Join(",",
            Rec(Down("KeyA", ", \"ctrl\": true, \"shift\": true"), Up("KeyA")),
            Rec(Down("F5"), Up("F5")),
            Rec(Down("KeyA"), Up("KeyA"), Down("KeyB"), Up("KeyB")),
            Rec(Down("ArrowLeft"), Up("ArrowLeft")),
            Rec(Down("NumpadAdd"), Up("NumpadAdd"))) + "]";
        var keys = Keys(Nexus2Y70Translator.TranslateLayout(Y70(dock), _ => false));

        Assert.Equal(("hotkey", "ctrl+shift+a"), (keys[0].Action!.Type, keys[0].Action!.Keys));
        Assert.Equal("f5", keys[1].Action!.Keys);
        Assert.Null(keys[2].Action);
        Assert.Equal("left", keys[3].Action!.Keys);
        Assert.Null(keys[4].Action);
        Assert.NotNull(DeckActionExecutor.ParseHotkey(keys[0].Action!.Keys!));
        Assert.NotNull(DeckActionExecutor.ParseHotkey(keys[1].Action!.Keys!));
    }

    [Fact]
    public void KeysFollowTheDockOrderAndAFullDockPagesOntoTheStripWithNavigationKeys()
    {
        static string Item(int h, string target) =>
            "{ \"type\": \"macros\", \"position\": 0, \"positionHorizontal\": " + h + ", \"macro\": { \"type\": \"shortcut\", \"targetId\": \"" + target + "\" } }";
        var dock = "[" + string.Join(",", Item(5, "f!"), Item(0, "a!"), Item(1, "b!"), Item(2, "c!"), Item(3, "d!"), Item(4, "e!")) + "]";
        var result = Nexus2Y70Translator.TranslateLayout(Y70(dock), _ => false);

        Assert.Equal(2, result.Dock!.Rows);
        Assert.Equal(new[] { "a!", "b!", "c!", "d!", "e!", "f!" }, Keys(result).Where(k => k.Action is not null).Select(k => k.Action!.AppId));

        // On the 4x1 widget every key is reachable: the fit adds page keys and spreads the rest over pages.
        var fitted = DeckConfigNavigation.FitToGrid(4, result.Dock.Rows, result.Dock.Config, 4, 1, DeckTargetKind.Widget);
        Assert.True(fitted.Pages.Count > 1);
        var reachable = fitted.Pages.SelectMany(p => p.Slots).Where(k => k.Action?.Type == "launchApp").Select(k => k.Action!.AppId);
        Assert.Equal(new[] { "a!", "b!", "c!", "d!", "e!", "f!" }, reachable);
        Assert.Contains(fitted.Pages[0].Slots, k => k.Action?.Type == "page" || k.Auto == true);
    }

    [Fact]
    public void ExePathShortcutsOpenAsFilesAndStoreAppIdsLaunchAsApps()
    {
        var keys = Keys(Nexus2Y70Translator.TranslateLayout(Y70(RealShapeDock), _ => false));

        Assert.Equal(("openFile", "C:\\WINDOWS\\explorer.exe"), (keys[0].Action!.Type, keys[0].Action!.Path));
        Assert.Equal(("openFile", "C:\\WINDOWS\\System32\\Taskmgr.exe"), (keys[1].Action!.Type, keys[1].Action!.Path));
        Assert.Equal(("launchApp", "Microsoft.Paint_8wekyb3d8bbwe!App"), (keys[2].Action!.Type, keys[2].Action!.AppId));
    }

    [Fact]
    public void AWebsiteWithoutASchemeGetsHttps()
    {
        var dock = """[{ "type": "macros", "macro": { "type": "website", "url": "example.com/docs" } }]""";
        Assert.Equal("https://example.com/docs", Keys(Nexus2Y70Translator.TranslateLayout(Y70(dock), _ => false))[0].Action!.Url);
    }
}
