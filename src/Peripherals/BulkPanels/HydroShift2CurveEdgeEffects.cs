using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.LianLiFlex;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>
/// The HydroShift II OLED Curved edge effects as L-Connect renders them: the same 19 modes, palette
/// defaults, slider mapping and frame timing, streamed by the PC one frame at a time (the board has no
/// effects of its own). Each frame is <see cref="HydroShift2CurveProtocol.LedCount"/> packed RGB triples.
/// </summary>
public static class HydroShift2CurveEdgeEffects
{
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

    private const int Leds = HydroShift2CurveProtocol.LedCount;
    private const int FrameBytes = Leds * 3;
    private const int Half = 18;

    /// <summary>Each frame's interval is the speed step times this many ms.</summary>
    private const int IntervalBaseMs = 11;

    private static readonly Rgb Red = new(255, 0, 0);
    private static readonly Rgb Green = new(0, 255, 0);
    private static readonly Rgb Blue = new(0, 0, 255);
    private static readonly Rgb Yellow = new(255, 255, 0);
    private static readonly Rgb Cyan = new(0, 255, 255);
    private static readonly Rgb Magenta = new(255, 0, 255);
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

    /// <summary>Which <see cref="Party"/> colour each LED twinkles in.</summary>
    private static readonly byte[] TwinkleColorByLed =
    {
        1, 0, 6, 3, 4, 2, 5, 0, 1, 3, 6, 4, 1, 0, 5, 2, 4, 3, 1, 0,
        2, 6, 3, 4, 1, 0, 2, 5, 1, 3, 4, 2, 6, 1, 2,
    };

    /// <summary>One turn of the colour wheel in 36 steps, per channel.</summary>
    private static readonly byte[][] Wheel =
    {
        new byte[] { 255, 232, 209, 186, 163, 140, 117, 94, 71, 48, 25, 10, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 10, 25, 48, 71, 94, 117, 140, 163, 186, 209, 232 },
        new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 10, 25, 48, 71, 94, 117, 140, 163, 186, 209, 232, 255, 232, 209, 186, 163, 140, 117, 94, 71, 48, 25, 10 },
        new byte[] { 0, 10, 25, 48, 71, 94, 117, 140, 163, 186, 209, 232, 255, 232, 209, 186, 163, 140, 117, 94, 71, 48, 25, 10, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
    };

    private static readonly byte[] WaveProfile = { 0, 0, 16, 48, 128, 255, 128, 48, 16, 0, 0, 0 };
    private static readonly byte[] MeteorTail = { 16, 32, 64, 128, 255 };
    private static readonly byte[] RiverColorBySlot = { 0, 0, 0, 0, 1, 1 };

    /// <summary>Rainbow Wave's lit runs: dark gaps of five between bands of seven (the last band six).</summary>
    private static readonly byte[] RainbowWaveMask =
    {
        0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1,
    };

    /// <summary>Colour pickers the mode shows; 0 means the mode draws its own colours.</summary>
    public static int ColorCount(string mode) => DefaultPalettes.TryGetValue(mode, out var p) ? p.Length : 0;

    /// <summary>Modes whose direction L-Connect lets the user flip.</summary>
    public static bool HasDirection(string mode) =>
        mode is Rainbow or Wave or Meteor or BulletStack or River or RainbowWave;

    /// <summary>Static is one frame; every other mode has a speed.</summary>
    public static bool HasSpeed(string mode) => mode != Static;

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

    /// <summary>
    /// Ms between frames: L-Connect sleeps 0.625 of the step's interval (truncated) after each frame's
    /// three USB writes.
    /// </summary>
    public static int FramePeriodMs(int speedStep) => (int)(Math.Clamp(speedStep, 3, 7) * IntervalBaseMs * 0.625);

