using System.Collections.Generic;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>
/// The merged-line effects L-Connect 2.1.29 draws across several Lian Li devices at once (Quick Sync's
/// "wireless" effects): RgbEffect.GetUIeEffect picks one of these by UIEffect number and draws it over
/// a line of UI dots, every participating device in turn, before each device samples its own stretch.
/// The bodies follow L-Connect's UiMode1..30 step for step so the frames are the same byte for byte.
/// </summary>
public static class LianLiUiEffects
{
    /// <summary>The UIEffect numbers drawn on the shared line (RainbowMorph and Twinkle run per device instead).</summary>
    public static readonly IReadOnlyList<int> LineEffects = new[] { 1, 3, 4, 5, 6, 7, 9, 10, 11, 12, 16, 21, 22, 26, 27, 29, 30 };

    /// <summary>
    /// Draws <paramref name="uiEffect"/> over <paramref name="leds"/> dots (24..720): the frame count and
    /// the frames as [frame, channel, dot]. <paramref name="colors"/> is L-Connect's 6 x 3 palette
    /// (<see cref="Palette"/>); <paramref name="bright"/> is 0..255, <paramref name="direction"/> 0 or 1.
    /// </summary>
    public static (int Frames, byte[,,] Data) Render(int uiEffect, int leds, int bright, int direction, byte[,] colors) => uiEffect switch
    {
        1 => Mode1(leds, (byte)bright, (byte)direction),
        3 => Mode3(leds, (byte)bright, (byte)direction, colors),
        4 => Mode4(leds, (byte)bright, (byte)direction, colors),
        5 => Mode5(leds, (byte)bright, (byte)direction, colors),
        6 => Mode6(leds, bright, direction, colors),
        7 => Mode7(leds, (byte)bright, (byte)direction, colors),
        9 => Mode9(leds, bright, direction, colors),
        10 => Mode10(leds, bright, direction, colors),
        11 => Mode11(leds, bright, direction, colors),
        12 => Mode12(leds, bright, direction, colors),
        16 => Mode16(leds, bright, direction, colors),
        21 => Mode21(leds, bright, direction, colors),
        22 => Mode22(leds, bright, direction, colors),
        26 => Mode26(leds, bright, direction, colors),
        27 => Mode27(leds, bright, direction, colors),
        29 => Mode29(leds, bright, direction, colors),
        30 => Mode30(leds, bright, direction, colors),
        _ => (0, new byte[1, 3, 1]),
    };

    /// <summary>
    /// GetUIeEffect's palette: up to six user colours, each held to R + G + B of 600 by 5% steps
    /// (the same stepping as the TL colours); missing slots stay black.
    /// </summary>
    public static byte[,] Palette(IReadOnlyList<(byte R, byte G, byte B)> colors)
    {
        var palette = new byte[6, 3];
        for (var i = 0; i < colors.Count && i < 6; i++)
        {
            palette[i, 0] = colors[i].R;
            palette[i, 1] = colors[i].G;
            palette[i, 2] = colors[i].B;
        }
        for (var i = 0; i < 6; i++)
        {
            var (r, g, b) = Nexus.Service.Peripherals.LianLiFlex.FlexPalette.Cap(palette[i, 0], palette[i, 1], palette[i, 2]);
            palette[i, 0] = r;
            palette[i, 1] = g;
            palette[i, 2] = b;
        }
        return palette;
    }

    // L-Connect's generators, renamed ModeN; their local names are the decompiler's.
#pragma warning disable IDE0059, IDE0018, IDE0045, IDE0046, IDE0047, IDE0048, IDE0054, IDE0074
	private static (int Frames, byte[,,] Data) Mode1(int leds, byte bright, byte direction)
	{
		byte[,,] array = new byte[4096, 3, 720];
		int num = 0;
		byte[,] array2 = new byte[3, 1024];
		byte[,] array3 = new byte[3, 24]
		{
			{
				255, 224, 192, 160, 128, 96, 64, 32, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 32, 64, 96,
				128, 160, 192, 224
			},
			{
				0, 32, 64, 96, 128, 160, 192, 224, 255, 224,
				192, 160, 128, 96, 64, 32, 0, 0, 0, 0,
				0, 0, 0, 0
			},
			{
				0, 0, 0, 0, 0, 0, 0, 0, 0, 32,
				64, 96, 128, 160, 192, 224, 255, 224, 192, 160,
				128, 96, 64, 32
			}
		};
		try
		{
			if (leds < 24 || leds > 720)
			{
				leds = 24;
			}
			if (direction > 1)
			{
				direction = 0;
			}
			num = 0;
			for (int i = 0; i < 24; i++)
			{
				int num2 = i;
				int num3;
				int j;
				for (j = 0; j < 24; j++)
				{
					num3 = j;
					if (direction == 0)
					{
						num3 = 24 - j - 1;
					}
					array2[0, num3] = (byte)(array3[0, num2] * bright >> 8);
					array2[1, num3] = (byte)(array3[1, num2] * bright >> 8);
					array2[2, num3] = (byte)(array3[2, num2] * bright >> 8);
					num2++;
					if (num2 >= 24)
					{
						num2 -= 24;
					}
				}
				int num4 = num;
				num++;
				num3 = 1;
				j = 24;
				while (j < leds)
				{
					j += 24;
					num3++;
				}
				for (num2 = 0; num2 < num3; num2++)
				{
					for (j = 0; j < 24; j++)
					{
						int num5 = num2 * 24 + j;
						if (num5 < leds)
						{
							array[num4, 0, num2 * 24 + j] = array2[0, j];
							array[num4, 1, num2 * 24 + j] = array2[1, j];
							array[num4, 2, num2 * 24 + j] = array2[2, j];
						}
					}
				}
			}
		}
		catch (System.Exception)
		{
		}
		return (num, array);
	}

