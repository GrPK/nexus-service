using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.LianLiFlex;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>
/// The Universal Screen 8.8 bezel effects as L-Connect renders them: the same 19 modes, palette
/// defaults, controls, slider mapping and frame timing. The 0416:8050 LED board has no effects of its
/// own, so each frame is <see cref="LedCount"/> packed RGB triples streamed by the PC.
/// </summary>
public static class UniversalScreen88LedEffects
{
    public const int LedCount = 60;

    public const string Rainbow = "rainbow";
    public const string Wave = "wave";
    public const string Static = "static";
    public const string Breathing = "breathing";
    public const string RainbowMorph = "rainbowMorph";
    public const string Paint = "paint";
    public const string Runway = "runway";
    public const string Tide = "tide";
    public const string BlowUp = "blowUp";
    public const string Meteor = "meteor";
    public const string Snooker = "snooker";
    public const string Mixing = "mixing";
    public const string PingPong = "pingPong";
    public const string BulletStack = "bulletStack";
    public const string Twinkle = "twinkle";
    public const string River = "river";
    public const string Hourglass = "hourglass";
    public const string ElectricCurrent = "electricCurrent";
    public const string RainbowWave = "rainbowWave";

    /// <summary>L-Connect's mode list, in its order.</summary>
    public static readonly IReadOnlyList<string> Modes = new[]
    {
        Rainbow, Wave, Static, Breathing, RainbowMorph, Paint, Runway, Tide, BlowUp, Meteor,
        Snooker, Mixing, PingPong, BulletStack, Twinkle, River, Hourglass, ElectricCurrent, RainbowWave,
    };

    /// <summary>Top of the speed and brightness scales: five stops, L-Connect's slider at 0, 25, 50, 75 and 100.</summary>
    public const int MaxLevel = 4;

    /// <summary>L-Connect's default slider position: speed 75.</summary>
    public const int DefaultSpeedLevel = 3;

    /// <summary>L-Connect's default slider position: brightness 100.</summary>
    public const int DefaultBrightnessLevel = 4;

    private const int FrameBytes = LedCount * 3;

    private static readonly Rgb Red = new(255, 0, 0);
    private static readonly Rgb Green = new(0, 255, 0);
    private static readonly Rgb Blue = new(0, 0, 255);
    private static readonly Rgb Yellow = new(255, 255, 0);
    private static readonly Rgb Cyan = new(0, 255, 255);
    private static readonly Rgb Magenta = new(255, 0, 255);
    private static readonly Rgb White = new(255, 255, 255);
    private static readonly Rgb[] SixDefaults = { Red, Green, Blue, Yellow, Cyan, Magenta };

    private static readonly Dictionary<string, Rgb[]> DefaultPalettes = new(StringComparer.Ordinal)
    {
        [Wave] = SixDefaults,
        [Static] = new[] { Red },
        [Breathing] = new[] { Red },
        [Paint] = SixDefaults,
        [Runway] = new[] { Red, Green },
        [Tide] = SixDefaults,
        [BlowUp] = SixDefaults,
        [Meteor] = SixDefaults,
        [Snooker] = SixDefaults,
        [Mixing] = new[] { Red, Green },
        [PingPong] = SixDefaults,
        [River] = new[] { new Rgb(0, 0, 255), new Rgb(0, 215, 255) },
        [Hourglass] = new[] { new Rgb(255, 0, 150), new Rgb(0, 0, 255), new Rgb(255, 240, 38), new Rgb(0, 255, 0) },
        [ElectricCurrent] = new[] { new Rgb(255, 0, 150), new Rgb(0, 0, 255), new Rgb(0, 255, 0), new Rgb(255, 240, 38) },
    };

    /// <summary>The seven-colour palette Bullet Stack and Twinkle use instead of the user's.</summary>
    private static readonly Rgb[] Party =
    {
        new(153, 0, 204), new(255, 51, 204), new(255, 153, 0), new(255, 255, 0),
        new(0, 255, 102), new(51, 255, 255), new(66, 87, 248),
    };