    /// <summary>
    /// Renders <paramref name="mode"/>'s whole loop. <paramref name="colors"/> are the user's (up to six);
    /// an empty list falls back to the mode's defaults. <paramref name="brightness"/> is 0..255 and
    /// <paramref name="reverse"/> is L-Connect's counter-clockwise direction.
    /// </summary>
    public static List<byte[]> Render(string mode, IReadOnlyList<(byte R, byte G, byte B)> colors, int brightness, bool reverse)
    {
        var palette = Palette(mode, colors);
        var bright = RendererBrightness(brightness);
        var dir = reverse ? 1 : 0;
        return mode switch
        {
            Rainbow => RenderRainbow(bright, dir),
            Wave => RenderWave(palette, bright, dir),
            Static => RenderStatic(palette, bright),
            Breathing => RenderBreathing(palette, bright),
            RainbowMorph => RenderMorph(bright),
            Paint => RenderPaint(palette, bright, flipEveryOtherColor: true, dir: 0),
            Runway => RenderRunway(palette, bright),
            // Tide draws like Paint but its sweep follows the direction instead of alternating per colour.
            Tide => RenderPaint(palette, bright, flipEveryOtherColor: false, dir),
            BlowUp => RenderBlowUp(palette, bright),
            Meteor => RenderMeteor(palette, bright, dir),
            Snooker => RenderSnooker(palette, bright),
            Mixing => RenderMixing(palette, bright),
            PingPong => RenderPingPong(palette, bright),
            BulletStack => RenderBulletStack(bright, dir),
            Twinkle => RenderTwinkle(bright),
            River => RenderRiver(palette, bright, dir),
            Hourglass => RenderHourglass(palette, bright),
            ElectricCurrent => RenderElectricCurrent(palette, bright),
            RainbowWave => RenderRainbowWave(bright, dir),
            _ => throw new ArgumentException($"unknown HydroShift II OLED Curved effect '{mode}'", nameof(mode)),
        };
    }

