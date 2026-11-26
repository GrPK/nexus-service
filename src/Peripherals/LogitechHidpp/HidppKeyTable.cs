using System;
using System.Collections.Generic;
using System.Linq;

namespace Nexus.Service.Peripherals.LogitechHidpp;

/// <summary>What a remapped key sends.</summary>
public enum HidppKeyActionKind
{
    /// <summary>A keyboard key (HID usage page 7) with modifiers.</summary>
    Keyboard,
    /// <summary>Mouse buttons, as a button mask (1 left, 2 right, 4 middle, 8 back, 0x10 forward).</summary>
    Mouse,
    /// <summary>A consumer control (media keys, volume), HID usage page 0x0C.</summary>
    Consumer,
    /// <summary>Holds the G-shift layer while pressed.</summary>
    GShift,
    /// <summary>The key sends nothing.</summary>
    Disabled,
}

/// <summary>
/// The 4-byte action of a key table row, the HID binding format of onboard mouse profiles:
/// 80 02 mm kk keyboard, 80 01 bb bb mouse mask, 80 03 uu uu consumer usage, 80 04 00 02
/// G-shift, 80 00 00 00 nothing.
/// </summary>
public sealed record HidppKeyAction(HidppKeyActionKind Kind, int Code = 0, int Modifiers = 0)
{
    public const int GShiftCode = 0x02;

    public static readonly HidppKeyAction Disabled = new(HidppKeyActionKind.Disabled);
    public static readonly HidppKeyAction GShift = new(HidppKeyActionKind.GShift, GShiftCode);

    public static HidppKeyAction Key(int usage, int modifiers = 0) => new(HidppKeyActionKind.Keyboard, usage, modifiers);

    public byte[] Encode()
    {
        Validate();
        return Kind switch
        {
            HidppKeyActionKind.Keyboard => new byte[] { 0x80, 0x02, (byte)Modifiers, (byte)Code },
            HidppKeyActionKind.Mouse => new byte[] { 0x80, 0x01, (byte)(Code >> 8), (byte)Code },
            HidppKeyActionKind.Consumer => new byte[] { 0x80, 0x03, (byte)(Code >> 8), (byte)Code },
            HidppKeyActionKind.GShift => new byte[] { 0x80, 0x04, 0x00, GShiftCode },
            _ => new byte[] { 0x80, 0x00, 0x00, 0x00 },
        };
    }

    /// <summary>The action in <paramref name="b"/>, or null for a type this code does not know.</summary>
    public static HidppKeyAction? Decode(ReadOnlySpan<byte> b)
    {
        if (b.Length < 4 || b[0] != 0x80)
        {
            return null;
        }
        return b[1] switch
        {
            0x00 => Disabled,
            0x01 => new HidppKeyAction(HidppKeyActionKind.Mouse, b[2] << 8 | b[3]),
            0x02 => new HidppKeyAction(HidppKeyActionKind.Keyboard, b[3], b[2]),
            0x03 => new HidppKeyAction(HidppKeyActionKind.Consumer, b[2] << 8 | b[3]),
            0x04 when b[3] == GShiftCode => GShift,
            _ => null,
        };
    }

    public void Validate()
    {
        var ok = Kind switch
        {
            HidppKeyActionKind.Keyboard => Code is > 0 and <= 0xFF && Modifiers is >= 0 and <= 0xFF,
            HidppKeyActionKind.Mouse => Code is > 0 and <= 0xFFFF && Modifiers == 0,
            HidppKeyActionKind.Consumer => Code is > 0 and <= 0xFFFF && Modifiers == 0,
            _ => true,
        };
        if (!ok)
        {
            throw new ArgumentException($"invalid {Kind} action (code {Code}, modifiers {Modifiers})");
        }
    }
}

/// <summary>
/// One row of a key table. A plain row makes the keyboard send <see cref="Action"/> itself; a
/// row with a <see cref="DivertSlot"/> makes the key only report its press to software (G HUB's
/// way of running macros), which needs a program listening.
/// </summary>
public sealed record HidppKeyRow(int Key, HidppKeyAction Action, int DivertSlot = 0);