    /// <summary>One turn of the colour wheel in 30 steps, per channel (Rainbow).</summary>
    private static readonly byte[][] Wheel30 =
    {
        new byte[] { 255, 229, 203, 177, 151, 125, 99, 73, 47, 21, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 21, 47, 73, 99, 125, 151, 177, 203, 229 },
        new byte[] { 0, 21, 47, 73, 99, 125, 151, 177, 203, 229, 255, 229, 203, 177, 151, 125, 99, 73, 47, 21, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
        new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 21, 47, 73, 99, 125, 151, 177, 203, 229, 255, 229, 203, 177, 151, 125, 99, 73, 47, 21 },
    };

    /// <summary>One turn of the colour wheel in 60 steps, per channel (Rainbow Wave).</summary>
    private static readonly byte[][] Wheel60 = BuildWheel60();

    private static readonly byte[] WaveProfile = { 0, 0, 32, 64, 128, 255, 128, 64, 32, 0, 0, 0 };
    private static readonly byte[] MeteorTail = { 16, 32, 64, 128, 255 };
    private static readonly byte[] RiverColorBySlot = { 0, 0, 0, 0, 1, 1 };

    /// <summary>Rainbow Wave's lit bands round the bezel.</summary>
    private static readonly byte[] RainbowWaveMask =
    {
        0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 1,
        1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1,
    };

    /// <summary>Colour pickers the mode shows; 0 means the mode draws its own colours.</summary>
    public static int ColorCount(string mode) => DefaultPalettes.TryGetValue(mode, out var p) ? p.Length : 0;

    /// <summary>Modes whose direction L-Connect lets the user flip.</summary>
    public static bool HasDirection(string mode) =>
        mode is Rainbow or Wave or Meteor or BulletStack or River or RainbowWave;

    /// <summary>L-Connect shows the speed slider on every mode of this screen, Static included.</summary>
    public static bool HasSpeed(string mode) => KnownModes.Contains(mode);

    private static readonly HashSet<string> KnownModes = new(Modes, StringComparer.Ordinal);

    /// <summary>L-Connect's colours for <paramref name="mode"/> as "#rrggbb", in picker order.</summary>
    public static IReadOnlyList<string> DefaultColors(string mode)
    {
        if (!DefaultPalettes.TryGetValue(mode, out var palette))
        {
            return Array.Empty<string>();
        }
        var hex = new string[palette.Length];
        for (int i = 0; i < palette.Length; i++)
        {
            hex[i] = $"#{palette[i].R:x2}{palette[i].G:x2}{palette[i].B:x2}";
        }
        return hex;
    }

    /// <summary>The L-Connect slider position (0..100) a level stands for.</summary>
    public static double SliderPercent(int level) => Math.Clamp(level, 0, MaxLevel) * 25.0;

    /// <summary>Slider 0..100 to the speed step 7 (slowest) .. 3, as L-Connect maps it (truncating).</summary>
    public static int SpeedStep(double percent) => Math.Clamp((int)(7.0 + ((3.0 - 7.0) * (Math.Clamp(percent, 0, 100) / 100.0))), 3, 7);

    /// <summary>Slider 0..100 to the 0..255 brightness L-Connect hands the renderer (truncating).</summary>
    public static int BrightnessByte(double percent) => Math.Clamp((int)(Math.Clamp(percent, 0, 100) / 100.0 * 255.0), 0, 255);

    /// <summary>Ms per speed step: 16.5 for Rainbow and Rainbow Wave, 14.3 for Wave and River, 11 for the rest.</summary>
    public static double IntervalBaseMs(string mode) => mode switch
    {
        Rainbow or RainbowWave => 16.5,
        Wave or River => 14.3,
        _ => 11.0,
    };

    /// <summary>
    /// Ms between frames: L-Connect truncates step x base to whole ms, then sleeps 0.625 of it
    /// (truncated) after each frame's three USB writes.
    /// </summary>
    public static int FramePeriodMs(string mode, int speedStep) =>
        (int)((int)(Math.Clamp(speedStep, 3, 7) * IntervalBaseMs(mode)) * 0.625);

    /// <summary>
    /// Renders <paramref name="mode"/>'s whole loop. <paramref name="colors"/> are the user's (up to six);
    /// an empty list falls back to the mode's defaults. <paramref name="brightness"/> is 0..255 and
    /// <paramref name="reverse"/> is L-Connect's counter-clockwise direction. Twinkle picks a random
    /// colour per LED on every render, as L-Connect does.
    /// </summary>
    public static List<byte[]> Render(string mode, IReadOnlyList<(byte R, byte G, byte B)> colors, int brightness, bool reverse) =>
        Render(mode, colors, brightness, reverse, new Random());

