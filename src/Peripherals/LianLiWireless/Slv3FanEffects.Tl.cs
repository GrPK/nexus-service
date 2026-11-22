using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.LianLiFlex;
using DeterministicRandom = Nexus.Service.Peripherals.LianLiWireless.Slv3WirelessEffectMath.DeterministicRandom;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>One half's look on a TL chain: an effect key, speed and brightness levels 0..4, direction 0 or 1, and colours.</summary>
public sealed record Slv3TlLook(string Key, int Speed, int Direction, int Brightness, IReadOnlyList<RgbColor> Colors);

public static partial class Slv3FanEffects
{
    // L-Connect's 29 TL effects as its TL v2 page offers them (TLV2SubProfile.DefaultLightingSettings):
    // speed and direction where that page shows them, as many colours as its defaults hold.
    private static Slv3StrimerEffectInfo[] BuildTlCatalog() => new[]
    {
        Tl("rainbow", speed: true, direction: true),
        Tl("rainbowMorph", speed: true, direction: false),
        Tl("static", speed: false, direction: false),
        Tl("breathing", speed: true, direction: false),
        Tl("runway", speed: true, direction: false),
        Tl("meteor", speed: true, direction: true),
        Tl("colorCycle", speed: true, direction: true),
        Tl("staggered", speed: true, direction: false),
        Tl("tide", speed: true, direction: false),
        Tl("mixing", speed: true, direction: false),
        Tl("voice", speed: true, direction: false),
        Tl("door", speed: true, direction: false),
        Tl("render", speed: true, direction: true),
        Tl("ripple", speed: true, direction: false),
        Tl("reflect", speed: true, direction: false),
        Tl("tailChasing", speed: true, direction: true),
        Tl("paint", speed: true, direction: false),
        Tl("pingPong", speed: true, direction: false),
        Tl("stack", speed: true, direction: true),
        Tl("coverCycle", speed: true, direction: true),
        Tl("wave", speed: true, direction: false),
        Tl("racing", speed: true, direction: false),
        Tl("lottery", speed: true, direction: true),
        Tl("intertwine", speed: true, direction: false),
        Tl("meteorShower", speed: true, direction: true),
        Tl("collide", speed: true, direction: false),
        Tl("electricCurrent", speed: true, direction: false),
        Tl("kaleidoscope", speed: true, direction: false),
        Tl("twinkle", speed: true, direction: false),
    };

    private static Slv3StrimerEffectInfo Tl(string key, bool speed, bool direction) => new()
    {
        Key = key,
        HasSpeed = speed,
        HasDirection = direction,
        ColorsMin = 0,
        ColorsMax = FlexSwatchCounts.Of(FlexFamily.TlFlex, key),
    };

    private const int TlHalfLeds = 13;

    /// <summary>The families whose chains are TL fans: 26 LEDs per fan, LED 0..12 the up half and 13..25 the down half.</summary>
    public static bool IsTlFamily(Slv3FanFamily family) =>
        family is Slv3FanFamily.Tlv2Led or Slv3FanFamily.Tlv2Lcd or Slv3FanFamily.TlFlex;

    /// <summary>The colours an effect plays with no picks (L-Connect's TL v2 defaults); empty when it draws its own.</summary>
    public static IReadOnlyList<RgbColor> TlDefaultColors(string key) =>
        FlexDefaultPalettes.RendersFromTable(FlexFamily.TlFlex, key) ? Array.Empty<RgbColor>() : FlexDefaultPalettes.For(FlexFamily.TlFlex, key);