/// <summary>
/// The key table of feature 0x1B05 (full key customization) for one layer, as G HUB writes it
/// through the 0x8101 file system: a big-endian row count, then 11-byte rows sorted by key:
/// [key][01][divert slot][A 4][B 4]. A is 80 00 00 00 and B the action for a plain remap; a
/// diverted row has A = 00 00 80 00 and B = 80 00 00 00. The key is the control's index in
/// feature 0x1B10's control list. Tables are written whole and never read back (the keyboard
/// has no read for them), so the copy kept by Nexus is the truth.
/// </summary>
public static class HidppKeyTable
{
    public const int RowLength = 11;
    public const int MaxRows = 0x80;

    /// <summary>Feature that owns the tables in the file system.</summary>
    public const ushort OwnerFeature = 0x1B05;
    /// <summary>File type of a key table.</summary>
    public const byte FileType = 0x02;

    /// <summary>Tables per layer: 1 base, 2 while Fn is held, 3 while G-shift is held.</summary>
    public const int LayerBase = 1;
    public const int LayerFn = 2;
    public const int LayerGShift = 3;

    private static readonly byte[] PlainMarker = { 0x80, 0x00, 0x00, 0x00 };
    private static readonly byte[] DivertMarker = { 0x00, 0x00, 0x80, 0x00 };

    public static byte[] Build(IEnumerable<HidppKeyRow> rows)
    {
        var sorted = rows.OrderBy(r => r.Key).ToList();
        if (sorted.Count > MaxRows)
        {
            throw new ArgumentException($"at most {MaxRows} keys per layer");
        }
        for (var i = 0; i < sorted.Count; i++)
        {
            var row = sorted[i];
            if (row.Key is < 0 or >= MaxRows)
            {
                throw new ArgumentException($"key {row.Key} out of range");
            }
            if (i > 0 && sorted[i - 1].Key == row.Key)
            {
                throw new ArgumentException($"key {row.Key} appears twice");
            }
            if (row.DivertSlot is < 0 or > 0xFF)
            {
                throw new ArgumentException($"divert slot {row.DivertSlot} out of range");
            }
        }
        var data = new byte[2 + sorted.Count * RowLength];
        data[0] = (byte)(sorted.Count >> 8);
        data[1] = (byte)sorted.Count;
        var at = 2;
        foreach (var row in sorted)
        {
            data[at] = (byte)row.Key;
            data[at + 1] = 0x01;
            data[at + 2] = (byte)row.DivertSlot;
            if (row.DivertSlot == 0)
            {
                PlainMarker.CopyTo(data, at + 3);
                row.Action.Encode().CopyTo(data, at + 7);
            }
            else
            {
                DivertMarker.CopyTo(data, at + 3);
                PlainMarker.CopyTo(data, at + 7);
            }
            at += RowLength;
        }
        return data;
    }

    /// <summary>The rows of a table, or null when it is malformed.</summary>
    public static IReadOnlyList<HidppKeyRow>? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
        {
            return null;
        }
        var count = data[0] << 8 | data[1];
        if (data.Length < 2 + count * RowLength)
        {
            return null;
        }
        var rows = new List<HidppKeyRow>(count);
        for (var i = 0; i < count; i++)
        {
            var row = data.Slice(2 + i * RowLength, RowLength);
            if (row[1] != 0x01)
            {
                return null;
            }
            if (row[2] != 0)
            {
                rows.Add(new HidppKeyRow(row[0], HidppKeyAction.Disabled, row[2]));
                continue;
            }
            if (HidppKeyAction.Decode(row[7..]) is not { } action)
            {
                return null;
            }
            rows.Add(new HidppKeyRow(row[0], action));
        }
        return rows;
    }

    /// <summary>The CRC the file system checks: CRC-32 (IEEE, the zlib one).</summary>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
        return ~crc;
    }

    private static readonly uint[] Table = MakeTable();

    private static uint[] MakeTable()
    {
        var table = new uint[256];
        for (var n = 0u; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }
            table[n] = c;
        }
        return table;
    }
}
