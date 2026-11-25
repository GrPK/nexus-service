using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Nexus.Service.Peripherals.LogitechHidpp;

/// <summary>
/// One profile sector of a Logitech mouse's onboard memory (HID++ 2.0 feature 0x8100,
/// profile formats 1 to 5), in the layout Solaar and libratbag document and G HUB writes:
/// report rate, five DPI levels with a default and a DPI shift level, timeouts, sixteen
/// button bindings plus their G-Shift copies, a UTF-16 name, two lighting blocks and a
/// CRC-16/CCITT over everything before it. Parsing keeps the raw sector; edits change only
/// the fields they name and every other byte stays as the mouse wrote it.
/// </summary>
public sealed class HidppOnboardProfile
{
    public const int ButtonSlots = 16;
    public const int DpiLevels = 5;
    public const int NameChars = 24;

    private const int ReportRateOffset = 0;
    private const int DefaultDpiOffset = 1;
    private const int ShiftDpiOffset = 2;
    private const int DpiOffset = 3;
    private const int PowerModeOffset = 16;
    private const int AngleSnapOffset = 17;
    private const int ButtonsOffset = 32;
    private const int GShiftOffset = 96;
    private const int NameOffset = 160;
    private const int NameBytes = NameChars * 2;

    private readonly byte[] _raw;

    private HidppOnboardProfile(byte[] raw)
    {
        _raw = raw;
    }

    /// <summary>Sector size this profile was read with (255 on the G502 X).</summary>
    public int SectorSize => _raw.Length;

    /// <summary>Report interval in milliseconds (1 = 1000 Hz, 2 = 500, 4 = 250, 8 = 125).</summary>
    public int ReportIntervalMs => _raw[ReportRateOffset];

    public int DefaultDpiIndex => _raw[DefaultDpiOffset];
    public int ShiftDpiIndex => _raw[ShiftDpiOffset];

    /// <summary>The five DPI levels; 0 marks an unused level.</summary>
    public IReadOnlyList<int> Dpi
    {
        get
        {
            var levels = new int[DpiLevels];
            for (var i = 0; i < DpiLevels; i++)
            {
                var value = BinaryPrimitives.ReadUInt16LittleEndian(_raw.AsSpan(DpiOffset + i * 2));
                levels[i] = value == 0xFFFF ? 0 : value;
            }
            return levels;
        }
    }

    public int PowerMode => _raw[PowerModeOffset];
    public int AngleSnap => _raw[AngleSnapOffset];

    public HidppButtonBinding Button(int slot) => HidppButtonBinding.Decode(_raw.AsSpan(ButtonsOffset + Slot(slot) * 4, 4));
    public HidppButtonBinding GShiftButton(int slot) => HidppButtonBinding.Decode(_raw.AsSpan(GShiftOffset + Slot(slot) * 4, 4));

