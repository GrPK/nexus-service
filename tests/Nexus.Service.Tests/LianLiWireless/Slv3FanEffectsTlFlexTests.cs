using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.LianLiFlex;
using Nexus.Service.Peripherals.LianLiWireless;
using Xunit;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Tests.LianLiWireless;

/// <summary>A wireless TL chain plays what L-Connect 2.1.29 sends it, drawn by the Flex TL generators.</summary>
public class Slv3FanEffectsTlFlexTests
{
    private const int LedsPerFan = 26;
    private const int Half = 13;
    private const Slv3FanFamily Tl = Slv3FanFamily.Tlv2Lcd;

    private static readonly Slv3FanFamily[] TlFamilies = { Slv3FanFamily.Tlv2Led, Slv3FanFamily.Tlv2Lcd, Slv3FanFamily.TlFlex };

    private static readonly RgbColor[] Picks =
    {
        new(10, 200, 30), new(250, 20, 120), new(90, 90, 250), new(200, 160, 10), new(1, 2, 3), new(4, 5, 6),
    };

    public static IEnumerable<object[]> Keys() => FlexTlEffects.Keys.Select(k => new object[] { k });

    public static IEnumerable<object[]> KeysAndFanCounts()
    {
        foreach (var key in FlexTlEffects.Keys)
        {
            for (var fans = 1; fans <= Slv3FanEffects.MaxFans; fans++)
            {
                yield return new object[] { key, fans };
            }
        }
    }

    private static Slv3TlLook Look(string key, int speed = 2, int direction = 0, int brightness = 4, IReadOnlyList<RgbColor>? colors = null) =>
        new(key, speed, direction, brightness, colors ?? Array.Empty<RgbColor>());

    private static Slv3StrimerAnimation Whole(Slv3TlLook look, int fans = 1, bool swap = false) =>
        Slv3FanEffects.RenderTl(Tl, fans, look, look, downSetLast: false, swap);

    private static byte[] HalfOf(byte[] frames, int frame, int fans, int fan, int halfStart) =>
        frames.AsSpan((frame * fans * LedsPerFan + fan * LedsPerFan + halfStart) * 3, Half * 3).ToArray();

