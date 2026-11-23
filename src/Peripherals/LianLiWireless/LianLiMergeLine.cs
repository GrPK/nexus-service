namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>
/// How each device takes its stretch of L-Connect's merged line (RgbEffect.GetTL / GetHS2 / GetL88):
/// dots <c>start</c>..<c>stop</c> of the line's frames, read backwards when the device's merge
/// direction is 1, laid onto the device's LEDs. Each returns the device's frames packed R,G,B per LED,
/// frame after frame.
/// </summary>
public static class LianLiMergeLine
{
    public const int TlDotsPerFan = 13;
    public const int CurveDots = 14;
    public const int CurveLeds = 35;
    public const int Screen88Dots = 31;
    public const int Screen88Leds = 60;

    private static int Dot(int start, int stop, int index, int direction) => direction == 1 ? stop + start - index : index;

    private static void Put(byte[] frames, int frame, int leds, int led, byte[,,] line, int dot)
    {
        var o = ((frame * leds) + led) * 3;
        frames[o] = line[frame, 0, dot];
        frames[o + 1] = line[frame, 1, dot];
        frames[o + 2] = line[frame, 2, dot];
    }

    /// <summary>GetTL: 13 dots per fan, the same run on both halves of the fan.</summary>
    public static byte[] Tl(int start, int stop, int frames, byte[,,] line, int direction)
    {
        var fans = (stop + 1 - start) / TlDotsPerFan;
        var leds = fans * 26;
        var output = new byte[frames * leds * 3];
        for (var f = 0; f < frames; f++)
        {
            for (var fan = 0; fan < fans; fan++)
            {
                for (var j = 0; j < TlDotsPerFan; j++)
                {
                    var dot = Dot(start, stop, start + (fan * TlDotsPerFan) + j, direction);
                    Put(output, f, leds, (fan * 26) + j, line, dot);
                    Put(output, f, leds, (fan * 26) + TlDotsPerFan + j, line, dot);
                }
            }
        }
        return output;
    }

    /// <summary>GetHS2: LEDs 14..20 all take the first dot, 13..0 and 21..34 the 14 dots outwards.</summary>
    public static byte[] Curve(int start, int stop, int frames, byte[,,] line, int direction)
    {
        var output = new byte[frames * CurveLeds * 3];
        for (var f = 0; f < frames; f++)
        {
            for (var j = 0; j < 7; j++)
            {
                Put(output, f, CurveLeds, j + 14, line, Dot(start, stop, start, direction));
            }
            for (var j = 0; j < CurveDots; j++)
            {
                Put(output, f, CurveLeds, 13 - j, line, Dot(start, stop, start + j, direction));
            }
            for (var j = 0; j < CurveDots; j++)
            {
                Put(output, f, CurveLeds, j + 21, line, Dot(start, stop, start + j, direction));
            }
        }
        return output;
    }

    /// <summary>GetL88: dots 0..12 on LEDs 47..59, dots 13..30 on LEDs 0..17, dots 0..30 on LEDs 47..17.</summary>
    public static byte[] Screen88(int start, int stop, int frames, byte[,,] line, int direction)
    {
        var output = new byte[frames * Screen88Leds * 3];
        for (var f = 0; f < frames; f++)
        {
            for (var j = 0; j < 13; j++)
            {
                Put(output, f, Screen88Leds, j + 47, line, Dot(start, stop, start + j, direction));
            }
            for (var j = 0; j < 18; j++)
            {
                Put(output, f, Screen88Leds, j, line, Dot(start, stop, start + j + 13, direction));
            }
            for (var j = 0; j < Screen88Dots; j++)
            {
                Put(output, f, Screen88Leds, 47 - j, line, Dot(start, stop, start + j, direction));
            }
        }
        return output;
    }
}