    /// <summary>
    /// Renders a TL chain the way L-Connect's radio code does (RfDevice.SetTLEffectType): the up look
    /// drawn on LED 0..12 of each fan and the down look on 13..25, each on its own by the Flex TL
    /// generators at its own brightness, then joined as RgbEffect.CombingTLRgbData joins them: the
    /// longer animation plays whole and the shorter half is stretched over it. The loop plays at the
    /// speed of the look set last (<paramref name="downSetLast"/>) times the up effect's frame step,
    /// and <paramref name="upDownSwap"/> exchanges the halves (RgbEffect.UpsideDown). Brightness is
    /// in the frames, so they go to the chain unscaled.
    /// </summary>
    /// <param name="speedStep">
    /// L-Connect's speed step (7 slowest .. 3) for the loop, when the sender fixes it rather than the
    /// look set last: Quick Sync sends Static with no speed, which L-Connect reads as L3.
    /// </param>
    public static Slv3StrimerAnimation RenderTl(
        Slv3FanFamily family, int fanCount, Slv3TlLook up, Slv3TlLook down, bool downSetLast, bool upDownSwap, int? speedStep = null)
    {
        if (!IsTlFamily(family))
        {
            throw new ArgumentException($"{family} is not a TL family", nameof(family));
        }
        if (fanCount < 1 || fanCount > MaxFans)
        {
            throw new ArgumentException($"Fan count must be 1..{MaxFans}", nameof(fanCount));
        }
        var upInfo = Find(family, up.Key) ?? throw new ArgumentException($"Unknown fan effect key '{up.Key}' for {family}", nameof(up));
        var downInfo = Find(family, down.Key) ?? throw new ArgumentException($"Unknown fan effect key '{down.Key}' for {family}", nameof(down));
        var upAnim = RenderTlHalf(up, upInfo, FlexScope.Up, fanCount);
        var downAnim = RenderTlHalf(down, downInfo, FlexScope.Down, fanCount);
        var (timingLook, timingInfo) = downSetLast ? (down, downInfo) : (up, upInfo);
        var step = speedStep ?? FlexAnimationComposer.SpeedMultiplier(timingLook.Key, TlSpeed(timingLook, timingInfo));
        var interval = (int)(step * upAnim.IntervalBaseMs);
        var leds = fanCount * FlexTlEffects.LedsPerFan;
        var (frames, count) = JoinTlHalves(upAnim, downAnim, leds, interval);
        if (upDownSwap)
        {
            SwapTlHalves(frames, count, leds);
        }
        return Slv3WirelessEffectMath.Finalize(new RawAnimation(frames, count, interval), fanCount, FlexTlEffects.LedsPerFan);
    }

    // A hidden slider leaves L-Connect's speed at 0, its slowest level (LWirelessUtils.ValueToRFLampEffectSpeedType),
    // and a hidden direction at clockwise.
    private static int TlSpeed(Slv3TlLook look, Slv3StrimerEffectInfo info) => info.HasSpeed ? ClampSpeed(look.Speed) : 0;

    private static int TlDirection(Slv3TlLook look, Slv3StrimerEffectInfo info) => info.HasDirection ? NormalizeDirection(look.Direction) : 0;

    private static FlexRawAnimation RenderTlHalf(Slv3TlLook look, Slv3StrimerEffectInfo info, FlexScope scope, int fans)
    {
        var req = new FlexEffectRequest(
            scope, fans, TlDirection(look, info), FlexAnimationComposer.BrightnessLevel(look.Brightness),
            TlColors(look.Key, look.Colors), FlexAnimationComposer.SpeedMultiplier(look.Key, TlSpeed(look, info)));
        return FlexTlEffects.IsImplemented(look.Key)
            ? FlexTlEffects.Render(look.Key, req)
            : FlexTlExtendedEffects.Render(look.Key, req, FlexTlExtendedEffects.SeedFor(look.Key));
    }

    /// <summary>
    /// The colours L-Connect hands the effect: the user's, up to the effect's swatch count, then its
    /// defaults, each held to R + G + B of 600 (RgbEffect.initUserColor). No picks keep the defaults,
    /// or Twinkle's own table.
    /// </summary>
    private static IReadOnlyList<RgbColor> TlColors(string key, IReadOnlyList<RgbColor> picked)
    {
        var defaults = TlDefaultColors(key);
        var count = Math.Min(picked.Count, FlexSwatchCounts.Of(FlexFamily.TlFlex, key));
        var colors = new List<RgbColor>(Math.Max(count, defaults.Count));
        for (var i = 0; i < Math.Max(count, defaults.Count); i++)
        {
            var c = i < count ? picked[i] : defaults[i];
            var (r, g, b) = FlexPalette.Cap(c.R, c.G, c.B);
            colors.Add(new RgbColor(r, g, b));
        }
        return colors;
    }

