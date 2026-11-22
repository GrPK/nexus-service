using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.LianLiFlex;
using Nexus.Service.Peripherals.LianLiWireless;
using Xunit;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Tests.LianLiWireless;

/// <summary>A wireless TL chain plays the Flex TL generators' frames, the same a wired Flex TL unit gets.</summary>
public class Slv3FanEffectsTlFlexTests
{
    private const int LedsPerFan = 26;

    private static readonly Slv3FanFamily[] TlFamilies = { Slv3FanFamily.Tlv2Led, Slv3FanFamily.Tlv2Lcd, Slv3FanFamily.TlFlex };

    private static readonly RgbColor[] Picks =
    {
        new(10, 200, 30), new(250, 20, 120), new(90, 90, 250), new(200, 160, 10), new(1, 2, 3), new(4, 5, 6),
    };

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

    public static IEnumerable<object[]> Keys() => FlexTlEffects.Keys.Select(k => new object[] { k });

    private static FlexComposedAnimation Wired(string key, int fans, int speed, int direction, IReadOnlyList<RgbColor> colors) =>
        FlexAnimationComposer.Compose(
            FlexFamily.TlFlex, fans,
            new Dictionary<string, FlexZoneLook> { ["all"] = new(key, speed, direction, 4, colors) },
            upDownSwap: false);

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
    public void The_swatch_count_is_the_one_the_generator_reads(string key)
    {
        Assert.Equal(FlexSwatchCounts.Of(FlexFamily.TlFlex, key), Slv3FanEffects.Find(Slv3FanFamily.Tlv2Lcd, key)!.ColorsMax);
    }

    [Theory]
    [MemberData(nameof(KeysAndFanCounts))]
    public void A_wireless_chain_gets_the_wired_frames_and_interval(string key, int fans)
    {
        var count = FlexSwatchCounts.Of(FlexFamily.TlFlex, key);
        foreach (var (speed, direction) in new[] { (0, 0), (2, 1), (4, 0) })
        {
            var wired = Wired(key, fans, speed, direction, Picks.Take(count).ToArray());
            var wireless = Slv3FanEffects.Render(Slv3FanFamily.Tlv2Lcd, key, fans, speed, direction, Picks);

            Assert.Equal(fans * LedsPerFan, wired.Leds);
            Assert.Equal(wired.Frames, wireless.FrameCount);
            Assert.Equal(wired.IntervalMs, wireless.IntervalMs, 9);
            Assert.True(wired.Raw.AsSpan().SequenceEqual(wireless.Frames), $"{key} {fans} fans speed {speed} direction {direction}");
        }
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void No_picks_play_the_effect_defaults(string key)
    {
        var defaults = FlexDefaultPalettes.RendersFromTable(FlexFamily.TlFlex, key)
            ? Array.Empty<RgbColor>()
            : FlexDefaultPalettes.For(FlexFamily.TlFlex, key);
        var wired = Wired(key, 3, 2, 0, defaults);
        var wireless = Slv3FanEffects.Render(Slv3FanFamily.Tlv2Led, key, 3, 2, 0, Array.Empty<RgbColor>());
        Assert.True(wired.Raw.AsSpan().SequenceEqual(wireless.Frames), key);
    }

    [Fact]
    public void Fewer_picks_than_swatches_are_filled_from_the_effect_defaults()
    {
        var red = new RgbColor(255, 0, 0);
        var defaults = FlexDefaultPalettes.For(FlexFamily.TlFlex, "breathing");
        var expected = Wired("breathing", 4, 2, 0, new[] { red, defaults[1], defaults[2], defaults[3] });
        var wireless = Slv3FanEffects.Render(Slv3FanFamily.Tlv2Lcd, "breathing", 4, 2, 0, new[] { red });
        Assert.Equal(expected.Raw, wireless.Frames);
    }

    [Fact]
    public void Picks_past_the_swatch_count_are_ignored()
    {
        // Twinkle has no swatches and draws from its own table; Runway reads two colours.
        var none = Slv3FanEffects.Render(Slv3FanFamily.Tlv2Lcd, "twinkle", 4, 2, 0, Array.Empty<RgbColor>());
        var six = Slv3FanEffects.Render(Slv3FanFamily.Tlv2Lcd, "twinkle", 4, 2, 0, Picks);
        Assert.Equal(none.Frames, six.Frames);

        var two = Slv3FanEffects.Render(Slv3FanFamily.Tlv2Lcd, "runway", 4, 2, 0, Picks.Take(2).ToArray());
        var all = Slv3FanEffects.Render(Slv3FanFamily.Tlv2Lcd, "runway", 4, 2, 0, Picks);
        Assert.Equal(two.Frames, all.Frames);
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void Direction_is_offered_exactly_where_it_changes_the_frames(string key)
    {
        var changes = false;
        for (var fans = 1; fans <= Slv3FanEffects.MaxFans && !changes; fans++)
        {
            var forward = Slv3FanEffects.Render(Slv3FanFamily.Tlv2Lcd, key, fans, 2, 0, Picks);
            var backward = Slv3FanEffects.Render(Slv3FanFamily.Tlv2Lcd, key, fans, 2, 1, Picks);
            changes = !forward.Frames.AsSpan().SequenceEqual(backward.Frames);
        }
        Assert.Equal(changes, Slv3FanEffects.Find(Slv3FanFamily.Tlv2Lcd, key)!.HasDirection);
    }

    [Fact]
    public void Static_gives_each_fan_its_own_colour_at_full_level()
    {
        var picks = new[] { new RgbColor(255, 0, 0), new RgbColor(0, 255, 0), new RgbColor(0, 0, 255), new RgbColor(255, 255, 0) };
        var anim = Slv3FanEffects.Render(Slv3FanFamily.Tlv2Lcd, "static", 4, 0, 0, picks);
        for (var fan = 0; fan < 4; fan++)
        {
            for (var led = 0; led < LedsPerFan; led++)
            {
                var o = (fan * LedsPerFan + led) * 3;
                // 255 * 255 >> 8, as L-Connect scales at its top brightness level.
                Assert.Equal(picks[fan].R == 255 ? 254 : 0, anim.Frames[o]);
                Assert.Equal(picks[fan].G == 255 ? 254 : 0, anim.Frames[o + 1]);
                Assert.Equal(picks[fan].B == 255 ? 254 : 0, anim.Frames[o + 2]);
            }
        }
    }

    [Fact]
    public void Rainbow_steps_one_LED_a_frame_on_both_halves_of_a_fan()
    {
        var anim = Slv3FanEffects.Render(Slv3FanFamily.Tlv2Lcd, "rainbow", 1, 2, 0, Array.Empty<RgbColor>());
        Assert.Equal(13, anim.FrameCount);
        Assert.Equal(11.0 * 5, anim.IntervalMs, 9);
        for (var f = 0; f < anim.FrameCount; f++)
        {
            var frame = anim.Frames.AsSpan(f * LedsPerFan * 3, LedsPerFan * 3);
            Assert.True(frame[..(13 * 3)].SequenceEqual(frame[(13 * 3)..]), $"frame {f}: the halves differ");
        }
        var first = anim.Frames.AsSpan(0, 13 * 3).ToArray();
        var second = anim.Frames.AsSpan(LedsPerFan * 3, 13 * 3).ToArray();
        Assert.Equal(first[..(12 * 3)], second[3..(13 * 3)]);
    }
}
