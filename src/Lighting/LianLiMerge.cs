using System;
using System.Collections.Generic;
using System.Globalization;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// L-Connect 2.1.29's merged Quick Sync effects (QuickSyncLighting MainPage.applyToWirelessMergeLightingEffect,
/// then MergeLightingEffectSettingController.ApplyWMergeLightingEffect and LightingEffectUtil.SetAllEffect):
/// one effect drawn over a line of UI dots that runs through every device in the merge order, each device
/// taking its own stretch (a TL chain 13 dots per fan, the OLED Curved 14, the 8.8 bezel 31), read in its
/// own merge direction. RainbowMorph and Twinkle are not drawn on the line: each device plays its own.
/// </summary>
public static class LianLiMerge
{
    public const string KindTl = "tlChain";
    public const string KindCurve = "hydroShift2Curve";
    public const string KindScreen88 = "universalScreen88";

    /// <summary>One merged effect: its L-Connect UIEffect number and Quick Sync's defaults for it.</summary>
    public sealed record EffectInfo(string Key, int UiEffect, bool HasSpeed, bool HasDirection, int DefaultSpeed, IReadOnlyList<string> DefaultColors);

    /// <summary>A device on the line, in merge order: its kind, id, dots and merge direction.</summary>
    public sealed record Participant(string Kind, string Id, int FanCount, int Dots, int Direction);

    /// <summary>One device's share: a TL chain's upload, or the frames a USB device streams and their period.</summary>
    public sealed class Output
    {
        public Slv3StrimerAnimation? Tl { get; init; }
        public IReadOnlyList<byte[]>? Frames { get; init; }
        public int PeriodMs { get; init; }
    }

    private static readonly string[] None = Array.Empty<string>();

    /// <summary>The 19 merged effects in Quick Sync's order (QuickSyncLightingEffectMode RainbowWireless..EchoWireless), defaults from defaultWirelessLightingSettings.</summary>
    public static readonly IReadOnlyList<EffectInfo> Effects = new EffectInfo[]
    {
        new("mergeRainbow", 1, true, true, 3, None),
        new("mergeRainbowMorph", 2, true, false, 3, None),
        new("mergeStatic", 3, false, false, 3, new[] { "#00D7FF" }),
        new("mergeBreathing", 4, true, false, 3, new[] { "#AA00FF" }),
        new("mergeMeteor", 6, true, true, 3, new[] { "#00D7FF" }),
        new("mergeRunway", 5, true, false, 2, new[] { "#FF0000", "#00FFAA" }),
        new("mergeStack", 7, true, true, 2, new[] { "#0000FF" }),
        new("mergeTwinkle", 8, true, false, 2, None),
        new("mergeColorCycle", 9, true, true, 3, new[] { "#0000FF", "#FF0000", "#FFFF00" }),
        new("mergeCoverCycle", 10, true, true, 3, new[] { "#0000FF", "#FF0000" }),
        new("mergeWave", 11, true, true, 3, new[] { "#8600FF" }),
        new("mergeMeteorShower", 12, true, true, 3, new[] { "#FF0080", "#0000E3", "#F9F900", "#00EC00" }),
        new("mergeDisco", 16, true, true, 3, new[] { "#FF0000", "#008000", "#0000FF", "#FFFF00" }),
        new("mergeBlowUp", 21, true, true, 3, new[] { "#FF0000", "#008000" }),
        new("mergeHeartBeat", 27, true, true, 3, new[] { "#7B0037", "#FF7DFF" }),
        new("mergeWarning", 22, true, true, 3, new[] { "#FFFF00", "#00FFFF" }),
        new("mergeSeaFlow", 29, true, true, 3, new[] { "#000D9B", "#FFFFFF" }),
        new("mergeRipple", 30, true, true, 3, new[] { "#800036", "#00FFFF" }),
        new("mergeEcho", 26, true, true, 3, new[] { "#000000", "#00FFFF" }),
    };

    private static readonly int[] BrightnessLevels = { 0, 64, 128, 192, 255 };

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

    /// <summary>LWirelessUtils.ValueToRFLampEffectSpeedType of the slider; an effect without speed is sent with none, read as L3.</summary>
    public static int SpeedType(EffectInfo effect, LianLiSyncLook look) => effect.HasSpeed ? 7 - Math.Clamp(look.Speed, 0, LianLiSync.MaxLevel) : 5;

    /// <summary>LWirelessUtils.ValueToRFBrightnessType of the slider.</summary>
    public static int BrightnessType(LianLiSyncLook look) => BrightnessLevels[Math.Clamp(look.Brightness, 0, LianLiSync.MaxLevel)];

    /// <summary>Ms per frame: the speed step times 12, or 18 for HeartBeat..Ripple (UIEffects 27-30, LightingEffectUtil.iDefSpeed).</summary>
    public static int IntervalMs(EffectInfo effect, int speedType) => speedType * (effect.UiEffect is >= 27 and <= 30 ? 18 : 12);

    /// <summary>
    /// The USB devices stream one frame every 0.625 of the interval (SetHS2EffectByData /
    /// SetLed88EffectByData), and hold a one-frame effect with an interval of 1600 ms.
    /// </summary>
    public static int UsbPeriodMs(int intervalMs, int frames) => (int)((frames == 1 ? 1600 : intervalMs) * 0.625);

