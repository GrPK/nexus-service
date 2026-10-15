using System.Collections.Generic;
using Nexus.Service.Persistence;

namespace Nexus.Service.Deck;

/// <summary>
/// The built-in starter keys: the Essentials set a new deck widget starts with,
/// and the three presets seeded into every install's preset list once.
/// Every set has exactly four keys, the smallest deck (4x1), and is authored
/// as a 2x2 so it reads in order as a strip and as a square.
/// </summary>
public static class DeckDefaultPresets
{
    public const string EssentialsId = "default-essentials";
    public const string MediaId = "default-media";
    public const string SystemId = "default-system";

    private const int SeedCols = 2;
    private const int SeedRows = 2;

    /// <summary>Seeds through the store, persisting only when something was added.</summary>
    public static void SeedOnce(IConfigStore store)
    {
        if (!store.Load().DeckDefaultPresetsSeeded)
        {
            store.Update(s => SeedOnce(s));
        }
    }

    /// <summary>
    /// Adds the three default presets to the list when the install never had
    /// them seeded, then sets the flag; true when it changed anything. The
    /// flag is what keeps a preset the user deleted or edited from coming back.
    /// </summary>
    public static bool SeedOnce(NexusSettings settings)
    {
        if (settings.DeckDefaultPresetsSeeded)
        {
            return false;
        }
        settings.DeckDefaultPresetsSeeded = true;
        AddPreset(settings, EssentialsId, "Essentials", EssentialsKeys());
        AddPreset(settings, MediaId, "Media", [SystemKey("mediaPrev"), SystemKey("mediaPlayPause"), SystemKey("mediaNext"), SystemKey("muteToggle")]);
        AddPreset(settings, SystemId, "System", [PowerKey("lock"), PowerKey("sleep"), SystemKey("screenRecord", "Disc"), SystemKey("openSettings")]);
        return true;
    }

    /// <summary>The Essentials keys first, remaining slots empty, on a cols x rows grid; a grid under four keys falls back to 2x2 so no key is dropped.</summary>
    public static (int Cols, int Rows, DeckConfig Deck) EssentialsFor(int cols, int rows)
    {
        if (cols * rows < 4)
        {
            (cols, rows) = (SeedCols, SeedRows);
        }
        return (cols, rows, ConfigOf(EssentialsKeys(), cols * rows));
    }

    private static void AddPreset(NexusSettings settings, string id, string name, List<DeckSlot> keys)
    {
        settings.StreamDeck.Presets.Add(new DeckPreset
        {
            Id = id,
            Name = DeckModesMigration.UniqueName(settings.StreamDeck.Presets, name, "default"),
            Cols = SeedCols,
            Rows = SeedRows,
            Deck = ConfigOf(keys, SeedCols * SeedRows),
            TemplateId = id,
        });
    }

    private static DeckConfig ConfigOf(List<DeckSlot> keys, int capacity)
    {
        while (keys.Count < capacity)
        {
            keys.Add(new DeckSlot());
        }
        var config = DeckConfigNavigation.EmptyConfig();
        config.Pages[0].Slots = keys;
        return config;
    }

    private static List<DeckSlot> EssentialsKeys() =>
        [SystemKey("volumeDown"), SystemKey("volumeUp"), SystemKey("screenshot"), SystemKey("taskManager")];

    private static DeckSlot SystemKey(string op, string? icon = null) => new()
    {
        Action = new DeckAction { Type = "system", SystemAction = new DeckSystemAction { Op = op } },
        Icon = icon is null ? null : new DeckIcon { Kind = "lucide", Value = icon },
    };

    private static DeckSlot PowerKey(string power) => new()
    {
        Action = new DeckAction { Type = "power", PowerAction = power },
    };
}
