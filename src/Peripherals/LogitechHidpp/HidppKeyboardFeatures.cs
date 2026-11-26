using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.LogitechHidpp;

/// <summary>
/// HID++ 2.0 feature 0x8101 (profile and file management) on newer Logitech keyboards: files
/// identified by the feature that owns them, an index and a type. The write path as G HUB uses
/// it: function 2 starts a buffer of a given size, function 3 appends up to 16 bytes (the reply
/// counts the chunks), function 9 stores the buffer as [owner][index][type] with its CRC-32.
/// </summary>
public sealed class HidppFileManagement
{
    public const ushort FeatureId = 0x8101;
    private const int Chunk = 16;

    private readonly IHidppChannel _channel;
    private readonly byte _index;

    private HidppFileManagement(IHidppChannel channel, byte index)
    {
        _channel = channel;
        _index = index;
    }

    public static HidppFileManagement? Open(IHidppChannel channel) =>
        channel.FeatureIndex(FeatureId) is { } index ? new HidppFileManagement(channel, index) : null;

    /// <summary>Stores <paramref name="data"/> as the file [owner][index][type]; false when any step is refused.</summary>
    public bool WriteFile(ushort ownerFeature, byte fileIndex, byte fileType, ReadOnlySpan<byte> data)
    {
        var size = data.Length;
        if (size is 0 or > 0xFFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(data), size, "file size must be 1..65535 bytes");
        }
        if (_channel.Call(_index, 2, new[] { (byte)(size >> 8), (byte)size }) is null)
        {
            return false;
        }
        var chunk = new byte[Chunk];
        for (var offset = 0; offset < size; offset += Chunk)
        {
            Array.Clear(chunk);
            data.Slice(offset, Math.Min(Chunk, size - offset)).CopyTo(chunk);
            if (_channel.Call(_index, 3, chunk) is null)
            {
                return false;
            }
        }
        var crc = HidppKeyTable.Crc32(data);
        var store = new byte[]
        {
            (byte)(ownerFeature >> 8), (byte)ownerFeature, fileIndex, fileType, 0x00,
            (byte)(size >> 8), (byte)size,
            (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc,
        };
        return _channel.Call(_index, 9, store) is not null;
    }
}

/// <summary>
/// HID++ 2.0 feature 0x1B05 (full key customization): whether the key tables are in force.
/// This is G HUB's "custom assignments" switch (Fn+F1 on the keyboard). Function 1 [01 v] and
/// function 3 [01 v] set it as G HUB does; function 1 [00 00] reads it.
/// </summary>
public sealed class HidppKeyCustomization
{
    public const ushort FeatureId = 0x1B05;

    private readonly IHidppChannel _channel;
    private readonly byte _index;

    private HidppKeyCustomization(IHidppChannel channel, byte index)
    {
        _channel = channel;
        _index = index;
    }

    public static HidppKeyCustomization? Open(IHidppChannel channel) =>
        channel.FeatureIndex(FeatureId) is { } index ? new HidppKeyCustomization(channel, index) : null;

    public bool? GetEnabled() => _channel.Call(_index, 1, new byte[] { 0x00, 0x00 }) is { } p ? p[0] != 0 : null;

    public bool SetEnabled(bool enabled)
    {
        var v = (byte)(enabled ? 1 : 0);
        return _channel.Call(_index, 1, new byte[] { 0x01, v }) is not null
            && _channel.Call(_index, 3, new byte[] { 0x01, v }) is not null;
    }
}

/// <summary>
/// HID++ 2.0 feature 0x1B10 (control list): the keyboard's controls by Logitech control id. A
/// key's position in this list is the key number the 0x1B05 tables use.
/// </summary>
public static class HidppControlList
{
    public const ushort FeatureId = 0x1B10;

    /// <summary>Control ids in list order (0 for an empty slot), or null when the list cannot be read.</summary>
    public static IReadOnlyList<int>? Read(IHidppChannel channel)
    {
        if (channel.FeatureIndex(FeatureId) is not { } index || channel.Call(index, 0, ReadOnlySpan<byte>.Empty) is not { } info)
        {
            return null;
        }
        var count = info[0];
        var cids = new List<int>(count);
        for (var offset = 0; offset < count; offset += 8)
        {
            if (channel.Call(index, 1, new[] { (byte)offset }) is not { } p)
            {
                return null;
            }
            for (var i = 0; i < 8 && offset + i < count; i++)
            {
                cids.Add(p[2 * i] << 8 | p[2 * i + 1]);
            }
        }
        return cids;
    }
}
