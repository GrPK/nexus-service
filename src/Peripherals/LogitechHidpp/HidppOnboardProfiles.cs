using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.LogitechHidpp;

/// <summary>What feature 0x8100 reports about a mouse's onboard memory (function 0).</summary>
public sealed record HidppOnboardInfo(
    int MemoryModel, int ProfileFormat, int MacroFormat, int ProfileCount, int RomProfileCount,
    int ButtonCount, int SectorCount, int SectorSize)
{
    public static HidppOnboardInfo? Parse(ReadOnlySpan<byte> p) =>
        p.Length < 9 ? null : new HidppOnboardInfo(p[0], p[1], p[2], p[3], p[4], p[5], p[6], p[7] << 8 | p[8]);
}

/// <summary>One entry of the profile directory (sector 0): where a profile lives and whether it is in the cycle.</summary>
public sealed record HidppProfileSlot(int Sector, bool Enabled);

/// <summary>
/// HID++ 2.0 feature 0x8100 Onboard Profiles: the mouse's own memory, where the profiles it
/// uses without any software live. Function numbers as libratbag and Solaar use them and as
/// G HUB sends them: 0 info, 1/2 set/get mode (1 onboard, 2 host), 3/4 set/get current
/// profile, 5 read 16 bytes, 6 start a write, 7 write 16 bytes, 8 end the write.
/// </summary>
public sealed class HidppOnboardProfiles
{
    public const ushort FeatureId = 0x8100;
    public const int ModeOnboard = 1;
    public const int ModeHost = 2;
    /// <summary>Sectors from here up are the factory (read-only) profiles.</summary>
    public const int RomSectorBase = 0x0100;

    private const int Chunk = 16;

    private readonly IHidppChannel _channel;
    private readonly byte _index;

    private HidppOnboardProfiles(IHidppChannel channel, byte index)
    {
        _channel = channel;
        _index = index;
    }

    /// <summary>The feature on this device, or null when it has no onboard profiles.</summary>
    public static HidppOnboardProfiles? Open(IHidppChannel channel) =>
        channel.FeatureIndex(FeatureId) is { } index ? new HidppOnboardProfiles(channel, index) : null;

    public HidppOnboardInfo? GetInfo() => _channel.Call(_index, 0, ReadOnlySpan<byte>.Empty) is { } p ? HidppOnboardInfo.Parse(p) : null;

    public int? GetMode() => _channel.Call(_index, 2, ReadOnlySpan<byte>.Empty)?[0];

    public bool SetMode(int mode) => _channel.Call(_index, 1, new[] { (byte)mode }) is not null;

    /// <summary>Sector of the profile the mouse uses now.</summary>
    public int? GetCurrentSector() => _channel.Call(_index, 4, ReadOnlySpan<byte>.Empty) is { } p ? p[0] << 8 | p[1] : null;

    public bool SetCurrentSector(int sector) => _channel.Call(_index, 3, new[] { (byte)(sector >> 8), (byte)sector }) is not null;

    /// <summary>A whole sector. The last read is aligned to the sector's end, as G HUB does (offset size - 16).</summary>
    public byte[]? ReadSector(int sector, int size)
    {
        if (size < Chunk)
        {
            return null;
        }
        var data = new byte[size];
        for (var offset = 0; offset < size; offset += Chunk)
        {
            var at = Math.Min(offset, size - Chunk);
            var p = _channel.Call(_index, 5, new[] { (byte)(sector >> 8), (byte)sector, (byte)(at >> 8), (byte)at });
            if (p is null)
            {
                return null;
            }
            p.AsSpan(0, Chunk).CopyTo(data.AsSpan(at));
        }
        return data;
    }

    /// <summary>
    /// Writes a whole sector (start, 16-byte chunks, end). The caller sets the CRC and reads the
    /// sector back to confirm it landed. Factory sectors are refused here, not left to the mouse.
    /// </summary>
    public bool WriteSector(int sector, ReadOnlySpan<byte> data)
    {
        if (sector <= 0 || sector >= RomSectorBase)
        {
            throw new ArgumentOutOfRangeException(nameof(sector), sector, "only user profile sectors (1..0xFF) are writable");
        }
        var size = data.Length;
        if (_channel.Call(_index, 6, new[] { (byte)(sector >> 8), (byte)sector, (byte)0, (byte)0, (byte)(size >> 8), (byte)size }) is null)
        {
            return false;
        }
        var chunk = new byte[Chunk];
        for (var offset = 0; offset < size; offset += Chunk)
        {
            Array.Clear(chunk);
            data.Slice(offset, Math.Min(Chunk, size - offset)).CopyTo(chunk);
            if (_channel.Call(_index, 7, chunk) is null)
            {
                return false;
            }
        }
        return _channel.Call(_index, 8, ReadOnlySpan<byte>.Empty) is not null;
    }

    /// <summary>The profile directory (sector 0): 4-byte entries (sector, enabled, reserved) up to an 0xFFFF sector.</summary>
    public static IReadOnlyList<HidppProfileSlot> ParseDirectory(ReadOnlySpan<byte> directory)
    {
        var slots = new List<HidppProfileSlot>();
        for (var offset = 0; offset + 4 <= directory.Length - 2; offset += 4)
        {
            var sector = directory[offset] << 8 | directory[offset + 1];
            if (sector == 0xFFFF)
            {
                break;
            }
            slots.Add(new HidppProfileSlot(sector, directory[offset + 2] != 0));
        }
        return slots;
    }
}