	private static (int Frames, byte[,,] Data) Mode3(int leds, byte bright, byte direction, byte[,] UiUserColour)
	{
		byte[,,] array = new byte[1, 3, 720];
		if (leds < 24 || leds > 720)
		{
			leds = 24;
		}
		if (direction > 1)
		{
			direction = 0;
		}
		for (int i = 0; i < leds; i++)
		{
			array[0, 0, i] = (byte)(UiUserColour[0, 0] * bright >> 8);
			array[0, 1, i] = (byte)(UiUserColour[0, 1] * bright >> 8);
			array[0, 2, i] = (byte)(UiUserColour[0, 2] * bright >> 8);
		}
		return (1, array);
	}

	private static (int Frames, byte[,,] Data) Mode4(int leds, byte bright, byte direction, byte[,] UiUserColour)
	{
		byte[,,] array = new byte[170, 3, 720];
		if (leds < 24 || leds > 720)
		{
			leds = 24;
		}
		if (direction > 1)
		{
			direction = 0;
		}
		int num = 0;
		int num2 = 0;
		for (int i = 0; i < 170; i++)
		{
			byte b = (byte)(num2 * 3);
			for (int j = 0; j < leds; j++)
			{
				int num3 = num;
				array[num3, 0, j] = (byte)(UiUserColour[0, 0] * b >> 8);
				array[num3, 0, j] = (byte)(array[num3, 0, j] * bright >> 8);
				array[num3, 1, j] = (byte)(UiUserColour[0, 1] * b >> 8);
				array[num3, 1, j] = (byte)(array[num3, 1, j] * bright >> 8);
				array[num3, 2, j] = (byte)(UiUserColour[0, 2] * b >> 8);
				array[num3, 2, j] = (byte)(array[num3, 2, j] * bright >> 8);
			}
			num++;
			num2 = ((i >= 85) ? (num2 - 1) : (num2 + 1));
		}
		return (num, array);
	}

	private static (int Frames, byte[,,] Data) Mode5(int leds, byte bright, byte direction, byte[,] UiUserColour)
	{
		byte[,,] array = new byte[2048, 3, 720];
		int num = 0;
		if (leds < 24 || leds > 720)
		{
			leds = 24;
		}
		if (direction > 1)
		{
			direction = 0;
		}
		byte b = 12;
		for (int i = 0; i < 2; i++)
		{
			for (int j = 0; j < leds + b - 1; j++)
			{
				for (int k = 0; k < leds; k++)
				{
					int num2 = 1;
					if (k <= j && k + b > j)
					{
						num2 = 0;
					}
					byte b2 = UiUserColour[num2, 0];
					byte b3 = UiUserColour[num2, 1];
					byte b4 = UiUserColour[num2, 2];
					int num3 = num;
					int num4 = k;
					if (i != 0)
					{
						num4 = leds - k - 1;
					}
					array[num3, 0, num4] = (byte)(b2 * bright >> 8);
					array[num3, 1, num4] = (byte)(b3 * bright >> 8);
					array[num3, 2, num4] = (byte)(b4 * bright >> 8);
				}
				num++;
			}
		}
		return (num, array);
	}

	private static (int Frames, byte[,,] Data) Mode6(int leds, int bright, int direction, byte[,] UiUserColour)
	{
		byte[] array = new byte[12]
		{
			12, 16, 20, 24, 32, 40, 48, 64, 96, 128,
			192, 255
		};
		byte[,] array2 = new byte[3, 1024];
		if (leds < 24 || leds > 720)
		{
			leds = 24;
		}
		if (direction > 1)
		{
			direction = 0;
		}
		int num = 12;
		int num2 = 0;
		for (int i = 0; i < leds + num; i++)
		{
			array2[0, i] = 0;
			array2[1, i] = 0;
			array2[2, i] = 0;
			if (i < num)
			{
				array2[0, i] = (byte)(UiUserColour[0, 0] * array[i] >> 8);
				array2[1, i] = (byte)(UiUserColour[0, 1] * array[i] >> 8);
				array2[2, i] = (byte)(UiUserColour[0, 2] * array[i] >> 8);
			}
		}
		byte[,,] array3 = new byte[leds + num, 3, leds];
		int num3 = num;
		for (int j = 0; j < leds + num; j++)
		{
			for (int i = 0; i < leds; i++)
			{
				int num4 = num2;
				int num5 = i;
				if (direction == 1)
				{
					num5 = leds - i - 1;
				}
				int num6 = num3 + i;
				if (num6 >= leds + num)
				{
					num6 = num6 - leds - num;
				}
				array3[num4, 0, num5] = (byte)(array2[0, num6] * bright >> 8);
				array3[num4, 1, num5] = (byte)(array2[1, num6] * bright >> 8);
				array3[num4, 2, num5] = (byte)(array2[2, num6] * bright >> 8);
			}
			num3 = ((num3 != 0) ? (num3 - 1) : (leds + num - 1));
			num2++;
		}
		return (num2, array3);
	}

	private static (int Frames, byte[,,] Data) Mode7(int leds, byte bright, byte direction, byte[,] UiUserColour)
	{
		byte[,] array = new byte[3, 1024];
		if (leds < 24 || leds > 720)
		{
			leds = 24;
		}
		byte[,,] array2 = new byte[4096, 3, leds];
		if (direction > 1)
		{
			direction = 0;
		}
		byte b = 12;
		int num = 1;
		int num2 = b;
		while (num2 < leds)
		{
			num2 += b;
			num++;
		}
		int num3 = 0;
		int num4 = 0;
		int num5 = 0;
		while (num5 < num)
		{
			for (int i = 0; i < leds - num4; i++)
			{
				for (num2 = 0; num2 < leds - num4; num2++)
				{
					byte b2 = 0;
					byte b3 = 0;
					byte b4 = 0;
					if (num2 <= i && num2 + b > i)
					{
						b2 = UiUserColour[0, 0];
						b3 = UiUserColour[0, 1];
						b4 = UiUserColour[0, 2];
					}
					int num6 = num2;
					if (direction == 1)
					{
						num6 = leds - num2 - 1;
					}
					array[0, num6] = (byte)(b2 * bright >> 8);
					array[1, num6] = (byte)(b3 * bright >> 8);
					array[2, num6] = (byte)(b4 * bright >> 8);
				}
				int num7 = num3;
				num3++;
				for (num2 = 0; num2 < leds; num2++)
				{
					array2[num7, 0, num2] = array[0, num2];
					array2[num7, 1, num2] = array[1, num2];
					array2[num7, 2, num2] = array[2, num2];
				}
			}
			num5++;
			num4 += b;
		}
		return (num3, array2);
	}