    /// <summary>RgbEffect.CombingTLRgbData with the down animation as its first array and the up one as its second.</summary>
    private static (byte[] Frames, int Count) JoinTlHalves(FlexRawAnimation up, FlexRawAnimation down, int leds, int interval)
    {
        var downLonger = down.FrameCount > up.FrameCount;
        var whole = downLonger ? down : up;
        var part = downLonger ? up : down;
        var halfStart = downLonger ? 0 : TlHalfLeds;
        var total = whole.FrameCount;
        var partFrames = part.FrameCount;
        var partData = part.Frames;
        if (total / 2 >= partFrames)
        {
            var times = total / partFrames;
            var filled = new byte[total * leds * 3];
            for (var i = 0; i < times; i++)
            {
                Buffer.BlockCopy(partData, 0, filled, i * partFrames * leds * 3, partFrames * leds * 3);
            }
            partFrames *= times;
            partData = filled;
        }
        var frames = new byte[total * leds * 3];
        Buffer.BlockCopy(whole.Frames, 0, frames, 0, frames.Length);
        var step = interval * total / partFrames;
        for (var i = 0; i < total; i++)
        {
            var src = Math.Min((int)((double)i * interval / step), partFrames - 1);
            for (var fan = 0; fan < leds; fan += FlexTlEffects.LedsPerFan)
            {
                Buffer.BlockCopy(partData, (src * leds + fan + halfStart) * 3, frames, (i * leds + fan + halfStart) * 3, TlHalfLeds * 3);
            }
        }
        return (frames, total);
    }

    private static void SwapTlHalves(byte[] frames, int count, int leds)
    {
        var half = new byte[TlHalfLeds * 3];
        for (var f = 0; f < count; f++)
        {
            for (var fan = 0; fan < leds; fan += FlexTlEffects.LedsPerFan)
            {
                var o = (f * leds + fan) * 3;
                Buffer.BlockCopy(frames, o, half, 0, half.Length);
                Buffer.BlockCopy(frames, o + half.Length, frames, o, half.Length);
                Buffer.BlockCopy(half, 0, frames, o + half.Length, half.Length);
            }
        }
    }

    // Voice, Door and Lottery below are drawn for the SL-INF fans.

    /// <summary>No live audio input is available to a pre-rendered loop; stands in with an irregular pulse sequence.</summary>
    private static RawAnimation TlVoice(FanEffectContext ctx)
    {
        const int frameCount = 32;
        var rng = new DeterministicRandom(0x707CE1CEUL);
        var level = new double[frameCount];
        var current = 0.3;
        var peak = 0.0;
        for (var i = 0; i < frameCount; i++)
        {
            current = current * 0.5 + rng.NextDouble() * 0.5;
            level[i] = current;
            peak = Math.Max(peak, current);
        }
        for (var i = 0; i < frameCount; i++)
        {
            level[i] /= peak;
        }
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 16.0), (buf, f) =>
        {
            var color = Slv3WirelessEffectMath.Scale(ctx.Colors[0], 0.1 + 0.9 * level[f]);
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                {
                    SetRing(buf, f, ctx, fan, ringPos, color);
                }
            }
        });
    }

    /// <summary>Two halves of the ring split open from the center and close again, like sliding doors.</summary>
    private static RawAnimation TlDoor(FanEffectContext ctx)
    {
        const int frameCount = 40;
        var half = ctx.RingLen / 2.0;
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 12.0), (buf, f) =>
        {
            var opened = Slv3WirelessEffectMath.BounceTravel((double)f / frameCount) * half;
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                {
                    var distFromCenter = Math.Abs(ringPos - (ctx.RingLen - 1) / 2.0);
                    if (distFromCenter <= opened)
                    {
                        SetRing(buf, f, ctx, fan, ringPos, ctx.Colors[0]);
                    }
                }
            }
        });
    }

    /// <summary>Random colours settle, slot-machine style, into one final colour by the end of the loop.</summary>
    private static RawAnimation TlLottery(FanEffectContext ctx)
    {
        const int frameCount = 40;
        var rng = new DeterministicRandom(0x107E5701UL);
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 11.0), (buf, f) =>
        {
            var settleFrac = (double)f / (frameCount - 1);
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                {
                    var settled = rng.NextDouble() < settleFrac * settleFrac;
                    var color = settled ? ctx.Colors[0] : ctx.Colors[rng.NextInt(6)];
                    SetRing(buf, f, ctx, fan, ringPos, color);
                }
            }
        });
    }
}