    internal static List<byte[]> Render(string mode, IReadOnlyList<(byte R, byte G, byte B)> colors, int brightness, bool reverse, Random twinkleRandom)
    {
        var palette = Palette(mode, colors);
        var bright = Math.Clamp(brightness, 0, 255);
        var dir = reverse ? 1 : 0;
        return mode switch
        {
            Rainbow => RenderRainbow(bright, dir),
            Wave => RenderWave(palette, bright, dir),
            Static => new List<byte[]> { Solid(Dim(palette[0], bright)) },
            Breathing => RenderBreathing(palette, bright),
            RainbowMorph => RenderMorph(bright),
            Paint => RenderPaint(palette, bright),
            Runway => RenderRunway(palette, bright),
            Tide => RenderTide(palette, bright),
            BlowUp => RenderBlowUp(palette, bright),
            Meteor => RenderMeteor(palette, bright, dir),
            Snooker => RenderSnooker(palette, bright),
            Mixing => RenderMixing(palette, bright),
            PingPong => RenderPingPong(palette, bright),
            BulletStack => RenderBulletStack(bright, dir),
            Twinkle => RenderTwinkle(bright, twinkleRandom),
            River => RenderRiver(palette, bright, dir),
            Hourglass => RenderHourglass(palette, bright),
            ElectricCurrent => RenderElectricCurrent(palette, bright),
            RainbowWave => RenderRainbowWave(bright, dir),
            _ => throw new ArgumentException($"unknown Universal Screen 8.8 effect '{mode}'", nameof(mode)),
        };
    }

    /// <summary>Six palette slots: the user's colours, else the mode's defaults; unused slots are black.</summary>
    private static Rgb[] Palette(string mode, IReadOnlyList<(byte R, byte G, byte B)> colors)
    {
        var slots = new Rgb[6];
        if (colors.Count > 0)
        {
            for (int i = 0; i < slots.Length && i < colors.Count; i++)
            {
                slots[i] = new Rgb(colors[i].R, colors[i].G, colors[i].B);
            }
        }
        else if (DefaultPalettes.TryGetValue(mode, out var defaults))
        {
            Array.Copy(defaults, slots, defaults.Length);
        }
        return slots;
    }

    private static byte[][] BuildWheel60()
    {
        // A ramp up 0, 8, 21 .. 242 and its mirror 255, 242 .. 8, each 20 steps; every channel
        // rises over one third of the turn, falls over the next and rests for the last.
        var up = new byte[20];
        var down = new byte[20];
        for (int i = 0; i < 20; i++)
        {
            up[i] = (byte)(i == 0 ? 0 : (13 * i) - 5);
            down[i] = (byte)(255 - (13 * i));
        }
        var r = new byte[60];
        var g = new byte[60];
        var b = new byte[60];
        for (int i = 0; i < 20; i++)
        {
            r[i] = down[i];
            r[40 + i] = up[i];
            g[i] = up[i];
            g[20 + i] = down[i];
            b[20 + i] = up[i];
            b[40 + i] = down[i];
        }
        return new[] { r, g, b };
    }

    private static byte Dim(int value, int bright) => (byte)(value * bright >> 8);

    private static Rgb Dim(Rgb c, int bright) => new(Dim(c.R, bright), Dim(c.G, bright), Dim(c.B, bright));

    private static Rgb Level(Rgb c, int level) => new(c.R * level >> 8, c.G * level >> 8, c.B * level >> 8);

    private static void Put(byte[] frame, int led, Rgb c)
    {
        frame[led * 3] = (byte)c.R;
        frame[(led * 3) + 1] = (byte)c.G;
        frame[(led * 3) + 2] = (byte)c.B;
    }

    private static byte[] Solid(Rgb c)
    {
        var frame = new byte[FrameBytes];
        for (int led = 0; led < LedCount; led++)
        {
            Put(frame, led, c);
        }
        return frame;
    }

    private static byte[] Repeated(Rgb[] pattern)
    {
        var frame = new byte[FrameBytes];
        for (int led = 0; led < LedCount; led++)
        {
            Put(frame, led, pattern[led % pattern.Length]);
        }
        return frame;
    }

