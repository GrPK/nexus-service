using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.LianLiFlex;
using DeterministicRandom = Nexus.Service.Peripherals.LianLiWireless.Slv3WirelessEffectMath.DeterministicRandom;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Peripherals.LianLiWireless;

public static partial class Slv3FanEffects
{
    // L-Connect's 29 TL effects. Direction is offered where the Flex TL generators use it, and the
    // swatch counts are the ones they read.
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
        Tl("voice", speed: false, direction: false),
        Tl("door", speed: true, direction: false),
        Tl("render", speed: true, direction: true),
        Tl("ripple", speed: true, direction: false),
        Tl("reflect", speed: true, direction: true),
        Tl("tailChasing", speed: true, direction: true),
        Tl("paint", speed: true, direction: true),
        Tl("pingPong", speed: true, direction: true),
        Tl("stack", speed: true, direction: true),
        Tl("coverCycle", speed: true, direction: true),
        Tl("wave", speed: true, direction: true),
        Tl("racing", speed: true, direction: true),
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

    private static IReadOnlyDictionary<string, Func<FanEffectContext, RawAnimation>> BuildTlRenderers()
    {
        var map = new Dictionary<string, Func<FanEffectContext, RawAnimation>>(StringComparer.Ordinal);
        foreach (var info in BuildTlCatalog())
        {
            var key = info.Key;
            map[key] = ctx => RenderFlexTl(key, ctx);
        }
        return map;
    }

    // The top level of FlexAnimationComposer.BrightnessLevel (255).
    private const int FlexFullBrightness = 4;

    /// <summary>
    /// A wireless TL chain lays its LEDs out like a wired Flex TL unit: 26 per fan, fan after fan,
    /// LED 0..12 the up half and 13..25 the down half. So its effects come from the Flex TL
    /// generators, through the composer the wired units use. They are drawn at full brightness,
    /// because the chain's brightness is applied when the frames are sent.
    /// </summary>
    private static RawAnimation RenderFlexTl(string key, FanEffectContext ctx)
    {
        var look = new FlexZoneLook(key, ctx.Speed, ctx.Direction, FlexFullBrightness, FlexTlColors(key, ctx.Picked));
        var zones = new Dictionary<string, FlexZoneLook>(StringComparer.Ordinal) { ["all"] = look };
        var anim = FlexAnimationComposer.Compose(FlexFamily.TlFlex, ctx.FanCount, zones, upDownSwap: false);
        return new RawAnimation(anim.Raw, anim.Frames, anim.IntervalMs);
    }

    /// <summary>
    /// The colours a wired Flex fan would play with the same picks: the user's, up to the effect's
    /// swatch count, then the effect's defaults. No picks keep the defaults, or Twinkle's own table.
    /// </summary>
    private static IReadOnlyList<RgbColor> FlexTlColors(string key, IReadOnlyList<RgbColor> picked)
    {
        var defaults = FlexDefaultPalettes.For(FlexFamily.TlFlex, key);
        var count = Math.Min(picked.Count, FlexSwatchCounts.Of(FlexFamily.TlFlex, key));
        if (count == 0)
        {
            return FlexDefaultPalettes.RendersFromTable(FlexFamily.TlFlex, key) ? Array.Empty<RgbColor>() : defaults;
        }
        var colors = new List<RgbColor>(Math.Max(count, defaults.Count));
        for (var i = 0; i < count; i++)
        {
            colors.Add(picked[i]);
        }
        for (var i = count; i < defaults.Count; i++)
        {
            colors.Add(defaults[i]);
        }
        return colors;
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
