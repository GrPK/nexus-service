using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.LogitechHidpp;

/// <summary>What one step of an onboard macro does.</summary>
public enum HidppMacroStepKind
{
    /// <summary>A keyboard key goes down: Code = HID usage (0xE0..0xE7 are the modifier keys), Modifiers = extra modifier mask.</summary>
    KeyDown,
    KeyUp,
    /// <summary>A pause: Code = milliseconds (1..65535).</summary>
    Delay,
    /// <summary>Mouse buttons go down: Code = button mask (bit 0 left, 1 right, 2 middle, 3 back, 4 forward).</summary>
    MouseDown,
    MouseUp,
    /// <summary>A consumer control (media, volume) goes down: Code = HID consumer usage.</summary>
    ConsumerDown,
    ConsumerUp,
    /// <summary>Vertical wheel: Code = signed clicks (-127..127).</summary>
    Wheel,
    /// <summary>Horizontal wheel (tilt): Code = signed clicks (-127..127).</summary>
    HWheel,
}

/// <summary>One step of an onboard macro.</summary>
public sealed record HidppMacroStep(HidppMacroStepKind Kind, int Code, int Modifiers = 0);

/// <summary>
/// A macro the mouse plays by itself from its onboard memory (feature 0x8100, macro format 1),
/// pointed at by a button binding (type 0, sector, byte address). Each command's length follows
/// from its opcode range, as the onboard memory format lays them out: 0x00-0x1F one byte, 0x20-0x3F
/// two, 0x40-0x5F three, 0x60-0x7F five, 0xFF ends the macro. The opcodes used here are the ones
/// libratbag names: 0x20 wheel, 0x21 horizontal wheel, 0x40 delay (big-endian ms), 0x41/0x42 mouse
/// buttons down/up (big-endian mask), 0x43/0x44 key down/up (modifier mask, usage), 0x45/0x46
/// consumer down/up (big-endian usage). A modifier key on its own is a key command with its bit in
/// the mask and usage 0, the way recorded macros store it. Anything else (jumps, repeats, pointer
/// moves) is kept as bytes but not offered for editing.
/// </summary>
public sealed class HidppOnboardMacro
{
    public const byte OpWheel = 0x20;
    public const byte OpHWheel = 0x21;
    public const byte OpDelay = 0x40;
    public const byte OpMouseDown = 0x41;
    public const byte OpMouseUp = 0x42;
    public const byte OpKeyDown = 0x43;
    public const byte OpKeyUp = 0x44;
    public const byte OpConsumerDown = 0x45;
    public const byte OpConsumerUp = 0x46;
    public const byte OpEnd = 0xFF;

    /// <summary>Most steps a macro may have; keeps one macro well inside a sector.</summary>
    public const int MaxSteps = 80;

    private HidppOnboardMacro(IReadOnlyList<HidppMacroStep> steps, byte[] bytes, bool editable)
    {
        Steps = steps;
        Bytes = bytes;
        Editable = editable;
    }

    /// <summary>The steps; empty for a macro that uses commands this class does not edit.</summary>
    public IReadOnlyList<HidppMacroStep> Steps { get; }

    /// <summary>The encoded macro, end marker included.</summary>
    public byte[] Bytes { get; }

    /// <summary>False when the macro uses commands (jump, repeat, pointer move) that are kept but not edited.</summary>
    public bool Editable { get; }

    /// <summary>Encodes steps, rejecting anything the mouse could be left holding (a key, button or control pressed and never released).</summary>
    public static HidppOnboardMacro FromSteps(IReadOnlyList<HidppMacroStep> steps)
    {
        if (steps.Count == 0)
        {
            throw new ArgumentException("a macro needs at least one step");
        }
        if (steps.Count > MaxSteps)
        {
            throw new ArgumentException($"a macro holds at most {MaxSteps} steps");
        }
        var bytes = new List<byte>(steps.Count * 3 + 1);
        var held = new HashSet<(HidppMacroStepKind, int)>();
        foreach (var s in steps)
        {
            switch (s.Kind)
            {
                case HidppMacroStepKind.KeyDown:
                case HidppMacroStepKind.KeyUp:
                    var (mods, key) = KeyFields(s);
                    bytes.Add(s.Kind == HidppMacroStepKind.KeyDown ? OpKeyDown : OpKeyUp);
                    bytes.Add(mods);
                    bytes.Add(key);
                    Track(held, s.Kind == HidppMacroStepKind.KeyDown, HidppMacroStepKind.KeyDown, s.Code, "key");
                    break;
                case HidppMacroStepKind.Delay:
                    if (s.Code is < 1 or > 0xFFFF)
                    {
                        throw new ArgumentException($"a delay is 1 to 65535 ms, not {s.Code}");
                    }
                    bytes.Add(OpDelay);
                    bytes.Add((byte)(s.Code >> 8));
                    bytes.Add((byte)s.Code);
                    break;
                case HidppMacroStepKind.MouseDown:
                case HidppMacroStepKind.MouseUp:
                    if (s.Code is < 1 or > 0xFFFF)
                    {
                        throw new ArgumentException($"mouse button mask 0x{s.Code:X} out of range");
                    }
                    bytes.Add(s.Kind == HidppMacroStepKind.MouseDown ? OpMouseDown : OpMouseUp);
                    bytes.Add((byte)(s.Code >> 8));
                    bytes.Add((byte)s.Code);
                    Track(held, s.Kind == HidppMacroStepKind.MouseDown, HidppMacroStepKind.MouseDown, s.Code, "mouse button");
                    break;
                case HidppMacroStepKind.ConsumerDown:
                case HidppMacroStepKind.ConsumerUp:
                    if (s.Code is < 1 or > 0xFFFF)
                    {
                        throw new ArgumentException($"consumer usage 0x{s.Code:X} out of range");
                    }
                    bytes.Add(s.Kind == HidppMacroStepKind.ConsumerDown ? OpConsumerDown : OpConsumerUp);
                    bytes.Add((byte)(s.Code >> 8));
                    bytes.Add((byte)s.Code);
                    Track(held, s.Kind == HidppMacroStepKind.ConsumerDown, HidppMacroStepKind.ConsumerDown, s.Code, "media control");
                    break;
                case HidppMacroStepKind.Wheel:
                case HidppMacroStepKind.HWheel:
                    if (s.Code is < -127 or > 127 || s.Code == 0)
                    {
                        throw new ArgumentException($"a wheel step is -127 to 127 clicks, not {s.Code}");
                    }
                    bytes.Add(s.Kind == HidppMacroStepKind.Wheel ? OpWheel : OpHWheel);
                    bytes.Add((byte)(sbyte)s.Code);
                    break;
                default:
                    throw new ArgumentException($"unknown macro step {s.Kind}");
            }
        }
        if (held.Count > 0)
        {
            throw new ArgumentException("every key, button and media control the macro presses must also be released");
        }
        bytes.Add(OpEnd);
        return new HidppOnboardMacro(steps, bytes.ToArray(), editable: true);
    }