    /// <summary>A 31-LED strip from LED 47: forward through 59 and 0..17, and backward from 47 down to 17.</summary>
    private static byte[] LongStrip(Rgb[] strip)
    {
        var frame = new byte[FrameBytes];
        for (int k = 0; k < 13; k++)
        {
            Put(frame, k + 47, strip[k]);
        }
        for (int k = 0; k < 18; k++)
        {
            Put(frame, k, strip[k + 13]);
        }
        for (int k = 0; k < 31; k++)
        {
            Put(frame, 47 - k, strip[k]);
        }
        return frame;
    }

    /// <summary>
    /// Two 16-LED strips, both starting at the top: one from LED 47 through 59 and 0..2, back down from
    /// 47 to 32 and from 17 to 2, the other from 17 up to 32. <paramref name="second"/> null draws the
    /// first strip in all four places.
    /// </summary>
    private static byte[] QuadStrips(Rgb[] first, Rgb[]? second = null)
    {
        second ??= first;
        var frame = new byte[FrameBytes];
        for (int k = 0; k < 13; k++)
        {
            Put(frame, k + 47, first[k]);
        }
        for (int k = 0; k < 3; k++)
        {
            Put(frame, k, first[k + 13]);
        }
        for (int k = 0; k < 16; k++)
        {
            Put(frame, 17 - k, second[k]);
        }
        for (int k = 0; k < 16; k++)
        {
            Put(frame, 47 - k, first[k]);
        }
        for (int k = 0; k < 16; k++)
        {
            Put(frame, k + 17, second[k]);
        }
        return frame;
    }

    /// <summary>A 25-LED strip: its first LED fills 44..50, the next nine 51..59, the next fourteen 0..13 and its last 14..20; its start runs back from 43 to 21.</summary>
    private static byte[] FoldedStrip(Rgb[] strip)
    {
        var frame = new byte[FrameBytes];
        for (int k = 0; k < 7; k++)
        {
            Put(frame, k + 44, strip[0]);
        }
        for (int k = 0; k < 9; k++)
        {
            Put(frame, k + 51, strip[k + 1]);
        }
        for (int k = 0; k < 14; k++)
        {
            Put(frame, k, strip[k + 10]);
        }
        for (int k = 0; k < 7; k++)
        {
            Put(frame, k + 14, strip[24]);
        }
        for (int k = 0; k < 23; k++)
        {
            Put(frame, 43 - k, strip[k + 1]);
        }
        return frame;
    }

    private static List<byte[]> RenderRainbow(int bright, int dir)
    {
        const int half = 30;
        var frames = new List<byte[]>(half);
        var line = new Rgb[half];
        for (int shift = 0; shift < half; shift++)
        {
            for (int j = 0; j < half; j++)
            {
                var w = (shift + j) % half;
                line[j] = new Rgb(Dim(Wheel30[0][w], bright), Dim(Wheel30[1][w], bright), Dim(Wheel30[2][w], bright));
            }
            var frame = new byte[FrameBytes];
            for (int side = 0; side < 2; side++)
            {
                for (int j = 0; j < half; j++)
                {
                    Put(frame, (side * half) + j, line[dir == 0 ? half - 1 - j : j]);
                }
            }
            frames.Add(frame);
        }
        return frames;
    }

    private static List<byte[]> RenderWave(Rgb[] palette, int bright, int dir)
    {
        const int span = 12;
        var frames = new List<byte[]>(6 * 4 * span);
        var strip = new Rgb[span];
        for (int color = 0; color < 6; color++)
        {
            for (int pass = 0; pass < 4; pass++)
            {
                for (int step = 0; step < span; step++)
                {
                    for (int l = 0; l < span; l++)
                    {
                        strip[dir == 0 ? span - 1 - l : l] = Dim(Level(palette[color], WaveProfile[(step + l) % span]), bright);
                    }
                    frames.Add(Repeated(strip));
                }
            }
        }
        return frames;
    }

    private static List<byte[]> RenderBreathing(Rgb[] palette, int bright)
    {
        var frames = new List<byte[]>(170);
        for (int half = 0; half < 2; half++)
        {
            for (int step = 0; step < 85; step++)
            {
                var level = (step * 3) & 0xFF;
                frames.Add(Solid(Dim(Level(palette[0], half == 1 ? 255 - level : level), bright)));
            }
        }
        return frames;
    }

