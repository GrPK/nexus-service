using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.BulkPanels;

public class HydroShift2CurveEdgeEffectsTests
{
    private static readonly (byte, byte, byte)[] SixColors =
    {
        (255, 1, 127), (5, 200, 17), (90, 33, 240), (18, 77, 150), (200, 180, 0), (0, 128, 255),
    };

    // FNV-1a over every frame at L-Connect's five brightness stops (0, 63, 127, 191, 255) x both
    // directions, from L-Connect 2.1.29's HydroShift II OLED Curved renderer (RgbEffect.HS2Mode1..19)
    // run on 35 LEDs with these six colours.
    [Theory]
    [InlineData(HydroShift2CurveEdgeEffects.Rainbow, 0x506fcb49dc769ea5UL)]
    [InlineData(HydroShift2CurveEdgeEffects.Wave, 0x7fe3b7aeda86bdb5UL)]
    [InlineData(HydroShift2CurveEdgeEffects.Static, 0xcc8199c904fdf8dbUL)]
    [InlineData(HydroShift2CurveEdgeEffects.Breathing, 0x56cdf96a8a15e33dUL)]
    [InlineData(HydroShift2CurveEdgeEffects.RainbowMorph, 0x8b35eed0af64495fUL)]
    [InlineData(HydroShift2CurveEdgeEffects.Paint, 0x73b485a5357d9501UL)]
    [InlineData(HydroShift2CurveEdgeEffects.Runway, 0x3a3e9876aa7da255UL)]
    [InlineData(HydroShift2CurveEdgeEffects.Tide, 0xb3922c7d19e1fa81UL)]
    [InlineData(HydroShift2CurveEdgeEffects.BlowUp, 0x703dcfe8902d9811UL)]
    [InlineData(HydroShift2CurveEdgeEffects.Meteor, 0x395d62d124d260a4UL)]
    [InlineData(HydroShift2CurveEdgeEffects.Snooker, 0x9d62d14accb3d7c1UL)]
    [InlineData(HydroShift2CurveEdgeEffects.Mixing, 0xbbe273bb946db8e5UL)]
    [InlineData(HydroShift2CurveEdgeEffects.PingPong, 0x5aa9c3ac58e80de5UL)]
    [InlineData(HydroShift2CurveEdgeEffects.BulletStack, 0x6908b5de81451e99UL)]
    [InlineData(HydroShift2CurveEdgeEffects.Twinkle, 0x8726e902aad05409UL)]
    [InlineData(HydroShift2CurveEdgeEffects.River, 0xc84bc3a47c380005UL)]
    [InlineData(HydroShift2CurveEdgeEffects.Hourglass, 0x8a749cbe0d0cbd11UL)]
    [InlineData(HydroShift2CurveEdgeEffects.ElectricCurrent, 0x99593e0d4a063fe5UL)]
    [InlineData(HydroShift2CurveEdgeEffects.RainbowWave, 0xcaa72139d91968dbUL)]
    public void Frames_match_the_vendor_effect(string mode, ulong expected)
    {
        ulong hash = 0xcbf29ce484222325;
        foreach (var brightness in new[] { 0, 63, 127, 191, 255 })
        {
            foreach (var reverse in new[] { false, true })
            {
                var frames = HydroShift2CurveEdgeEffects.Render(mode, SixColors, brightness, reverse);
                hash = Fnv(hash, BitConverter.GetBytes(frames.Count));
                foreach (var frame in frames)
                {
                    Assert.Equal(HydroShift2CurveProtocol.LedCount * 3, frame.Length);
                    hash = Fnv(hash, frame);
                }
            }
        }
        Assert.Equal(expected, hash);
    }

    [Theory]
    [InlineData(64, 0x024592f677db75d2UL)]
    [InlineData(128, 0x1da3808617e7c328UL)]
    public void The_two_middle_brightness_steps_are_dimmed_a_quarter_like_the_vendor(int brightness, ulong expected)
    {
        var frames = HydroShift2CurveEdgeEffects.Render(HydroShift2CurveEdgeEffects.Rainbow, [], brightness, false);
        ulong hash = 0xcbf29ce484222325;
        foreach (var frame in frames)
        {
            hash = Fnv(hash, frame);
        }
        Assert.Equal(expected, hash);
    }

