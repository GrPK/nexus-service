using System;
using System.Collections.Generic;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// L-Connect 2.1.29's Quick Sync ("Sync all") for the six effects it plays on every device
/// (QuickSyncLighting MainPage.applyLightingSettings): each device gets its own effect of the same
/// name, with the colours, speed, brightness and direction carried over as L-Connect carries them
/// (applyToTLV2Fan / applyToTLFlexFan, applyToCurve, applyToUniversalScreen8p8Inch). While sync is
/// on these looks stand in for each device's own; turning it off brings the device's own back.
/// </summary>
public static class LianLiSync
{
    public const string Rainbow = "rainbow";
    public const string RainbowMorph = "rainbowMorph";
    public const string Static = "static";
    public const string Breathing = "breathing";
    public const string Meteor = "meteor";
    public const string Runway = "runway";

    /// <summary>Top of the speed and brightness levels: L-Connect's sliders at 0, 25, 50, 75 and 100.</summary>
    public const int MaxLevel = 4;

    /// <summary>One Quick Sync effect: what the page shows for it and its defaults (QuickSyncLightingProfile.defaultLightingSettings).</summary>
    public sealed record EffectInfo(string Key, bool HasSpeed, bool HasDirection, int DefaultSpeed, IReadOnlyList<string> DefaultColors);

    /// <summary>The effects in L-Connect's order (QuickSyncLightingEffectMode Rainbow..Runway).</summary>
    public static readonly IReadOnlyList<EffectInfo> Effects = new EffectInfo[]
    {
        new(Rainbow, HasSpeed: true, HasDirection: true, DefaultSpeed: 3, Array.Empty<string>()),
        new(RainbowMorph, HasSpeed: true, HasDirection: false, DefaultSpeed: 3, Array.Empty<string>()),
        new(Static, HasSpeed: false, HasDirection: false, DefaultSpeed: 3, new[] { "#00FFAA", "#FF0000", "#96FF00", "#FFD700" }),
        new(Breathing, HasSpeed: true, HasDirection: false, DefaultSpeed: 3, new[] { "#00FFAA", "#FF0000", "#96FF00", "#FFD700" }),
        new(Meteor, HasSpeed: true, HasDirection: true, DefaultSpeed: 3, new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }),
        new(Runway, HasSpeed: true, HasDirection: false, DefaultSpeed: 2, new[] { "#FF0000", "#00FFAA" }),
    };

    public static EffectInfo? Find(string? key)
    {
        foreach (var e in Effects)
        {
            if (e.Key == key)
            {
                return e;
            }
        }
        return null;
    }

    /// <summary>The look <paramref name="effect"/> was last given, or its defaults.</summary>
    public static LianLiSyncLook LookOf(LianLiSyncSettings sync, string effect)
    {
        if (sync.Looks.TryGetValue(effect, out var look))
        {
            return look;
        }
        var info = Find(effect);
        return new LianLiSyncLook
        {
            Speed = info?.DefaultSpeed ?? 3,
            Brightness = MaxLevel,
            Direction = 0,
            Colors = new List<string>(info?.DefaultColors ?? Array.Empty<string>()),
        };
    }

    /// <summary>
    /// A TL chain's whole-fan look under sync (applyToTLV2Fan / applyToTLFlexFan: scope TLAll, the
    /// same TL effect, every colour, the chain's own up/down swap kept by RfDevice.SetTLEffectType)
    /// and L-Connect's speed step for it: LWirelessUtils.ValueToRFLampEffectSpeedType of the slider,
    /// where Static, sent with no speed, reads as L3.
    /// </summary>
    public static (LianLiWirelessChainLighting Lighting, int SpeedStep)? TlLighting(LianLiSyncSettings sync, LianLiWirelessChainLighting? own)
    {
        if (Find(sync.Effect) is not { } info)
        {
            return null;
        }
        var look = LookOf(sync, info.Key);
        var lighting = new LianLiWirelessChainLighting
        {
            Mode = info.Key,
            EffectMode = info.Key,
            Speed = Math.Clamp(look.Speed, 0, MaxLevel),
            Direction = info.HasDirection ? Math.Clamp(look.Direction, 0, 1) : 0,
            Brightness = Math.Clamp(look.Brightness, 0, MaxLevel),
            Colors = new List<string>(look.Colors),
            UpDownSwap = own?.UpDownSwap ?? false,
        };
        return (lighting, info.HasSpeed ? 7 - lighting.Speed : 5);
    }

    /// <summary>
    /// The OLED Curved edge under sync, or its own settings (applyToCurve: one colour for Static and
    /// Breathing, two for Runway, six for Meteor taken round the list, none for the rainbows; reverse
    /// only for "left").
    /// </summary>
    public static HydroShift2CurveLightingSettings CurveLighting(NexusSettings settings)
    {
        var sync = settings.Devices.LianLiSync;
        if (!sync.Enabled || Find(sync.Effect) is not { } info)
        {
            return settings.Devices.HydroShift2CurveLighting;
        }
        var look = LookOf(sync, info.Key);
        var count = info.Key switch
        {
            Rainbow or RainbowMorph => 0,
            Static or Breathing => 1,
            Runway => 2,
            _ => 6,
        };
        var colors = new List<string>(count);
        for (var i = 0; i < count && look.Colors.Count > 0; i++)
        {
            colors.Add(look.Colors[i < look.Colors.Count ? i : i % look.Colors.Count]);
        }
        return new HydroShift2CurveLightingSettings
        {
            Mode = info.Key,
            EffectMode = info.Key,
            Speed = Math.Clamp(look.Speed, 0, MaxLevel),
            Direction = info.HasDirection ? Math.Clamp(look.Direction, 0, 1) : 0,
            Brightness = Math.Clamp(look.Brightness, 0, MaxLevel),
            Colors = colors,
        };
    }

    /// <summary>
    /// The Universal Screen 8.8 bezel under sync, or its own settings (applyToUniversalScreen8p8Inch:
    /// one colour for Static and Breathing, two for Runway, Meteor's first three each twice, none for
    /// the rainbows). Static goes with no speed, which the screen's service maps to its slowest step.
    /// </summary>
    public static UniversalScreen88LightingSettings Screen88Lighting(NexusSettings settings)
    {
        var sync = settings.Devices.LianLiSync;
        if (!sync.Enabled || Find(sync.Effect) is not { } info)
        {
            return settings.Devices.UniversalScreen88Lighting;
        }
        var look = LookOf(sync, info.Key);
        var c = look.Colors;
        string At(int i) => c.Count == 0 ? "#000000" : c[Math.Min(i, c.Count - 1)];
        var colors = info.Key switch
        {
            Static or Breathing => new List<string> { At(0) },
            Runway => new List<string> { At(0), At(1) },
            Meteor => new List<string> { At(0), At(0), At(1), At(1), At(2), At(2) },
            _ => new List<string>(),
        };
        return new UniversalScreen88LightingSettings
        {
            Mode = info.Key,
            EffectMode = info.Key,
            Speed = info.HasSpeed ? Math.Clamp(look.Speed, 0, MaxLevel) : 0,
            Direction = info.HasDirection ? Math.Clamp(look.Direction, 0, 1) : 0,
            Brightness = Math.Clamp(look.Brightness, 0, MaxLevel),
            Colors = colors,
        };
    }
}