	private static (int Frames, byte[,,] Data) Mode9(int leds, int bright, int direction, byte[,] UiUserColour)
	{
		byte[] array = new byte[48]
		{
			1, 1, 1, 1, 1, 1, 0, 0, 0, 0,
			0, 0, 0, 0, 0, 0, 2, 2, 2, 2,
			2, 2, 0, 0, 0, 0, 0, 0, 0, 0,
			0, 0, 3, 3, 3, 3, 3, 3, 0, 0,
			0, 0, 0, 0, 0, 0, 0, 0
		};
		byte[,] array2 = new byte[3, 1024];
		if (leds < 24 || leds > 720)
		{
			leds = 24;
		}
		byte[,,] array3 = new byte[4096, 3, leds];
		if (direction > 1)
		{
			direction = 0;
		}
		int num = 48;
		int num2 = 1;
		int num3 = num;
		while (num3 < leds)
		{
			num3 += num;
			num2++;
		}
		int num4 = 0;
		for (int i = 0; i < num; i++)
		{
			int num5 = i;
			for (num3 = 0; num3 < num; num3++)
			{
				int num6 = num3;
				if (direction == 0)
				{
					num6 = num - num3 - 1;
				}
				array2[0, num6] = 0;
				array2[1, num6] = 0;
				array2[2, num6] = 0;
				if (array[num5] != 0)
				{
					array2[0, num6] = (byte)(UiUserColour[array[num5] - 1, 0] * bright >> 8);
					array2[1, num6] = (byte)(UiUserColour[array[num5] - 1, 1] * bright >> 8);
					array2[2, num6] = (byte)(UiUserColour[array[num5] - 1, 2] * bright >> 8);
				}
				num5++;
				if (num5 >= num)
				{
					num5 -= num;
				}
			}
			int num7 = num4;
			for (num5 = 0; num5 < num2; num5++)
			{
				for (num3 = 0; num3 < num; num3++)
				{
					int num6 = num5 * num + num3;
					if (num6 >= leds)
					{
						break;
					}
					array3[num7, 0, num6] = array2[0, num3];
					array3[num7, 1, num6] = array2[1, num3];
					array3[num7, 2, num6] = array2[2, num3];
				}
			}
			num4++;
		}
		return (num4, array3);
	}

	private static (int Frames, byte[,,] Data) Mode10(int leds, int bright, int direction, byte[,] UiUserColour)
	{
		byte[,] array = new byte[3, 1024];
		if (leds < 24 || leds > 720)
		{
			leds = 24;
		}
		byte[,,] array2 = new byte[4096, 3, leds];
		if (direction > 1)
		{
			direction = 0;
		}
		int num = 0;
		for (int i = 0; i < 2; i++)
		{
			for (int j = 0; j < leds; j++)
			{
				for (int k = 0; k < j; k++)
				{
					array[0, k] = (byte)(UiUserColour[i, 0] * bright >> 8);
					array[1, k] = (byte)(UiUserColour[i, 1] * bright >> 8);
					array[2, k] = (byte)(UiUserColour[i, 2] * bright >> 8);
				}
				for (int k = j; k < leds; k++)
				{
					int num2 = 0;
					if (i == 0)
					{
						num2 = 1;
					}
					array[0, k] = (byte)(UiUserColour[num2, 0] * bright >> 8);
					array[1, k] = (byte)(UiUserColour[num2, 1] * bright >> 8);
					array[2, k] = (byte)(UiUserColour[num2, 2] * bright >> 8);
				}
				int num3 = num;
				for (int k = 0; k < leds; k++)
				{
					int num4 = k;
					if (direction == 1)
					{
						num4 = leds - k - 1;
					}
					array2[num3, 0, k] = array[0, num4];
					array2[num3, 1, k] = array[1, num4];
					array2[num3, 2, k] = array[2, num4];
				}
				num++;
			}
		}
		return (num, array2);
	}

	private static (int Frames, byte[,,] Data) Mode11(int leds, int bright, int direction, byte[,] UiUserColour)
	{
		byte[] array = new byte[24]
		{
			0, 0, 4, 8, 16, 32, 64, 96, 128, 160,
			192, 255, 255, 192, 160, 128, 96, 64, 32, 16,
			8, 4, 0, 0
		};
		byte[,] array2 = new byte[3, 1024];
		if (leds < 24 || leds > 720)
		{
			leds = 24;
		}
		byte[,,] array3 = new byte[4096, 3, leds];
		if (direction > 1)
		{
			direction = 0;
		}
		int num = 24;
		int num2 = 1;
		int num3 = num;
		while (num3 < leds)
		{
			num3 += num;
			num2++;
		}
		int num4 = 0;
		for (int i = 0; i < num; i++)
		{
			int num5 = i;
			for (num3 = 0; num3 < num; num3++)
			{
				int num6 = num3;
				if (direction == 0)
				{
					num6 = num - num3 - 1;
				}
				array2[0, num6] = (byte)(UiUserColour[0, 0] * array[num5] >> 8);
				array2[1, num6] = (byte)(UiUserColour[0, 1] * array[num5] >> 8);
				array2[2, num6] = (byte)(UiUserColour[0, 2] * array[num5] >> 8);
				num5++;
				if (num5 >= num)
				{
					num5 -= num;
				}
			}
			int num7 = num4;
			for (num5 = 0; num5 < num2; num5++)
			{
				for (num3 = 0; num3 < num; num3++)
				{
					int num6 = num5 * num + num3;
					if (num6 >= leds)
					{
						break;
					}
					array3[num7, 0, num6] = (byte)(array2[0, num3] * bright >> 8);
					array3[num7, 1, num6] = (byte)(array2[1, num3] * bright >> 8);
					array3[num7, 2, num6] = (byte)(array2[2, num3] * bright >> 8);
				}
			}
			num4++;
		}
		return (num4, array3);
	}