    [Fact]
    public void The_TL_catalog_lists_the_Flex_TL_keys_in_order()
    {
        foreach (var family in TlFamilies)
        {
            Assert.Equal(FlexTlEffects.Keys, Slv3FanEffects.CatalogFor(family).Select(i => i.Key));
        }
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void Every_TL_key_has_a_Flex_generator(string key)
    {
        Assert.True(FlexTlEffects.IsImplemented(key) || FlexTlExtendedEffects.IsImplemented(key));
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void Speed_direction_and_swatches_are_the_ones_L_Connect_offers(string key)
    {
        // TLV2SubProfile.DefaultLightingSettings: a speed slider everywhere but Static, a direction on nine effects.
        string[] withDirection = { "rainbow", "meteor", "colorCycle", "render", "tailChasing", "stack", "coverCycle", "lottery", "meteorShower" };
        var info = Slv3FanEffects.Find(Tl, key)!;
        Assert.Equal(key != "static", info.HasSpeed);
        Assert.Equal(withDirection.Contains(key), info.HasDirection);
        Assert.Equal(FlexSwatchCounts.Of(FlexFamily.TlFlex, key), info.ColorsMax);
    }

    [Fact]
    public void Defaults_are_L_Connect_TL_v2_defaults()
    {
        static string Hex(IReadOnlyList<RgbColor> colors) => string.Join(",", colors.Select(c => $"{c.R:X2}{c.G:X2}{c.B:X2}"));
        Assert.Equal("AA00FF,00D7FF,00FF00,FFD800", Hex(Slv3FanEffects.TlDefaultColors("static")));
        Assert.Equal("FFFFFF,FF0096", Hex(Slv3FanEffects.TlDefaultColors("runway")));
        Assert.Equal("FFD700,00FF00,00D7FF", Hex(Slv3FanEffects.TlDefaultColors("colorCycle")));
        Assert.Equal("FF0096,0000FF,00FF00,FFF026", Hex(Slv3FanEffects.TlDefaultColors("render")));
        Assert.Equal("FF0000,0000FF,008000", Hex(Slv3FanEffects.TlDefaultColors("coverCycle")));
        Assert.Equal("FF0000,0000FF,008000,FFFF00", Hex(Slv3FanEffects.TlDefaultColors("door")));
        Assert.Equal("FF0000,0000FF,00FF00,FFFF00", Hex(Slv3FanEffects.TlDefaultColors("stack")));
        Assert.Equal("", Hex(Slv3FanEffects.TlDefaultColors("twinkle")));
        Assert.Equal("", Hex(Slv3FanEffects.TlDefaultColors("voice")));
    }

    [Theory]
    [MemberData(nameof(KeysAndFanCounts))]
    public void A_whole_fan_look_gets_the_wired_frames(string key, int fans)
    {
        var info = Slv3FanEffects.Find(Tl, key)!;
        var count = FlexSwatchCounts.Of(FlexFamily.TlFlex, key);
        foreach (var speed in new[] { 0, 4 })
        {
            var direction = info.HasDirection ? 1 : 0;
            var wired = FlexAnimationComposer.Compose(
                FlexFamily.TlFlex, fans,
                new Dictionary<string, FlexZoneLook> { ["all"] = new(key, info.HasSpeed ? speed : 0, direction, 4, Picks.Take(count).ToArray()) },
                upDownSwap: false);
            var wireless = Whole(Look(key, speed, direction, 4, Picks), fans);
            Assert.Equal(wired.Frames, wireless.FrameCount);
            Assert.Equal(wired.IntervalMs, wireless.IntervalMs, 9);
            Assert.True(wired.Raw.AsSpan().SequenceEqual(wireless.Frames), $"{key} {fans} fans speed {speed}");
        }
    }

    [Fact]
    public void ColorCycle_matches_L_Connect_frame_for_frame()
    {
        // L-Connect 2.1.29: TLMode7 drawn for the up half (tb 0) and the down half (tb 1), joined by
        // CombingTLRgbData; 1 fan, default colours, speed L3, brightness L5, clockwise.
        byte[] frame0 =
        {
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 214, 254, 0, 0, 0, 0, 0, 0, 0, 254, 0, 0, 0, 0, 0, 0, 0, 254, 214, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 214, 254, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 254, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 254, 214, 0,
        };
        byte[] frame5 =
        {
            0, 0, 0, 0, 254, 0, 0, 0, 0, 0, 0, 0, 254, 214, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 214, 254, 0, 0, 0,
            0, 254, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 254, 214, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 214, 254, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        };
        var anim = Whole(Look("colorCycle"));
        Assert.Equal(13, anim.FrameCount);
        Assert.Equal(55.0, anim.IntervalMs, 9);
        Assert.Equal(frame0, anim.Frames.AsSpan(0, 78).ToArray());
        Assert.Equal(frame5, anim.Frames.AsSpan(5 * 78, 78).ToArray());
    }

    [Fact]
    public void Door_matches_the_L_Connect_Witmod_library()
    {
        // WitmodLightings GetTLEffect(12, scope 1 and 0, 1 fan, clockwise) with the default colours at L5, joined.
        byte[] frame5 =
        {
            254, 0, 0, 254, 0, 0, 254, 0, 0, 254, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 254, 0, 0, 254, 0, 0, 254, 0, 0, 254, 0, 0,
            254, 0, 0, 254, 0, 0, 254, 0, 0, 254, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 254, 0, 0, 254, 0, 0, 254, 0, 0, 254, 0, 0,
        };
        var anim = Whole(Look("door"));
        Assert.Equal(100, anim.FrameCount);
        Assert.Equal(new byte[78], anim.Frames.AsSpan(0, 78).ToArray());
        Assert.Equal(frame5, anim.Frames.AsSpan(5 * 78, 78).ToArray());
    }

    [Fact]
    public void Each_half_plays_its_own_look()
    {
        // Rainbow and Color Cycle are both 13 frames on one fan, so neither is stretched.
        var up = Look("rainbow");
        var down = Look("colorCycle", colors: Picks);
        var both = Slv3FanEffects.RenderTl(Tl, 1, up, down, downSetLast: false, upDownSwap: false);
        var rainbow = Whole(up);
        var cycle = Whole(down);
        Assert.Equal(13, both.FrameCount);
        for (var f = 0; f < both.FrameCount; f++)
        {
            Assert.Equal(HalfOf(rainbow.Frames, f, 1, 0, 0), HalfOf(both.Frames, f, 1, 0, 0));
            Assert.Equal(HalfOf(cycle.Frames, f, 1, 0, Half), HalfOf(both.Frames, f, 1, 0, Half));
        }
    }

    [Fact]
    public void The_longer_half_plays_whole_at_the_speed_set_last_times_the_up_step()
    {
        var collide = Look("collide", speed: 0);
        var breathing = Look("breathing", speed: 4);
        var upLast = Slv3FanEffects.RenderTl(Tl, 2, collide, breathing, downSetLast: false, upDownSwap: false);
        var downLast = Slv3FanEffects.RenderTl(Tl, 2, collide, breathing, downSetLast: true, upDownSwap: false);
        Assert.Equal(Math.Max(Whole(collide, 2).FrameCount, Whole(breathing, 2).FrameCount), upLast.FrameCount);
        // Collide's step is 16 ms; speed 0 is L1 (7), speed 4 is L5 (3).
        Assert.Equal(7 * 16.0, upLast.IntervalMs, 9);
        Assert.Equal(3 * 16.0, downLast.IntervalMs, 9);
    }

    [Fact]
    public void The_swap_exchanges_the_halves_of_every_fan()
    {
        var up = Look("meteor", direction: 1);
        var down = Look("runway", colors: Picks);
        var plain = Slv3FanEffects.RenderTl(Tl, 3, up, down, downSetLast: false, upDownSwap: false);
        var swapped = Slv3FanEffects.RenderTl(Tl, 3, up, down, downSetLast: false, upDownSwap: true);
        Assert.Equal(plain.FrameCount, swapped.FrameCount);
        for (var f = 0; f < plain.FrameCount; f++)
        {
            for (var fan = 0; fan < 3; fan++)
            {
                Assert.Equal(HalfOf(plain.Frames, f, 3, fan, 0), HalfOf(swapped.Frames, f, 3, fan, Half));
                Assert.Equal(HalfOf(plain.Frames, f, 3, fan, Half), HalfOf(swapped.Frames, f, 3, fan, 0));
            }
        }
    }

    [Fact]
    public void A_hidden_direction_plays_clockwise_and_a_hidden_speed_the_slowest_step()
    {
        // Reflect draws differently counter-clockwise, but L-Connect offers it no direction.
        Assert.Equal(Whole(Look("reflect", direction: 0)).Frames, Whole(Look("reflect", direction: 1)).Frames);
        Assert.Equal(77.0, Whole(Look("static", speed: 4)).IntervalMs, 9);
    }

    [Fact]
    public void Brightness_is_drawn_into_the_frames()
    {
        var red = new[] { new RgbColor(255, 0, 0) };
        var full = Whole(Look("static", brightness: 4, colors: red));
        var half = Whole(Look("static", brightness: 2, colors: red));
        Assert.Equal(254, full.Frames[0]);
        Assert.Equal(127, half.Frames[0]);
    }

    [Fact]
    public void Colours_past_600_are_stepped_down_as_L_Connect_does()
    {
        // White steps down 5% at a time to 195, 195, 195; Door draws with the Witmod generator, which does not cap by itself.
        var white = Whole(Look("door", colors: new[] { new RgbColor(255, 255, 255) }));
        var capped = Whole(Look("door", colors: new[] { new RgbColor(195, 195, 195) }));
        Assert.Equal(capped.Frames, white.Frames);
    }

    [Fact]
    public void Picks_past_the_swatch_count_are_ignored_and_missing_ones_take_the_defaults()
    {
        Assert.Equal(Whole(Look("twinkle")).Frames, Whole(Look("twinkle", colors: Picks)).Frames);
        Assert.Equal(Whole(Look("runway", colors: Picks.Take(2).ToArray())).Frames, Whole(Look("runway", colors: Picks)).Frames);
        var red = new RgbColor(255, 0, 0);
        var defaults = Slv3FanEffects.TlDefaultColors("breathing");
        Assert.Equal(
            Whole(Look("breathing", colors: new[] { red, defaults[1], defaults[2], defaults[3] }), 4).Frames,
            Whole(Look("breathing", colors: new[] { red }), 4).Frames);
    }

    [Fact]
    public void Render_plays_the_look_on_the_whole_fan_at_full_brightness()
    {
        var render = Slv3FanEffects.Render(Tl, "meteor", 2, 3, 1, Picks);
        Assert.Equal(Whole(Look("meteor", 3, 1, 4, Picks), 2).Frames, render.Frames);
    }
}