    /// <summary>Every device's share of <paramref name="effect"/>, by participant id.</summary>
    public static IReadOnlyDictionary<string, Output> Render(EffectInfo effect, LianLiSyncLook look, IReadOnlyList<Participant> devices)
    {
        var speed = SpeedType(effect, look);
        var bright = BrightnessType(look);
        var direction = effect.HasDirection ? Math.Clamp(look.Direction, 0, 1) : 0;
        // Only the effect's own colour slots count (Twinkle and RainbowMorph have none in L-Connect).
        var colors = ParseColors(look.Colors, effect.DefaultColors.Count);
        var result = new Dictionary<string, Output>(StringComparer.OrdinalIgnoreCase);
        if (effect.UiEffect is 2 or 8)
        {
            foreach (var d in devices)
            {
                result[d.Id] = OwnEffect(effect.UiEffect == 2, d, look, speed, bright, colors);
            }
            return result;
        }
        var dots = 0;
        foreach (var d in devices)
        {
            dots += d.Dots;
        }
        var (count, line) = LianLiUiEffects.Render(effect.UiEffect, dots, bright, direction, LianLiUiEffects.Palette(colors));
        if (count <= 0)
        {
            return result;
        }
        var interval = IntervalMs(effect, speed);
        var start = 0;
        foreach (var d in devices)
        {
            var stop = start + d.Dots - 1;
            result[d.Id] = d.Kind switch
            {
                KindTl => new Output
                {
                    Tl = Slv3WirelessEffectMath.Finalize(
                        new RawAnimation(LianLiMergeLine.Tl(start, stop, count, line, d.Direction), count, interval), d.FanCount, 26),
                },
                KindCurve => Usb(LianLiMergeLine.Curve(start, stop, count, line, d.Direction), count, LianLiMergeLine.CurveLeds, interval),
                _ => Usb(LianLiMergeLine.Screen88(start, stop, count, line, d.Direction), count, LianLiMergeLine.Screen88Leds, interval),
            };
            start = stop + 1;
        }
        return result;
    }

    /// <summary>
    /// RainbowMorph and Twinkle: each device plays its own (SetAllEffect before the line). A TL chain its
    /// TL effect of that name on the whole fan; the Curve and the bezel their own (RgbEffectType 4 / 14),
    /// all at the merge's speed and brightness.
    /// </summary>
    private static Output OwnEffect(bool morph, Participant d, LianLiSyncLook look, int speed, int bright, IReadOnlyList<(byte R, byte G, byte B)> colors)
    {
        switch (d.Kind)
        {
            case KindTl:
            {
                var tlColors = new List<Nexus.Service.Peripherals.Hyte.Np50.RgbColor>(colors.Count);
                foreach (var c in colors)
                {
                    tlColors.Add(new Nexus.Service.Peripherals.Hyte.Np50.RgbColor(c.R, c.G, c.B));
                }
                var tl = new Slv3TlLook(morph ? "rainbowMorph" : "twinkle", 0, 0, Math.Clamp(look.Brightness, 0, LianLiSync.MaxLevel), tlColors);
                return new Output { Tl = Slv3FanEffects.RenderTl(Slv3FanFamily.Tlv2Lcd, d.FanCount, tl, tl, downSetLast: false, upDownSwap: false, speedStep: speed) };
            }
            case KindCurve:
            {
                var mode = morph ? HydroShift2CurveEdgeEffects.RainbowMorph : HydroShift2CurveEdgeEffects.Twinkle;
                var frames = HydroShift2CurveEdgeEffects.Render(mode, Array.Empty<(byte, byte, byte)>(), bright, false);
                return new Output { Frames = frames, PeriodMs = HydroShift2CurveEdgeEffects.FramePeriodMs(speed) };
            }
            default:
            {
                var mode = morph ? UniversalScreen88LedEffects.RainbowMorph : UniversalScreen88LedEffects.Twinkle;
                var frames = UniversalScreen88LedEffects.Render(mode, Array.Empty<(byte, byte, byte)>(), bright, false);
                return new Output { Frames = frames, PeriodMs = UniversalScreen88LedEffects.FramePeriodMs(mode, speed) };
            }
        }
    }

    private static Output Usb(byte[] packed, int count, int leds, int intervalMs)
    {
        var frames = new List<byte[]>(count);
        for (var f = 0; f < count; f++)
        {
            frames.Add(packed.AsSpan(f * leds * 3, leds * 3).ToArray());
        }
        return new Output { Frames = frames, PeriodMs = UsbPeriodMs(intervalMs, count) };
    }

    private static List<(byte R, byte G, byte B)> ParseColors(IReadOnlyList<string> hex, int slots)
    {
        var colors = new List<(byte, byte, byte)>(slots);
        for (var i = 0; i < hex.Count && i < slots; i++)
        {
            var h = hex[i];
            if (h.Length == 7 && h[0] == '#' && uint.TryParse(h.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            {
                colors.Add(((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            }
        }
        return colors;
    }
}