	private static (int Frames, byte[,,] Data) Mode12(int leds, int bright, int direction, byte[,] UiUserColour)
	{
		byte[] array = new byte[48]
		{
			255, 128, 64, 32, 16, 8, 0, 0, 0, 0,
			255, 64, 32, 16, 0, 0, 0, 0, 0, 255,
			192, 168, 128, 96, 64, 32, 16, 8, 0, 0,
			0, 0, 0, 0, 0, 255, 128, 64, 32, 16,
			8, 0, 0, 0, 0, 0, 0, 0
		};
		byte[] array2 = new byte[48]
		{
			0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			1, 1, 1, 1, 1, 1, 1, 1, 1, 2,
			2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
			2, 2, 2, 2, 2, 3, 3, 3, 3, 3,
			3, 3, 3, 3, 3, 3, 3, 3
		};
		byte[,] array3 = new byte[3, 1024];
		if (leds < 24 || leds > 720)
		{
			leds = 24;
		}
		byte[,,] array4 = new byte[4096, 3, leds];
		if (direction > 1)
		{
			direction = 0;
		}
		int num = 48;
		int num2 = 1;
		int num3 = num;
		while (num3 < leds)
		{
			num3 += num;
			num2++;
		}
		int num4 = 0;
		for (int i = 0; i < num; i++)
		{
			int num5 = i;
			for (num3 = 0; num3 < num; num3++)
			{
				int num6 = num3;
				if (direction == 0)
				{
					num6 = num - num3 - 1;
				}
				array3[0, num6] = (byte)(UiUserColour[array2[num5], 0] * array[num5] >> 8);
				array3[1, num6] = (byte)(UiUserColour[array2[num5], 1] * array[num5] >> 8);
				array3[2, num6] = (byte)(UiUserColour[array2[num5], 2] * array[num5] >> 8);
				num5++;
				if (num5 >= num)
				{
					num5 -= num;
				}
			}
			int num7 = num4;
			for (num5 = 0; num5 < num2; num5++)
			{
				for (num3 = 0; num3 < num; num3++)
				{
					int num6 = num5 * num + num3;
					if (num6 >= leds)
					{
						break;
					}
					array4[num7, 0, num6] = (byte)(array3[0, num3] * bright >> 8);
					array4[num7, 1, num6] = (byte)(array3[1, num3] * bright >> 8);
					array4[num7, 2, num6] = (byte)(array3[2, num3] * bright >> 8);
				}
			}
			num4++;
		}
		return (num4, array4);
	}

	private static (int Frames, byte[,,] Data) Mode16(int leds, int bright, int direction, byte[,] UiUserColour)
	{
		byte[,,] array = new byte[4096, 3, leds + 12];
		int num = 0;
		byte[] array2 = new byte[12]
		{
			0, 0, 0, 1, 1, 1, 2, 2, 2, 3,
			3, 3
		};
		try
		{
			if (leds < 24 || leds > 720)
			{
				leds = 24;
			}
			if (direction > 1)
			{
				direction = 0;
			}
			num = 0;
			for (int i = 0; i < 12; i++)
			{
				int num2 = num;
				int num3 = i;
				for (int j = 0; j < 12; j++)
				{
					int num4 = j;
					if (direction == 0)
					{
						num4 = 12 - j - 1;
					}
					array[num2, 0, num4] = (byte)(UiUserColour[array2[num3], 0] * bright >> 8);
					array[num2, 1, num4] = (byte)(UiUserColour[array2[num3], 1] * bright >> 8);
					array[num2, 2, num4] = (byte)(UiUserColour[array2[num3], 2] * bright >> 8);
					num3++;
					if (num3 >= 12)
					{
						num3 -= 12;
					}
				}
				int num5 = leds / 12 - 1;
				if (leds % 12 != 0)
				{
					num5++;
				}
				for (num3 = 0; num3 < num5; num3++)
				{
					for (int j = 0; j < 12; j++)
					{
						array[num2, 0, (num3 + 1) * 12 + j] = array[num2, 0, j];
						array[num2, 1, (num3 + 1) * 12 + j] = array[num2, 1, j];
						array[num2, 2, (num3 + 1) * 12 + j] = array[num2, 2, j];
					}
				}
				num++;
			}
		}
		catch (System.Exception)
		{
		}
		return (num, array);
	}

	private static (int Frames, byte[,,] Data) Mode21(int leds, int bright, int direction, byte[,] UiUserColour)
	{
		if (leds < 24 || leds > 720)
		{
			leds = 24;
		}
		if (direction > 1)
		{
			direction = 0;
		}
		int num = leds / 2;
		if ((leds & 1) == 1)
		{
			num++;
		}
		byte[,,] array = new byte[4096, 3, leds + 8];
		int num2 = 0;
		for (int i = 0; i < 2; i++)
		{
			int num3;
			for (num3 = 0; num3 < num; num3++)
			{
				int num4 = num2;
				for (int j = 0; j < num; j++)
				{
					byte b = 0;
					byte b2 = 0;
					byte b3 = 0;
					if (j <= num3)
					{
						b = UiUserColour[i, 0];
						b2 = UiUserColour[i, 1];
						b3 = UiUserColour[i, 2];
					}
					array[num4, 0, num - j - 1] = (byte)(b * bright >> 8);
					array[num4, 1, num - j - 1] = (byte)(b2 * bright >> 8);
					array[num4, 2, num - j - 1] = (byte)(b3 * bright >> 8);
					array[num4, 0, j + num] = array[num4, 0, num - j - 1];
					array[num4, 1, j + num] = array[num4, 1, num - j - 1];
					array[num4, 2, j + num] = array[num4, 2, num - j - 1];
				}
				num2++;
				num3++;
			}
			for (num3 = 0; num3 < 35; num3++)
			{
				int num5 = ((num3 < 25) ? ((25 - num3 - 1) * 10) : 0);
				byte b = (byte)(UiUserColour[i, 0] * num5 >> 8);
				byte b2 = (byte)(UiUserColour[i, 1] * num5 >> 8);
				byte b3 = (byte)(UiUserColour[i, 2] * num5 >> 8);
				int num4 = num2;
				for (int j = 0; j < leds + 1; j++)
				{
					array[num4, 0, j] = (byte)(b * bright >> 8);
					array[num4, 1, j] = (byte)(b2 * bright >> 8);
					array[num4, 2, j] = (byte)(b3 * bright >> 8);
				}
				num2++;
			}
		}
		return (num2, array);
	}