    /// <summary>
    /// Reads the macro that starts at <paramref name="address"/> of a sector. Null when no end
    /// marker is found inside the sector (before its CRC) or a command is not one of the format's.
    /// </summary>
    public static HidppOnboardMacro? Parse(ReadOnlySpan<byte> sector, int address)
    {
        var limit = sector.Length - 2;
        if (address < 0 || address >= limit)
        {
            return null;
        }
        var steps = new List<HidppMacroStep>();
        var editable = true;
        var at = address;
        while (at < limit)
        {
            var op = sector[at];
            if (op == OpEnd)
            {
                var bytes = sector.Slice(address, at - address + 1).ToArray();
                return new HidppOnboardMacro(editable ? steps : Array.Empty<HidppMacroStep>(), bytes, editable);
            }
            var length = Length(op);
            if (length == 0 || at + length > limit)
            {
                return null;
            }
            var a = length > 1 ? sector[at + 1] : 0;
            var b = length > 2 ? sector[at + 2] : 0;
            switch (op)
            {
                case OpKeyDown:
                case OpKeyUp:
                    steps.Add(KeyStep(op == OpKeyDown ? HidppMacroStepKind.KeyDown : HidppMacroStepKind.KeyUp, a, b));
                    break;
                case OpDelay:
                    steps.Add(new HidppMacroStep(HidppMacroStepKind.Delay, a << 8 | b));
                    break;
                case OpMouseDown:
                    steps.Add(new HidppMacroStep(HidppMacroStepKind.MouseDown, a << 8 | b));
                    break;
                case OpMouseUp:
                    steps.Add(new HidppMacroStep(HidppMacroStepKind.MouseUp, a << 8 | b));
                    break;
                case OpConsumerDown:
                    steps.Add(new HidppMacroStep(HidppMacroStepKind.ConsumerDown, a << 8 | b));
                    break;
                case OpConsumerUp:
                    steps.Add(new HidppMacroStep(HidppMacroStepKind.ConsumerUp, a << 8 | b));
                    break;
                case OpWheel:
                    steps.Add(new HidppMacroStep(HidppMacroStepKind.Wheel, (sbyte)a));
                    break;
                case OpHWheel:
                    steps.Add(new HidppMacroStep(HidppMacroStepKind.HWheel, (sbyte)a));
                    break;
                default:
                    editable = false;
                    break;
            }
            at += length;
        }
        return null;
    }

    /// <summary>Command length from the opcode range; 0 for an opcode outside the format.</summary>
    public static int Length(byte op) => op switch
    {
        < 0x20 => 1,
        < 0x40 => 2,
        < 0x60 => 3,
        < 0x80 => 5,
        _ => 0,
    };

    // Modifier keys on their own go out as their bit with usage 0; a modifier mask can also ride on an ordinary key.
    private static (byte Mods, byte Key) KeyFields(HidppMacroStep s)
    {
        if (s.Modifiers is < 0 or > 0xFF)
        {
            throw new ArgumentException($"modifier mask 0x{s.Modifiers:X} out of range");
        }
        if (s.Code is >= 0xE0 and <= 0xE7)
        {
            return ((byte)(s.Modifiers | 1 << (s.Code - 0xE0)), 0);
        }
        if (s.Code is < 0x04 or > 0xDF)
        {
            throw new ArgumentException($"key usage 0x{s.Code:X2} is not a keyboard key");
        }
        return ((byte)s.Modifiers, (byte)s.Code);
    }

    private static HidppMacroStep KeyStep(HidppMacroStepKind kind, int mods, int key)
    {
        if (key == 0 && mods != 0 && (mods & (mods - 1)) == 0)
        {
            return new HidppMacroStep(kind, 0xE0 + System.Numerics.BitOperations.Log2((uint)mods));
        }
        return new HidppMacroStep(kind, key, mods);
    }

    private static void Track(HashSet<(HidppMacroStepKind, int)> held, bool down, HidppMacroStepKind kind, int code, string what)
    {
        if (down)
        {
            held.Add((kind, code));
        }
        else if (!held.Remove((kind, code)))
        {
            throw new ArgumentException($"the macro releases a {what} (0x{code:X}) it never pressed");
        }
    }
}