    [Theory]
    [InlineData(0, 7, 48)]
    [InlineData(1, 6, 41)]
    [InlineData(2, 5, 34)]
    [InlineData(3, 4, 27)]
    [InlineData(4, 3, 20)]
    public void Speed_levels_follow_the_vendor_slider(int level, int step, int periodMs)
    {
        var s = HydroShift2CurveEdgeEffects.SpeedStep(HydroShift2CurveEdgeEffects.SliderPercent(level));
        Assert.Equal(step, s);
        Assert.Equal(periodMs, HydroShift2CurveEdgeEffects.FramePeriodMs(s));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 63)]
    [InlineData(2, 127)]
    [InlineData(3, 191)]
    [InlineData(4, 255)]
    public void Brightness_levels_follow_the_vendor_slider(int level, int expected)
    {
        Assert.Equal(expected, HydroShift2CurveEdgeEffects.BrightnessByte(HydroShift2CurveEdgeEffects.SliderPercent(level)));
    }

    [Fact]
    public void An_empty_palette_plays_the_vendor_colours()
    {
        var unset = HydroShift2CurveEdgeEffects.Render(HydroShift2CurveEdgeEffects.River, [], 255, false);
        var vendor = HydroShift2CurveEdgeEffects.Render(HydroShift2CurveEdgeEffects.River, [(0, 0, 255), (0, 215, 255)], 255, false);
        Assert.Equal(vendor.SelectMany(f => f), unset.SelectMany(f => f));
        Assert.Equal(new[] { "#0000ff", "#00d7ff" }, HydroShift2CurveEdgeEffects.DefaultColors(HydroShift2CurveEdgeEffects.River));
    }

    [Fact]
    public void The_mode_list_and_controls_match_the_vendor_app()
    {
        Assert.Equal(19, HydroShift2CurveEdgeEffects.Modes.Count);
        Assert.Equal(6, HydroShift2CurveEdgeEffects.ColorCount(HydroShift2CurveEdgeEffects.Wave));
        Assert.Equal(1, HydroShift2CurveEdgeEffects.ColorCount(HydroShift2CurveEdgeEffects.Static));
        Assert.Equal(2, HydroShift2CurveEdgeEffects.ColorCount(HydroShift2CurveEdgeEffects.Mixing));
        Assert.Equal(4, HydroShift2CurveEdgeEffects.ColorCount(HydroShift2CurveEdgeEffects.Hourglass));
        Assert.Equal(0, HydroShift2CurveEdgeEffects.ColorCount(HydroShift2CurveEdgeEffects.Twinkle));
        var withDirection = HydroShift2CurveEdgeEffects.Modes.Where(HydroShift2CurveEdgeEffects.HasDirection).ToArray();
        Assert.Equal(
            new[]
            {
                HydroShift2CurveEdgeEffects.Rainbow, HydroShift2CurveEdgeEffects.Wave, HydroShift2CurveEdgeEffects.Meteor,
                HydroShift2CurveEdgeEffects.BulletStack, HydroShift2CurveEdgeEffects.River, HydroShift2CurveEdgeEffects.RainbowWave,
            },
            withDirection);
        Assert.False(HydroShift2CurveEdgeEffects.HasSpeed(HydroShift2CurveEdgeEffects.Static));
    }

    [Fact]
    public void Direction_is_ignored_where_the_vendor_app_hides_it()
    {
        // Tide's renderer reads a direction but L-Connect never offers it, so it always plays clockwise.
        var settings = new HydroShift2CurveLightingSettings { Mode = HydroShift2CurveEdgeEffects.Tide, Direction = 1 };
        var reverse = HydroShift2CurveEdgeEffects.HasDirection(settings.Mode) && settings.Direction == 1;
        Assert.False(reverse);
    }

    [Fact]
    public void Writer_render_applies_the_palette_and_colour_order()
    {
        var settings = new HydroShift2CurveLightingSettings
        {
            Mode = HydroShift2CurveEdgeEffects.Static,
            Colors = new List<string> { "#ff8000" },
        };
        var rgb = HydroShift2CurveLightingFrameWriter.Render(settings, 255, false, null);
        Assert.Single(rgb);
        Assert.Equal(new byte[] { 254, 127, 0 }, rgb[0][..3]);
        var grb = HydroShift2CurveLightingFrameWriter.Render(settings, 255, false, "GRB");
        Assert.Equal(new byte[] { 127, 254, 0 }, grb[0][..3]);
    }

    private static ulong Fnv(ulong hash, IEnumerable<byte> bytes)
    {
        foreach (var b in bytes)
        {
            hash = (hash ^ b) * 0x100000001b3;
        }
        return hash;
    }
}