	private static (int Frames, byte[,,] Data) Mode22(int leds, int bright, int direction, byte[,] UiUserColour)
	{
		byte[] array = new byte[32]
		{
			0, 1, 0, 2, 0, 1, 0, 2, 0, 1,
			0, 2, 0, 1, 0, 1, 0, 1, 0, 1,
			0, 1, 0, 2, 0, 2, 0, 2, 0, 2,
			0, 2
		};
		byte[] array2 = new byte[32]
		{
			8, 8, 8, 8, 8, 8, 8, 8, 8, 8,
			8, 8, 8, 2, 2, 2, 2, 2, 2, 2,
			2, 2, 8, 2, 2, 2, 2, 2, 2, 2,
			2, 2
		};
		if (leds < 24 || leds > 720)
		{
			leds = 24;
		}
		if (direction > 1)
		{
			direction = 0;
		}
		byte[,,] array3 = new byte[4096, 3, leds + 8];
		int num = 0;
		for (int i = 0; i < 32; i++)
		{
			for (int j = 0; j < array2[i]; j++)
			{
				int num2 = num;
				for (int k = 0; k < leds; k++)
				{
					byte b = 0;
					byte b2 = 0;
					byte b3 = 0;
					if (array[i] != 0)
					{
						b = UiUserColour[array[i] - 1, 0];
						b2 = UiUserColour[array[i] - 1, 1];
						b3 = UiUserColour[array[i] - 1, 2];
					}
					array3[num2, 0, k] = (byte)(b * bright >> 8);
					array3[num2, 1, k] = (byte)(b2 * bright >> 8);
					array3[num2, 2, k] = (byte)(b3 * bright >> 8);
				}
				num++;
			}
		}
		return (num, array3);
	}

	private static (int Frames, byte[,,] Data) Mode26(int leds, int bright, int direction, byte[,] UiUserColour)
	{
		byte[] array = new byte[27]
		{
			0, 0, 45, 60, 0, 0, 75, 90, 0, 0,
			105, 120, 0, 0, 135, 150, 0, 0, 165, 180,
			195, 0, 0, 210, 225, 240, 255
		};
		byte[] array2 = new byte[38]
		{
			0, 0, 45, 45, 0, 0, 55, 55, 0, 0,
			65, 65, 0, 0, 75, 85, 95, 0, 0, 105,
			115, 125, 135, 0, 0, 145, 155, 165, 175, 185,
			0, 0, 195, 215, 225, 235, 245, 255
		};
		byte[,,] array3 = new byte[3, 3, 1024];
		if (leds < 24 || leds > 720)
		{
			leds = 24;
		}
		if (direction > 1)
		{
			direction = 0;
		}
		int num = 27;
		if (leds > 48)
		{
			num = 38;
		}
		int num2 = 0;
		for (int i = 0; i < leds + num; i++)
		{
			array3[0, 0, i] = (byte)(UiUserColour[0, 0] * 64 >> 8);
			array3[0, 1, i] = (byte)(UiUserColour[0, 1] * 64 >> 8);
			array3[0, 2, i] = (byte)(UiUserColour[0, 2] * 64 >> 8);
			if (i < num && num == 27)
			{
				array3[0, 0, i] = 0;
				array3[0, 1, i] = 0;
				array3[0, 2, i] = 0;
				if (array[i] != 0)
				{
					array3[0, 0, i] = (byte)(UiUserColour[1, 0] * array[i] >> 8);
					array3[0, 1, i] = (byte)(UiUserColour[1, 1] * array[i] >> 8);
					array3[0, 2, i] = (byte)(UiUserColour[1, 2] * array[i] >> 8);
				}
			}
			if (i < num && num == 38 && array2[i] != 0)
			{
				array3[0, 0, i] = 0;
				array3[0, 1, i] = 0;
				array3[0, 2, i] = 0;
				if (array2[i] != 0)
				{
					array3[0, 0, i] = (byte)(UiUserColour[1, 0] * array2[i] >> 8);
					array3[0, 1, i] = (byte)(UiUserColour[1, 1] * array2[i] >> 8);
					array3[0, 2, i] = (byte)(UiUserColour[1, 2] * array2[i] >> 8);
				}
			}
		}
		byte[,,] array4 = new byte[leds + num, 3, leds];
		int num3 = num;
		for (int j = 0; j < leds + num; j++)
		{
			for (int i = 0; i < leds; i++)
			{
				int num4 = num3 + i;
				if (num4 >= leds + num)
				{
					num4 = num4 - leds - num;
				}
				array3[1, 0, i] = array3[0, 0, num4];
				array3[1, 1, i] = array3[0, 1, num4];
				array3[1, 2, i] = array3[0, 2, num4];
			}
			num3 = ((num3 != 0) ? (num3 - 1) : (leds + num - 1));
			for (int i = 0; i < leds; i++)
			{
				int num5 = leds - i - 1;
				array3[2, 0, num5] = array3[1, 0, i];
				array3[2, 1, num5] = array3[1, 1, i];
				array3[2, 2, num5] = array3[1, 2, i];
			}
			int num6 = num2;
			for (int i = 0; i < leds; i++)
			{
				int num7 = array3[1, 0, i] + array3[2, 0, i];
				if (num7 > 255)
				{
					num7 = 255;
				}
				int num8 = array3[1, 1, i] + array3[2, 1, i];
				if (num8 > 255)
				{
					num8 = 255;
				}
				int num9 = array3[1, 2, i] + array3[2, 2, i];
				if (num9 > 255)
				{
					num9 = 255;
				}
				array4[num6, 0, i] = (byte)(num7 * bright >> 8);
				array4[num6, 1, i] = (byte)(num8 * bright >> 8);
				array4[num6, 2, i] = (byte)(num9 * bright >> 8);
			}
			num2++;
		}
		return (num2, array4);
	}