    /// <summary>L-Connect dims its two middle brightness steps (64 and 128) a further quarter on this cooler.</summary>
    private static int RendererBrightness(int brightness)
    {
        var b = Math.Clamp(brightness, 0, 255);
        return b is 64 or 128 ? (int)(b * 0.75) : b;
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

    private static byte Dim(int value, int bright) => (byte)(value * bright >> 8);

    private static Rgb Dim(Rgb c, int bright) => new(Dim(c.R, bright), Dim(c.G, bright), Dim(c.B, bright));

    private static Rgb Level(Rgb c, int level) => new(c.R * level >> 8, c.G * level >> 8, c.B * level >> 8);

    private static void Put(byte[] frame, int led, Rgb c)
    {
        frame[led * 3] = (byte)c.R;
        frame[(led * 3) + 1] = (byte)c.G;
        frame[(led * 3) + 2] = (byte)c.B;
    }

    private static Rgb Get(byte[] frame, int led) => new(frame[led * 3], frame[(led * 3) + 1], frame[(led * 3) + 2]);

    private static byte[] Solid(Rgb c)
    {
        var frame = new byte[FrameBytes];
        for (int led = 0; led < Leds; led++)
        {
            Put(frame, led, c);
        }
        return frame;
    }

    /// <summary>An 18-LED strip drawn twice, mirrored about LED 17: LEDs 17..0 and 17..34 both show it from its start.</summary>
    private static byte[] MirroredHalves(Rgb[] strip)
    {
        var frame = new byte[FrameBytes];
        for (int k = 0; k < Half; k++)
        {
            Put(frame, Half - 1 - k, strip[k]);
        }
        for (int k = 0; k < Half; k++)
        {
            Put(frame, k + Half - 1, strip[k]);
        }
        return frame;
    }

    /// <summary>A 15-LED strip down the first 14 LEDs and back up the last 14, its last LED filling the 7 between.</summary>
    private static byte[] FoldedStrip(Rgb[] strip)
    {
        var frame = new byte[FrameBytes];
        for (int k = 0; k < 14; k++)
        {
            Put(frame, k, strip[k]);
        }
        for (int k = 0; k < 7; k++)
        {
            Put(frame, k + 14, strip[14]);
        }
        for (int k = 0; k < 14; k++)
        {
            Put(frame, 34 - k, strip[k]);
        }
        return frame;
    }

    /// <summary>The whole edge lit with one fading colour, (24 - step) * 10 for 25 steps.</summary>
    private static void AddFadeOut(List<byte[]> frames, Rgb color, int bright, int steps)
    {
        for (int step = 0; step < steps; step++)
        {
            var level = step < 25 ? (25 - step - 1) * 10 : 0;
            frames.Add(Solid(Dim(Level(color, level), bright)));
        }
    }

    private static List<byte[]> RenderRainbow(int bright, int dir)
    {
        var frames = new List<byte[]>(Leds);
        var line = new Rgb[Leds];
        for (int shift = 0; shift < Leds; shift++)
        {
            for (int j = 0; j < Leds; j++)
            {
                var w = (shift + j) % Wheel[0].Length;
                line[j] = new Rgb(Dim(Wheel[0][w], bright), Dim(Wheel[1][w], bright), Dim(Wheel[2][w], bright));
            }
            var frame = new byte[FrameBytes];
            for (int led = 0; led < Leds; led++)
            {
                Put(frame, led, line[dir == 0 ? Leds - 1 - led : led]);
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
                        var shade = WaveProfile[(step + l) % span];
                        strip[dir == 0 ? span - 1 - l : l] = Dim(Level(palette[color], shade), bright);
                    }
                    var frame = new byte[FrameBytes];
                    for (int led = 0; led < Leds; led++)
                    {
                        Put(frame, led, strip[led % span]);
                    }
                    frames.Add(frame);
                }
            }
        }
        return frames;
    }

    private static List<byte[]> RenderStatic(Rgb[] palette, int bright) => new() { Solid(Dim(palette[0], bright)) };

    private static List<byte[]> RenderBreathing(Rgb[] palette, int bright)
    {
        var frames = new List<byte[]>(170);
        for (int half = 0; half < 2; half++)
        {
            for (int step = 0; step < 85; step++)
            {
                var level = (step * 3) & 0xFF;
                if (half == 1)
                {
                    level = 255 - level;
                }
                frames.Add(Solid(Dim(Level(palette[0], level), bright)));
            }
        }
        return frames;
    }

    /// <summary>
    /// A solid colour round the wheel in 255 steps of 3, every other step kept. On this cooler L-Connect
    /// itself fails to start this mode (its 127-frame buffer is narrower than the 45 LEDs it packs), so
    /// this plays the morph it means to draw.
    /// </summary>
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

    /// <summary>Each colour wipes over the one before it along both mirrored halves.</summary>
    private static List<byte[]> RenderPaint(Rgb[] palette, int bright, bool flipEveryOtherColor, int dir)
    {
        var frames = new List<byte[]>(6 * Half);
        var strip = new Rgb[Half];
        for (int color = 0; color < 6; color++)
        {
            var previous = color == 0 ? 5 : color - 1;
            var flip = flipEveryOtherColor ? color % 2 == 1 : dir == 1;
            for (int front = 0; front < Half; front++)
            {
                for (int k = 0; k < Half; k++)
                {
                    var c = palette[k > front ? previous : color];
                    strip[flip ? Half - 1 - k : k] = Dim(c, bright);
                }
                frames.Add(MirroredHalves(strip));
            }
        }
        return frames;
    }

    /// <summary>A four-LED block of the first colour runs over the second, there and back.</summary>
    private static List<byte[]> RenderRunway(Rgb[] palette, int bright)
    {
        var frames = new List<byte[]>(2 * 22);
        var strip = new Rgb[Half];
        for (int pass = 0; pass < 2; pass++)
        {
            for (int head = 0; head < 22; head++)
            {
                for (int k = 0; k < Half; k++)
                {
                    var inBlock = k <= head && k + 4 > head;
                    strip[pass == 1 ? Half - 1 - k : k] = Dim(palette[inBlock ? 0 : 1], bright);
                }
                frames.Add(MirroredHalves(strip));
            }
        }
        return frames;
    }

    /// <summary>Each colour fills an eight-LED pattern from the centre of each side, then the edge fades out.</summary>
    private static List<byte[]> RenderBlowUp(Rgb[] palette, int bright)
    {
        var frames = new List<byte[]>(6 * (8 + Leds));
        var strip = new Rgb[8];
        for (int color = 0; color < 6; color++)
        {
            for (int fill = 0; fill < 8; fill++)
            {
                for (int k = 0; k < 8; k++)
                {
                    strip[k] = k <= fill ? Dim(palette[color], bright) : default;
                }
                var frame = new byte[FrameBytes];
                for (int k = 0; k < 8; k++)
                {
                    Put(frame, 7 - k, strip[k]);
                }
                for (int k = 0; k < 7; k++)
                {
                    Put(frame, k + 7, strip[k]);
                }
                for (int k = 0; k < 7; k++)
                {
                    Put(frame, k + 14, strip[7]);
                }
                for (int k = 0; k < 7; k++)
                {
                    Put(frame, 27 - k, strip[k]);
                }
                for (int k = 0; k < 8; k++)
                {
                    Put(frame, k + 27, strip[k]);
                }
                frames.Add(frame);
            }
            AddFadeOut(frames, palette[color], bright, Leds);
        }
        return frames;
    }

    private static List<byte[]> RenderMeteor(Rgb[] palette, int bright, int dir)
    {
        var frames = new List<byte[]>(6 * 23);
        var strip = new Rgb[Half];
        for (int color = 0; color < 6; color++)
        {
            for (int head = 0; head < 23; head++)
            {
                var tail = 0;
                for (int k = 0; k < Half; k++)
                {
                    var c = default(Rgb);
                    if (k <= head && k + MeteorTail.Length > head)
                    {
                        c = Level(palette[color], MeteorTail[tail++]);
                    }
                    strip[dir == 1 ? Half - 1 - k : k] = Dim(c, bright);
                }
                frames.Add(MirroredHalves(strip));
            }
        }
        return frames;
    }

    /// <summary>A three-LED ball rolls out and back along both halves in each colour.</summary>
    private static List<byte[]> RenderSnooker(Rgb[] palette, int bright)
    {
        var frames = new List<byte[]>(6 * 2 * 16);
        var strip = new Rgb[Half];
        for (int color = 0; color < 6; color++)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                for (int head = 1; head < 17; head++)
                {
                    for (int k = 0; k < Half; k++)
                    {
                        var lit = k <= head && k + 3 > head;
                        strip[pass == 1 ? Half - 1 - k : k] = lit ? Dim(palette[color], bright) : default;
                    }
                    frames.Add(MirroredHalves(strip));
                }
            }
        }
        return frames;
    }

    /// <summary>The two colours run in from both ends, then their sum fills back out.</summary>
    private static List<byte[]> RenderMixing(Rgb[] palette, int bright)
    {
        var a = palette[0];
        var b = palette[1];
        var mix = new Rgb(Math.Min(a.R + b.R, 255), Math.Min(a.G + b.G, 255), Math.Min(a.B + b.B, 255));
        if (mix.R + mix.G + mix.B > 536)
        {
            mix = Level(mix, 178);
        }
        var frames = new List<byte[]>(2 * Half);
        var line = new Rgb[2 * Half];
        for (int pass = 0; pass < 2; pass++)
        {
            for (int head = 0; head < Half; head++)
            {
                for (int k = 0; k < Half; k++)
                {
                    var c = pass == 0
                        ? (k <= head && k + 3 > head ? a : default)
                        : (k <= head ? mix : default);
                    line[pass == 1 ? Half - 1 - k : k] = Dim(c, bright);
                }
                for (int k = 0; k < Half; k++)
                {
                    var c = pass == 0
                        ? (k <= head && k + 3 > head ? b : default)
                        : (k <= head ? mix : default);
                    line[pass == 1 ? k + Half : (2 * Half) - 1 - k] = Dim(c, bright);
                }
                var frame = new byte[FrameBytes];
                for (int k = 0; k < Half; k++)
                {
                    Put(frame, k, line[k]);
                }
                for (int k = 0; k < Half; k++)
                {
                    Put(frame, k + Half - 1, line[k + Half]);
                }
                frames.Add(frame);
            }
        }
        return frames;
    }

    /// <summary>A four-LED paddle bounces along the folded strip in each colour.</summary>
    private static List<byte[]> RenderPingPong(Rgb[] palette, int bright)
    {
        var frames = new List<byte[]>(6 * 2 * 19);
        var strip = new Rgb[15];
        for (int color = 0; color < 6; color++)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                for (int head = 0; head < 19; head++)
                {
                    for (int k = 0; k < 15; k++)
                    {
                        var lit = k <= head && k + 4 > head;
                        strip[pass == 1 ? 15 - 1 - k : k] = lit ? Dim(palette[color], bright) : default;
                    }
                    frames.Add(FoldedStrip(strip));
                }
            }
        }
        return frames;
    }

    /// <summary>
    /// Bullets in the seven-colour palette shoot down the folded strip and stack at its end; the stack
    /// shows the previous bullet's colour.
    /// </summary>
    private static List<byte[]> RenderBulletStack(int bright, int dir)
    {
        const int span = 15;
        var frames = new List<byte[]>(120);
        var strip = new Rgb[span];
        var colorIndex = Party.Length;
        for (int stacked = 0; stacked < span; stacked++)
        {
            colorIndex = colorIndex + 1 >= Party.Length ? 0 : colorIndex + 1;
            var stackColor = Party[colorIndex == 0 ? Party.Length - 1 : colorIndex - 1];
            var free = span - stacked;
            for (int bullet = 0; bullet < free; bullet++)
            {
                for (int k = 0; k < free; k++)
                {
                    var c = k == bullet ? Party[colorIndex] : default;
                    strip[dir == 1 ? span - 1 - k : k] = Dim(c, bright);
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

    private static List<byte[]> RenderTwinkle(int bright)
    {
        var table = FlexTwinkleTable.Table;
        var frames = new List<byte[]>(200);
        for (int f = 0; f < 200; f++)
        {
            var frame = new byte[FrameBytes];
            for (int led = 0; led < Leds; led++)
            {
                var c = Party[TwinkleColorByLed[led]];
                var level = table[f, led];
                var twinkled = new Rgb((byte)(c.R * level >> 8), (byte)(c.G * level >> 8), (byte)(c.B * level >> 8));
                Put(frame, led, Dim(twinkled, bright));
            }
            frames.Add(frame);
        }
        return frames;
    }

    /// <summary>Runs of four first-colour and two second-colour LEDs flow round the edge.</summary>
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
            var frame = new byte[FrameBytes];
            for (int led = 0; led < Leds; led++)
            {
                Put(frame, led, strip[led % span]);
            }
            frames.Add(frame);
        }
        return frames;
    }

    /// <summary>Each of four colours drains from both halves into the bottom, then the edge fades out.</summary>
    private static List<byte[]> RenderHourglass(Rgb[] palette, int bright)
    {
        var frames = new List<byte[]>(4 * ((4 * Half) + 25));
        for (int color = 0; color < 4; color++)
        {
            var c = Dim(palette[color], bright);
            for (int settled = 0; settled < 4; settled++)
            {
                for (int top = 0; top < Half; top++)
                {
                    var frame = new byte[FrameBytes];
                    for (int l = 0; l < Half; l++)
                    {
                        var lit = l < top || l >= Half - settled;
                        int pos = l;
                        if (settled == 3)
                        {
                            if (l >= Half - 3)
                            {
                                break;
                            }
                            pos = Half - l - 1 - 3;
                        }
                        Put(frame, pos, lit ? c : default);
                    }
                    for (int l = 0; l < Half; l++)
                    {
                        Put(frame, l + Half - 1, Get(frame, Half - l - 1));
                    }
                    frames.Add(frame);
                }
            }
            AddFadeOut(frames, palette[color], bright, 25);
        }
        return frames;
    }

    /// <summary>A six-LED spark charges at the start of each half, then shoots out and back in each of four colours.</summary>
    private static List<byte[]> RenderElectricCurrent(Rgb[] palette, int bright)
    {
        const int spark = 6;
        var frames = new List<byte[]>(4 * 40);
        for (int color = 0; color < 4; color++)
        {
            var c = Dim(palette[color], bright);
            var charge = 0;
            for (int pass = 0; pass < 2; pass++)
            {
                for (int head = 0; head < Half + spark - 1; head++)
                {
                    // The first ten frames of a colour hold the spark at the start.
                    if (charge < 10)
                    {
                        charge++;
                        head = 0;
                    }
                    var frame = new byte[FrameBytes];
                    for (int l = 0; l < Half; l++)
                    {
                        var lit = l < head && l + spark >= head;
                        Put(frame, pass != 0 ? Half - l - 1 : l, lit ? c : default);
                    }
                    for (int l = 0; l < Half; l++)
                    {
                        var lit = l < head && l + spark >= head;
                        Put(frame, (pass != 0 ? l : Half - l - 1) + Half - 1, lit ? c : default);
                    }
                    if (pass != 0)
                    {
                        head += 2;
                    }
                    frames.Add(frame);
                }
            }
        }
        return frames;
    }

    /// <summary>The colour wheel at double pitch through three dark-gapped bands.</summary>
    private static List<byte[]> RenderRainbowWave(int bright, int dir)
    {
        var frames = new List<byte[]>(Leds);
        var line = new Rgb[Leds];
        for (int shift = 0; shift < Leds; shift++)
        {
            for (int j = 0; j < Leds; j++)
            {
                var w = (shift + (2 * j)) % Wheel[0].Length;
                line[j] = RainbowWaveMask[(shift + j) % Leds] == 0
                    ? default
                    : new Rgb(Dim(Wheel[0][w], bright), Dim(Wheel[1][w], bright), Dim(Wheel[2][w], bright));
            }
            var frame = new byte[FrameBytes];
            for (int led = 0; led < Leds; led++)
            {
                Put(frame, led, line[dir == 0 ? Leds - 1 - led : led]);
            }
            frames.Add(frame);
        }
        return frames;
    }

    private readonly record struct Rgb(int R, int G, int B);
}