    /// <summary>The profile's name; empty when the sector holds no name (erased 0xFFFF cells).</summary>
    public string Name
    {
        get
        {
            var span = _raw.AsSpan(NameOffset, NameBytes);
            var chars = new StringBuilder(NameChars);
            for (var i = 0; i < NameChars; i++)
            {
                var c = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(i * 2));
                if (c == 0 || c == 0xFFFF)
                {
                    break;
                }
                chars.Append((char)c);
            }
            return chars.ToString();
        }
    }

    /// <summary>Whether the stored CRC matches the bytes it covers.</summary>
    public bool CrcValid => StoredCrc == Crc16(_raw.AsSpan(0, _raw.Length - 2));

    private ushort StoredCrc => BinaryPrimitives.ReadUInt16BigEndian(_raw.AsSpan(_raw.Length - 2));

    /// <summary>Wraps a sector as read from the mouse. Null when it is too short to hold a profile.</summary>
    public static HidppOnboardProfile? Parse(ReadOnlySpan<byte> sector)
    {
        if (sector.Length < NameOffset + NameBytes + 2)
        {
            return null;
        }
        return new HidppOnboardProfile(sector.ToArray());
    }

    /// <summary>The sector bytes, CRC included.</summary>
    public byte[] ToBytes() => (byte[])_raw.Clone();

    /// <summary>
    /// A copy with the given fields changed and the CRC recomputed. Null fields keep their
    /// value. Throws <see cref="ArgumentException"/> for values the mouse cannot hold.
    /// </summary>
    public HidppOnboardProfile With(HidppOnboardProfileEdit edit)
    {
        var raw = (byte[])_raw.Clone();
        if (edit.ReportIntervalMs is { } rate)
        {
            if (rate is not (1 or 2 or 4 or 8))
            {
                throw new ArgumentException($"report interval {rate} ms is not one of 1, 2, 4, 8", nameof(edit));
            }
            raw[ReportRateOffset] = (byte)rate;
        }
        if (edit.Dpi is { } dpi)
        {
            if (dpi.Count != DpiLevels)
            {
                throw new ArgumentException($"expected {DpiLevels} DPI levels, got {dpi.Count}", nameof(edit));
            }
            for (var i = 0; i < DpiLevels; i++)
            {
                if (dpi[i] is < 0 or > 0xFFFE)
                {
                    throw new ArgumentException($"DPI level {i + 1} out of range: {dpi[i]}", nameof(edit));
                }
                BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(DpiOffset + i * 2), (ushort)dpi[i]);
            }
        }
        var levels = UsedLevels(raw);
        if (edit.DefaultDpiIndex is { } def)
        {
            raw[DefaultDpiOffset] = (byte)CheckLevel(def, levels, "default");
        }
        if (edit.ShiftDpiIndex is { } shift)
        {
            raw[ShiftDpiOffset] = (byte)CheckLevel(shift, levels, "shift");
        }
        if (edit.Buttons is { } buttons)
        {
            foreach (var (slot, binding) in buttons)
            {
                binding.Encode().CopyTo(raw.AsSpan(ButtonsOffset + Slot(slot) * 4, 4));
            }
        }
        if (edit.GShiftButtons is { } gshift)
        {
            foreach (var (slot, binding) in gshift)
            {
                binding.Encode().CopyTo(raw.AsSpan(GShiftOffset + Slot(slot) * 4, 4));
            }
        }
        if (edit.Name is { } name)
        {
            if (name.Length > NameChars)
            {
                throw new ArgumentException($"name longer than {NameChars} characters", nameof(edit));
            }
            var span = raw.AsSpan(NameOffset, NameBytes);
            span.Fill(0xFF);
            for (var i = 0; i < name.Length; i++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(i * 2), name[i]);
            }
            if (name.Length < NameChars)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(name.Length * 2), 0);
            }
        }
        BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(raw.Length - 2), Crc16(raw.AsSpan(0, raw.Length - 2)));
        return new HidppOnboardProfile(raw);
    }

    /// <summary>CRC-16/CCITT-FALSE (poly 0x1021, init 0xFFFF), stored big-endian at the end of the sector.</summary>
    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= (ushort)(b << 8);
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
            }
        }
        return crc;
    }

    private static int Slot(int slot) =>
        slot is >= 0 and < ButtonSlots ? slot : throw new ArgumentOutOfRangeException(nameof(slot), slot, "button slot out of range");

    private static int UsedLevels(byte[] raw)
    {
        var used = 0;
        for (var i = 0; i < DpiLevels; i++)
        {
            var value = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(DpiOffset + i * 2));
            if (value is 0 or 0xFFFF)
            {
                break;
            }
            used++;
        }
        return used;
    }

    private static int CheckLevel(int index, int used, string which) =>
        index >= 0 && index < Math.Max(used, 1)
            ? index
            : throw new ArgumentException($"{which} DPI level {index + 1} is not one of the {used} set levels");
}

/// <summary>Fields to change in a profile; null keeps the current value.</summary>
public sealed class HidppOnboardProfileEdit
{
    public int? ReportIntervalMs { get; init; }
    public IReadOnlyList<int>? Dpi { get; init; }
    public int? DefaultDpiIndex { get; init; }
    public int? ShiftDpiIndex { get; init; }
    public IReadOnlyDictionary<int, HidppButtonBinding>? Buttons { get; init; }
    public IReadOnlyDictionary<int, HidppButtonBinding>? GShiftButtons { get; init; }
    public string? Name { get; init; }
}