	private static (int Frames, byte[,,] Data) Mode27(int leds, int bright, int direction, byte[,] UiUserColour)
	{
		int num = 400;
		byte[,,] array = new byte[num, 3, 720];
		byte[] array2 = new byte[1000];
		int[] array3 = new int[10] { 51, 102, 153, 204, 255, 204, 153, 102, 51, 0 };
		int[] array4 = new int[10] { 0, 5, 10, 3, 6, 12, 5, 10, 6, 3 };
		int[] array5 = new int[60]
		{
			3, 20, 32, 56, 80, 90, 113, 132, 147, 158,
			177, 190, 208, 215, 232, 257, 275, 294, 305, 321,
			342, 357, 378, 395, 409, 429, 445, 468, 481, 496,
			512, 527, 545, 566, 581, 590, 606, 625, 642, 658,
			676, 692, 716, 732, 750, 766, 782, 794, 815, 829,
			850, 866, 883, 896, 910, 924, 942, 957, 969, 982
		};
		if (leds < 24 || leds > 720)
		{
			leds = 720;
		}
		if (direction > 1)
		{
			direction = 0;
		}
		int num2 = 0;
		for (int i = 0; i < 10; i++)
		{
			for (int j = 0; j < 1000; j++)
			{
				array2[j] = 0;
			}
			for (int k = 0; k < 60; k++)
			{
				if (array5[k] + array4[i] < 1000)
				{
					array2[array5[k] + array4[i]] = 1;
				}
			}
			for (int l = 0; l < 40; l++)
			{
				for (int j = 0; j < leds; j++)
				{
					int num3 = UiUserColour[0, 0] * 64 >> 8;
					int num4 = UiUserColour[0, 1] * 64 >> 8;
					int num5 = UiUserColour[0, 2] * 64 >> 8;
					if (l / 10 == 1 || l / 10 == 2)
					{
						if (array2[j] == 1)
						{
							num3 = UiUserColour[1, 0] * array3[l % 10] >> 8;
							num4 = UiUserColour[1, 1] * array3[l % 10] >> 8;
							num5 = UiUserColour[1, 2] * array3[l % 10] >> 8;
						}
						if (j > 0 && j < leds - 1 && l % 10 < 9 && (array2[j + 1] == 1 || array2[j - 1] == 1))
						{
							int num6 = array3[l % 10] - 51;
							if (num6 < 0)
							{
								num6 = 0;
							}
							num3 = UiUserColour[1, 0] * num6 >> 8;
							num4 = UiUserColour[1, 1] * num6 >> 8;
							num5 = UiUserColour[1, 2] * num6 >> 8;
						}
						if (j > 1 && j < leds - 2 && l % 10 > 0 && l % 10 < 8 && (array2[j + 2] == 1 || array2[j - 2] == 1))
						{
							int num7 = array3[l % 10] - 102;
							if (num7 < 0)
							{
								num7 = 0;
							}
							num3 = UiUserColour[1, 0] * num7 >> 8;
							num4 = UiUserColour[1, 1] * num7 >> 8;
							num5 = UiUserColour[1, 2] * num7 >> 8;
						}
					}
					int num8 = num2;
					array[num8, 0, j] = (byte)(num3 * bright >> 8);
					array[num8, 1, j] = (byte)(num4 * bright >> 8);
					array[num8, 2, j] = (byte)(num5 * bright >> 8);
				}
				num2++;
			}
		}
		byte[,,] array6 = new byte[num2, 3, leds];
		for (int m = 0; m < num2; m++)
		{
			for (int n = 0; n < 3; n++)
			{
				for (int num9 = 0; num9 < leds; num9++)
				{
					array6[m, n, num9] = array[m, n, num9];
				}
			}
		}
		return (num2, array6);
	}

