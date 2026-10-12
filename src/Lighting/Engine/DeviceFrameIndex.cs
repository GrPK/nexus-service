using System;
using System.Collections.Generic;
using Nexus.Service.Models.Devices;

namespace Nexus.Service.Lighting.Engine;

/// <summary>
/// Maps a lighting device id to the byte the engine's binary frame tags its LEDs
/// with, so a client can pick one device's colors out of the frame.
/// </summary>
public static class DeviceFrameIndex
{
    /// <summary>Exactly what SerializeAndBroadcast writes for the frame: the low byte of its Index.</summary>
    public static byte WireByte(DeviceFrame frame) => (byte)frame.Index;

    public static void Apply(IEnumerable<LightingDevice> devices, DeviceFrame[] frames)
    {
        var byId = new Dictionary<string, DeviceFrame>(frames.Length, StringComparer.Ordinal);
        foreach (var f in frames)
        {
            byId.TryAdd(f.Id, f);
        }
        foreach (var d in devices)
        {
            d.FrameIndex = byId.TryGetValue(d.Id, out var f) ? WireByte(f) : null;
        }
    }
}