    /// <summary>A solid colour round the wheel in 255 steps of 3, every other step kept.</summary>
    private static List<byte[]> RenderMorph(int bright)
    {
        var all = new List<byte[]>(255);
        int r = 255, g = 0, b = 0;
        for (int i = 0; i < 255; i++)
        {
            all.Add(Solid(new Rgb(Dim(r, bright), Dim(g, bright), Dim(b, bright))));
            if (i < 85)
            {
                r -= 3;
                g += 3;
                b = 0;
            }
            else if (i < 170)
            {
                r = 0;
                g -= 3;
                b += 3;
            }
            else
            {
                r += 3;
                g = 0;
                b -= 3;
            }
        }
        var frames = new List<byte[]>(127);
        for (int k = 0; k < 127; k++)
        {
            frames.Add(all[k * 2]);
        }
        return frames;
    }

    /// <summary>Each colour wipes over the one before it along the long strip, every other colour from the far end.</summary>
    private static List<byte[]> RenderPaint(Rgb[] palette, int bright)
    {
        const int span = 31;
        var frames = new List<byte[]>(6 * span);
        var strip = new Rgb[span];
        for (int color = 0; color < 6; color++)
        {
            var previous = color == 0 ? 5 : color - 1;
            for (int front = 0; front < span; front++)
            {
                for (int k = 0; k < span; k++)
                {
                    strip[color % 2 == 1 ? span - 1 - k : k] = Dim(palette[k > front ? previous : color], bright);
                }
                frames.Add(LongStrip(strip));
            }
        }
        return frames;
    }

    /// <summary>A four-LED block of the second colour runs over the first along the long strip, there and back.</summary>
    private static List<byte[]> RenderRunway(Rgb[] palette, int bright)
    {
        const int span = 31;
        var frames = new List<byte[]>(2 * 35);
        var strip = new Rgb[span];
        for (int pass = 0; pass < 2; pass++)
        {
            for (int head = 0; head < 35; head++)
            {
                for (int k = 0; k < span; k++)
                {
                    var inBlock = k <= head && k + 4 > head;
                    strip[pass == 1 ? span - 1 - k : k] = Dim(palette[inBlock ? 1 : 0], bright);
                }
                frames.Add(LongStrip(strip));
            }
        }
        return frames;
    }

    /// <summary>Each colour wipes over the one before it on the four short strips.</summary>
    private static List<byte[]> RenderTide(Rgb[] palette, int bright)
    {
        const int span = 16;
        var frames = new List<byte[]>(6 * span);
        var strip = new Rgb[span];
        for (int color = 0; color < 6; color++)
        {
            var previous = color == 0 ? 5 : color - 1;
            for (int front = 0; front < span; front++)
            {
                for (int k = 0; k < span; k++)
                {
                    strip[k] = Dim(palette[k > front ? previous : color], bright);
                }
                frames.Add(QuadStrips(strip));
            }
        }
        return frames;
    }

    /// <summary>Each colour fills the short strips from their far ends, then the bezel fades out.</summary>
    private static List<byte[]> RenderBlowUp(Rgb[] palette, int bright)
    {
        const int span = 16;
        var frames = new List<byte[]>(6 * (span + 35));
        var strip = new Rgb[span];
        for (int color = 0; color < 6; color++)
        {
            for (int fill = 0; fill < span; fill++)
            {
                for (int k = 0; k < span; k++)
                {
                    strip[span - 1 - k] = k <= fill ? Dim(palette[color], bright) : default;
                }
                frames.Add(QuadStrips(strip));
            }
            for (int step = 0; step < 35; step++)
            {
                var level = step < 25 ? (25 - step - 1) * 10 : 0;
                frames.Add(Solid(Dim(Level(palette[color], level), bright)));
            }
        }
        return frames;
    }

    private static List<byte[]> RenderMeteor(Rgb[] palette, int bright, int dir)
    {
        const int span = 25;
        var frames = new List<byte[]>(6 * 30);
        var strip = new Rgb[span];
        for (int color = 0; color < 6; color++)
        {
            for (int head = 0; head < 30; head++)
            {
                var tail = 0;
                for (int k = 0; k < span; k++)
                {
                    var c = default(Rgb);
                    if (k <= head && k + MeteorTail.Length > head)
                    {
                        c = Level(palette[color], MeteorTail[tail++]);
                    }
                    strip[dir == 1 ? span - 1 - k : k] = Dim(c, bright);
                }
                frames.Add(FoldedStrip(strip));
            }
        }
        return frames;
    }