	private static (int Frames, byte[,,] Data) Mode29(int leds, int bright, int direction, byte[,] UiUserColour)
	{
		int num = 30;
		byte[,,] array = new byte[num, 3, 720];
		byte[,] array2 = new byte[3, 20];
		int[] array3 = new int[30]
		{
			17, 34, 51, 68, 85, 102, 119, 136, 153, 170,
			187, 204, 221, 238, 255, 238, 221, 204, 187, 170,
			153, 136, 119, 102, 85, 68, 51, 34, 17, 0
		};
		if (leds < 24 || leds > 720)
		{
			leds = 720;
		}
		if (direction > 1)
		{
			direction = 0;
		}
		int num2 = 11;
		int num3 = leds / num2 + 1;
		int num4 = 0;
		for (int i = 0; i < 30; i++)
		{
			for (int j = 0; j < num2; j++)
			{
				int num5 = UiUserColour[0, 0] * 64 >> 8;
				int num6 = UiUserColour[0, 1] * 64 >> 8;
				int num7 = UiUserColour[0, 2] * 64 >> 8;
				if (j == 5)
				{
					num5 = UiUserColour[1, 0] * array3[i] >> 8;
					num6 = UiUserColour[1, 1] * array3[i] >> 8;
					num7 = UiUserColour[1, 2] * array3[i] >> 8;
				}
				if ((j == 4 || j == num2 - 4 - 1) && array3[i] > 34)
				{
					int num8 = array3[i] - 34;
					num5 = UiUserColour[1, 0] * num8 >> 8;
					num6 = UiUserColour[1, 1] * num8 >> 8;
					num7 = UiUserColour[1, 2] * num8 >> 8;
				}
				if ((j == 3 || j == num2 - 3 - 1) && array3[i] > 68)
				{
					int num9 = array3[i] - 68;
					num5 = UiUserColour[1, 0] * num9 >> 8;
					num6 = UiUserColour[1, 1] * num9 >> 8;
					num7 = UiUserColour[1, 2] * num9 >> 8;
				}
				if ((j == 2 || j == num2 - 2 - 1) && array3[i] > 102)
				{
					int num10 = array3[i] - 102;
					num5 = UiUserColour[1, 0] * num10 >> 8;
					num6 = UiUserColour[1, 1] * num10 >> 8;
					num7 = UiUserColour[1, 2] * num10 >> 8;
				}
				if ((j == 1 || j == num2 - 1 - 1) && array3[i] > 170)
				{
					int num11 = array3[i] - 170;
					num5 = UiUserColour[1, 0] * num11 >> 8;
					num6 = UiUserColour[1, 1] * num11 >> 8;
					num7 = UiUserColour[1, 2] * num11 >> 8;
				}
				if ((j == 0 || j == num2 - 1) && array3[i] > 204)
				{
					int num12 = array3[i] - 204;
					num5 = UiUserColour[1, 0] * num12 >> 8;
					num6 = UiUserColour[1, 1] * num12 >> 8;
					num7 = UiUserColour[1, 2] * num12 >> 8;
				}
				array2[0, j] = (byte)num5;
				array2[1, j] = (byte)num6;
				array2[2, j] = (byte)num7;
			}
			int num13 = num4;
			for (int k = 0; k < num3; k++)
			{
				for (int j = 0; j < num2; j++)
				{
					int num14 = k * num2 + j;
					if (num14 < leds)
					{
						array[num13, 0, num14] = (byte)(array2[0, j] * bright >> 8);
						array[num13, 1, num14] = (byte)(array2[1, j] * bright >> 8);
						array[num13, 2, num14] = (byte)(array2[2, j] * bright >> 8);
					}
				}
			}
			num4++;
		}
		byte[,,] array4 = new byte[num4, 3, leds];
		for (int l = 0; l < num4; l++)
		{
			for (int m = 0; m < 3; m++)
			{
				for (int n = 0; n < leds; n++)
				{
					array4[l, m, n] = array[l, m, n];
				}
			}
		}
		return (num4, array4);
	}

