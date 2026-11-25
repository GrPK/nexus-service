using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.LogitechHidpp;

/// <summary>What a button slot in an onboard profile sends.</summary>
public enum HidppButtonKind
{
    /// <summary>Mouse buttons, as a bit mask (bit 0 left, 1 right, 2 middle, 3 back, 4 forward).</summary>
    Mouse,
    /// <summary>A keyboard key: HID usage plus a modifier mask.</summary>
    Key,
    /// <summary>A consumer control (media, volume): HID consumer usage.</summary>
    Consumer,
    /// <summary>A built-in function of the mouse (DPI, profile, tilt, G-Shift, ...).</summary>
    Special,
    /// <summary>Anything else (macros, a disabled slot): kept byte for byte.</summary>
    Raw,
}

/// <summary>
/// One 4-byte button binding of an onboard profile: 80 01 mask (mouse buttons), 80 02 mod key
/// (keyboard), 80 03 usage (consumer), 90 fn (special function). Other encodings, macros among
/// them, are carried as raw bytes so a round trip never changes them.
/// </summary>
public sealed record HidppButtonBinding(HidppButtonKind Kind, int Code, int Modifiers, byte[]? RawBytes = null)
{
    public const int MouseLeft = 0x0001;
    public const int MouseRight = 0x0002;
    public const int MouseMiddle = 0x0004;
    public const int MouseBack = 0x0008;
    public const int MouseForward = 0x0010;

    /// <summary>Special function codes as the firmware numbers them (Solaar's names).</summary>
    public static readonly IReadOnlyDictionary<int, string> SpecialNames = new Dictionary<int, string>
    {
        [0] = "none",
        [1] = "tiltLeft",
        [2] = "tiltRight",
        [3] = "dpiUp",
        [4] = "dpiDown",
        [5] = "dpiCycle",
        [6] = "dpiDefault",
        [7] = "dpiShift",
        [8] = "profileNext",
        [9] = "profilePrevious",
        [10] = "profileCycle",
        [11] = "gShift",
        [12] = "batteryStatus",
        [13] = "profileSelect",
        [14] = "modeSwitch",
        [15] = "hostButton",
        [16] = "scrollDown",
        [17] = "scrollUp",
    };

    public static HidppButtonBinding Mouse(int mask) => new(HidppButtonKind.Mouse, mask & 0xFFFF, 0);
    public static HidppButtonBinding Key(int usage, int modifiers = 0) => new(HidppButtonKind.Key, usage & 0xFF, modifiers & 0xFF);
    public static HidppButtonBinding Consumer(int usage) => new(HidppButtonKind.Consumer, usage & 0xFFFF, 0);
    public static HidppButtonBinding Special(int function) => new(HidppButtonKind.Special, function & 0xFF, 0);

    public static HidppButtonBinding Decode(ReadOnlySpan<byte> b)
    {
        if (b.Length < 4)
        {
            throw new ArgumentException("a button binding is 4 bytes", nameof(b));
        }
        if (b[0] == 0x80)
        {
            switch (b[1])
            {
                case 0x01: return Mouse(b[2] << 8 | b[3]);
                case 0x02: return Key(b[3], b[2]);
                case 0x03: return Consumer(b[2] << 8 | b[3]);
            }
        }
        if (b[0] == 0x90 && b[2] == 0 && b[3] == 0)
        {
            return Special(b[1]);
        }
        return new HidppButtonBinding(HidppButtonKind.Raw, 0, 0, b[..4].ToArray());
    }

    public byte[] Encode() => Kind switch
    {
        HidppButtonKind.Mouse => new byte[] { 0x80, 0x01, (byte)(Code >> 8), (byte)Code },
        HidppButtonKind.Key => new byte[] { 0x80, 0x02, (byte)Modifiers, (byte)Code },
        HidppButtonKind.Consumer => new byte[] { 0x80, 0x03, (byte)(Code >> 8), (byte)Code },
        HidppButtonKind.Special => new byte[] { 0x90, (byte)Code, 0x00, 0x00 },
        _ => RawBytes is { Length: 4 } raw ? (byte[])raw.Clone() : throw new InvalidOperationException("raw binding without its 4 bytes"),
    };

    // Records compare arrays by reference; compare what goes to the mouse instead.
    public bool Equals(HidppButtonBinding? other) => other is not null && Encode().AsSpan().SequenceEqual(other.Encode());

    public override int GetHashCode() => BitConverter.ToInt32(Encode());
}