    /// <summary>A three-LED ball rolls out and back along the short strips in each colour.</summary>
    private static List<byte[]> RenderSnooker(Rgb[] palette, int bright)
    {
        const int span = 16;
        var frames = new List<byte[]>(6 * 2 * 14);
        var strip = new Rgb[span];
        for (int color = 0; color < 6; color++)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                for (int head = 1; head < 15; head++)
                {
                    for (int k = 0; k < span; k++)
                    {
                        var lit = k <= head && k + 3 > head;
                        strip[pass == 1 ? span - 1 - k : k] = lit ? Dim(palette[color], bright) : default;
                    }
                    frames.Add(QuadStrips(strip));
                }
            }
        }
        return frames;
    }

    /// <summary>The two colours run in on their own strips, then their sum fills back out.</summary>
    private static List<byte[]> RenderMixing(Rgb[] palette, int bright)
    {
        const int span = 16;
        var a = palette[0];
        var b = palette[1];
        var mix = new Rgb(Math.Min(a.R + b.R, 255), Math.Min(a.G + b.G, 255), Math.Min(a.B + b.B, 255));
        if (mix.R + mix.G + mix.B > 536)
        {
            mix = Level(mix, 178);
        }
        var frames = new List<byte[]>(2 * span);
        var line = new Rgb[2 * span];
        var first = new Rgb[span];
        var second = new Rgb[span];
        for (int pass = 0; pass < 2; pass++)
        {
            for (int head = 0; head < span; head++)
            {
                for (int k = 0; k < span; k++)
                {
                    var c = pass == 0 ? (k <= head && k + 3 > head ? a : default) : (k <= head ? mix : default);
                    line[pass == 1 ? span - 1 - k : k] = Dim(c, bright);
                }
                for (int k = 0; k < span; k++)
                {
                    var c = pass == 0 ? (k <= head && k + 3 > head ? b : default) : (k <= head ? mix : default);
                    line[pass == 1 ? k + span : (2 * span) - 1 - k] = Dim(c, bright);
                }
                for (int k = 0; k < span; k++)
                {
                    first[k] = line[k];
                    second[k] = line[(2 * span) - 1 - k];
                }
                frames.Add(QuadStrips(first, second));
            }
        }
        return frames;
    }

    /// <summary>A four-LED paddle bounces along the folded strip in each colour.</summary>
    private static List<byte[]> RenderPingPong(Rgb[] palette, int bright)
    {
        const int span = 25;
        var frames = new List<byte[]>(6 * 2 * 29);
        var strip = new Rgb[span];
        for (int color = 0; color < 6; color++)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                for (int head = 0; head < 29; head++)
                {
                    for (int k = 0; k < span; k++)
                    {
                        var lit = k <= head && k + 4 > head;
                        strip[pass == 1 ? span - 1 - k : k] = lit ? Dim(palette[color], bright) : default;
                    }
                    frames.Add(FoldedStrip(strip));
                }
            }
        }
        return frames;
    }

    /// <summary>Bullets in the seven-colour palette shoot along the folded strip and stack at its end in the previous bullet's colour.</summary>
    private static List<byte[]> RenderBulletStack(int bright, int dir)
    {
        const int span = 25;
        var frames = new List<byte[]>(325);
        var strip = new Rgb[span];
        var colorIndex = 0;
        for (int stacked = 0; stacked < span; stacked++)
        {
            colorIndex = colorIndex + 1 >= Party.Length ? 0 : colorIndex + 1;
            var stackColor = Party[colorIndex == 0 ? Party.Length - 1 : colorIndex - 1];
            var free = span - stacked;
            for (int bullet = 0; bullet < free; bullet++)
            {
                for (int k = 0; k < free; k++)
                {
                    strip[dir == 1 ? span - 1 - k : k] = Dim(k == bullet ? Party[colorIndex] : default, bright);
                }
                for (int k = 0; k < stacked; k++)
                {
                    var pos = free + k;
                    strip[dir == 1 ? span - 1 - pos : pos] = Dim(stackColor, bright);
                }
                frames.Add(FoldedStrip(strip));
            }
        }
        return frames;
    }

    private static List<byte[]> RenderTwinkle(int bright, Random random)
    {
        var colorByLed = new int[LedCount];
        for (int led = 0; led < LedCount; led++)
        {
            colorByLed[led] = random.Next(Party.Length);
        }
        var table = FlexTwinkleTable.Table;
        var frames = new List<byte[]>(200);
        for (int f = 0; f < 200; f++)
        {
            var frame = new byte[FrameBytes];
            for (int led = 0; led < LedCount; led++)
            {
                var c = Party[colorByLed[led]];
                var level = table[f, led];
                Put(frame, led, Dim(new Rgb((byte)(c.R * level >> 8), (byte)(c.G * level >> 8), (byte)(c.B * level >> 8)), bright));
            }
            frames.Add(frame);
        }
        return frames;
    }

    /// <summary>Runs of four first-colour and two second-colour LEDs flow round the bezel.</summary>
    private static List<byte[]> RenderRiver(Rgb[] palette, int bright, int dir)
    {
        const int span = 6;
        var frames = new List<byte[]>(span);
        var strip = new Rgb[span];
        for (int step = 0; step < span; step++)
        {
            for (int j = 0; j < span; j++)
            {
                strip[dir == 0 ? span - 1 - j : j] = Dim(palette[RiverColorBySlot[(step + j) % span]], bright);
            }
            frames.Add(Repeated(strip));
        }
        return frames;
    }

    /// <summary>Each of four colours runs through L-Connect's 70-frame hourglass table on the long strip.</summary>
    private static List<byte[]> RenderHourglass(Rgb[] palette, int bright)
    {
        var table = UniversalScreen88LedTables.Hourglass;
        var rows = table.GetLength(0);
        var frames = new List<byte[]>(4 * rows);
        var strip = new Rgb[UniversalScreen88LedTables.StripLeds];
        for (int color = 0; color < 4; color++)
        {
            for (int row = 0; row < rows; row++)
            {
                for (int k = 0; k < strip.Length; k++)
                {
                    strip[k] = Dim(Level(palette[color], table[row, k]), bright);
                }
                frames.Add(LongStrip(strip));
            }
        }
        return frames;
    }

    /// <summary>
    /// Each of four colours plays L-Connect's spark table on the long strip: the first row held five
    /// frames, rows 1..28 one each, then every other row to the end; cell 2 is white.
    /// </summary>
    private static List<byte[]> RenderElectricCurrent(Rgb[] palette, int bright)
    {
        var table = UniversalScreen88LedTables.ElectricCurrent;
        var rows = table.GetLength(0);
        var sequence = new List<int>();
        for (int hold = 0; hold < 5; hold++)
        {
            sequence.Add(0);
        }
        for (int row = 1; row < rows; row = row >= 28 ? row + 2 : row + 1)
        {
            sequence.Add(row);
        }
        var frames = new List<byte[]>(4 * sequence.Count);
        var strip = new Rgb[UniversalScreen88LedTables.StripLeds];
        for (int color = 0; color < 4; color++)
        {
            foreach (var row in sequence)
            {
                for (int k = 0; k < strip.Length; k++)
                {
                    var cell = table[row, k];
                    var c = cell == 2 ? White : cell != 0 ? palette[color] : default;
                    strip[k] = Dim(c, bright);
                }
                frames.Add(LongStrip(strip));
            }
        }
        return frames;
    }

    /// <summary>The 60-step colour wheel at double pitch through dark-gapped bands.</summary>
    private static List<byte[]> RenderRainbowWave(int bright, int dir)
    {
        var frames = new List<byte[]>(LedCount);
        var line = new Rgb[LedCount];
        for (int shift = 0; shift < LedCount; shift++)
        {
            for (int j = 0; j < LedCount; j++)
            {
                var w = (shift + (2 * j)) % LedCount;
                line[j] = RainbowWaveMask[(shift + j) % LedCount] == 0
                    ? default
                    : new Rgb(Dim(Wheel60[0][w], bright), Dim(Wheel60[1][w], bright), Dim(Wheel60[2][w], bright));
            }
            var frame = new byte[FrameBytes];
            for (int led = 0; led < LedCount; led++)
            {
                Put(frame, led, line[dir == 0 ? LedCount - 1 - led : led]);
            }
            frames.Add(frame);
        }
        return frames;
    }

    private readonly record struct Rgb(int R, int G, int B);
}