	private static (int Frames, byte[,,] Data) Mode30(int leds, int bright, int direction, byte[,] UiUserColour)
	{
		int num = 0;
		byte[,] array = new byte[3, 48];
		byte[,] array2 = new byte[3, 1000];
		int[] array3 = new int[5] { 255, 168, 128, 64, 32 };
		int[] array4 = new int[6] { 4, 20, 36, 12, 44, 28 };
		byte[,] array5 = new byte[34, 48]
		{
			{
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0
			},
			{
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 0, 1, 1, 0, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0
			},
			{
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 1, 1, 1, 1, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0
			},
			{
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 1, 1, 0, 0, 1, 1, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0
			},
			{
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				1, 1, 0, 0, 0, 0, 1, 1, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0
			},
			{
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0, 0, 2,
				2, 0, 0, 1, 1, 0, 0, 2, 2, 0,
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0
			},
			{
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0, 2, 2,
				0, 0, 1, 1, 1, 1, 0, 0, 2, 2,
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0
			},
			{
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 2, 2, 0,
				0, 1, 1, 0, 0, 1, 1, 0, 0, 2,
				2, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0
			},
			{
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 2, 2, 0, 0,
				1, 1, 0, 0, 0, 0, 1, 1, 0, 0,
				2, 2, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0
			},
			{
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 3, 3, 0, 0, 2,
				2, 0, 0, 1, 1, 0, 0, 2, 2, 0,
				0, 3, 3, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0
			},
			{
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 0, 0, 3, 3, 0, 0, 2, 2,
				0, 0, 1, 1, 1, 1, 0, 0, 2, 2,
				0, 0, 3, 3, 0, 0, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0
			},
			{
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 0, 3, 3, 0, 0, 2, 2, 0,
				0, 1, 1, 0, 0, 1, 1, 0, 0, 2,
				2, 0, 0, 3, 3, 0, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0
			},
			{
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 3, 3, 0, 0, 2, 2, 0, 0,
				1, 1, 0, 0, 0, 0, 1, 1, 0, 0,
				2, 2, 0, 0, 3, 3, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0
			},
			{
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 4, 4, 0, 0, 3, 3, 0, 0, 2,
				2, 0, 0, 1, 1, 0, 0, 2, 2, 0,
				0, 3, 3, 0, 0, 4, 4, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0
			},
			{
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				4, 4, 0, 0, 3, 3, 0, 0, 2, 2,
				0, 0, 1, 1, 1, 1, 0, 0, 2, 2,
				0, 0, 3, 3, 0, 0, 4, 4, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0
			},
			{
				0, 0, 0, 0, 0, 0, 0, 0, 0, 4,
				4, 0, 0, 3, 3, 0, 0, 2, 2, 0,
				0, 1, 1, 0, 0, 1, 1, 0, 0, 2,
				2, 0, 0, 3, 3, 0, 0, 4, 4, 0,
				0, 0, 0, 0, 0, 0, 0, 0
			},
			{
				0, 0, 0, 0, 0, 0, 0, 0, 4, 4,
				0, 0, 3, 3, 0, 0, 2, 2, 0, 0,
				1, 1, 0, 0, 0, 0, 1, 1, 0, 0,
				2, 2, 0, 0, 3, 3, 0, 0, 4, 4,
				0, 0, 0, 0, 0, 0, 0, 0
			},
			{
				0, 0, 0, 0, 0, 0, 0, 5, 5, 0,
				0, 4, 4, 0, 0, 3, 3, 0, 0, 2,
				2, 0, 0, 0, 0, 0, 0, 2, 2, 0,
				0, 3, 3, 0, 0, 4, 4, 0, 0, 5,
				5, 0, 0, 0, 0, 0, 0, 0
			},
			{
				0, 0, 0, 0, 0, 0, 5, 5, 0, 0,
				4, 4, 0, 0, 3, 3, 0, 0, 2, 2,
				0, 0, 0, 0, 0, 0, 0, 0, 2, 2,
				0, 0, 3, 3, 0, 0, 4, 4, 0, 0,
				5, 5, 0, 0, 0, 0, 0, 0
			},
			{
				0, 0, 0, 0, 0, 5, 5, 0, 0, 4,
				4, 0, 0, 3, 3, 0, 0, 2, 2, 0,
				0, 0, 0, 0, 0, 0, 0, 0, 0, 2,
				2, 0, 0, 3, 3, 0, 0, 4, 4, 0,
				0, 5, 5, 0, 0, 0, 0, 0
			},
			{
				0, 0, 0, 0, 5, 5, 0, 0, 4, 4,
				0, 0, 3, 3, 0, 0, 2, 2, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				2, 2, 0, 0, 3, 3, 0, 0, 4, 4,
				0, 0, 5, 5, 0, 0, 0, 0
			},
			{
				0, 0, 0, 0, 0, 0, 0, 5, 5, 0,
				0, 4, 4, 0, 0, 3, 3, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 3, 3, 0, 0, 4, 4, 0, 0, 5,
				5, 0, 0, 0, 0, 0, 0, 0
			},
			{
				0, 0, 0, 0, 0, 0, 5, 5, 0, 0,
				4, 4, 0, 0, 3, 3, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 3, 3, 0, 0, 4, 4, 0, 0,
				5, 5, 0, 0, 0, 0, 0, 0
			},
			{
				0, 0, 0, 0, 0, 5, 5, 0, 0, 4,
				4, 0, 0, 3, 3, 0, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 0, 3, 3, 0, 0, 4, 4, 0,
				0, 5, 5, 0, 0, 0, 0, 0
			},
			{
				0, 0, 0, 0, 5, 5, 0, 0, 4, 4,
				0, 0, 3, 3, 0, 0, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 0, 0, 3, 3, 0, 0, 4, 4,
				0, 0, 5, 5, 0, 0, 0, 0
			},
			{
				0, 0, 0, 0, 0, 0, 0, 5, 5, 0,
				0, 4, 4, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 4, 4, 0, 0, 5,
				5, 0, 0, 0, 0, 0, 0, 0
			},
			{
				0, 0, 0, 0, 0, 0, 5, 5, 0, 0,
				4, 4, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 4, 4, 0, 0,
				5, 5, 0, 0, 0, 0, 0, 0
			},
			{
				0, 0, 0, 0, 0, 5, 5, 0, 0, 4,
				4, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 4, 4, 0,
				0, 5, 5, 0, 0, 0, 0, 0
			},
			{
				0, 0, 0, 0, 5, 5, 0, 0, 4, 4,
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0, 4, 4,
				0, 0, 5, 5, 0, 0, 0, 0
			},
			{
				0, 0, 0, 0, 0, 0, 0, 5, 5, 0,
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0, 0, 5,
				5, 0, 0, 0, 0, 0, 0, 0
			},
			{
				0, 0, 0, 0, 0, 0, 5, 5, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				5, 5, 0, 0, 0, 0, 0, 0
			},
			{
				0, 0, 0, 0, 0, 5, 5, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 5, 5, 0, 0, 0, 0, 0
			},
			{
				0, 0, 0, 0, 5, 5, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 5, 5, 0, 0, 0, 0
			},
			{
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0
			}
		};
		byte[,,] array6 = new byte[300, 3, 720];
		if (leds < 24 || leds > 720)
		{
			leds = 24;
		}
		if (direction > 1)
		{
			direction = 0;
		}
		int num2 = 48;
		int num3 = leds / num2 + 1;
		try
		{
			for (int i = 0; i < 1000; i++)
			{
				array2[0, i] = (byte)(UiUserColour[0, 0] * 128 >> 8);
				array2[1, i] = (byte)(UiUserColour[0, 1] * 128 >> 8);
				array2[2, i] = (byte)(UiUserColour[0, 2] * 128 >> 8);
			}
			num = 0;
			for (int j = 0; j < 6; j++)
			{
				int num4 = 0;
				for (int k = 0; k < 34; k++)
				{
					if (num4 < 16)
					{
						num4++;
						k = 0;
					}
					for (int i = 0; i < num2; i++)
					{
						array[0, i] = (byte)(UiUserColour[0, 0] * 128 >> 8);
						array[1, i] = (byte)(UiUserColour[0, 1] * 128 >> 8);
						array[2, i] = (byte)(UiUserColour[0, 2] * 128 >> 8);
						if (array5[k, i] != 0)
						{
							array[0, i] = (byte)(UiUserColour[1, 0] * array3[array5[k, i] - 1] >> 8);
							array[1, i] = (byte)(UiUserColour[1, 1] * array3[array5[k, i] - 1] >> 8);
							array[2, i] = (byte)(UiUserColour[1, 2] * array3[array5[k, i] - 1] >> 8);
						}
					}
					for (int l = 0; l < num3; l++)
					{
						for (int i = 0; i < num2; i++)
						{
							array2[0, l * num2 + array4[j] + i] = array[0, i];
							array2[1, l * num2 + array4[j] + i] = array[1, i];
							array2[2, l * num2 + array4[j] + i] = array[2, i];
						}
					}
					int num5 = num;
					for (int i = 0; i < leds; i++)
					{
						array6[num5, 0, i] = (byte)(array2[0, i + num2] * bright >> 8);
						array6[num5, 1, i] = (byte)(array2[1, i + num2] * bright >> 8);
						array6[num5, 2, i] = (byte)(array2[2, i + num2] * bright >> 8);
					}
					num++;
				}
			}
		}
		catch (System.Exception)
		{
		}
		return (num, array6);
	}

#pragma warning restore IDE0059, IDE0018, IDE0045, IDE0046, IDE0047, IDE0048, IDE0054, IDE0074
}
