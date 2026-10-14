using System;
using System.Collections.Generic;
using System.Text;
using Nexus.Service.Devices;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Mappings;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Models.Devices;
using Nexus.Service.Peripherals.AsiahorseUmbra;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>One card per Umbra port with LEDs. The engine drives them only while the lighting source is "nexus"; otherwise they stay listed, marked as set on the device page.</summary>
public sealed class AsiahorseUmbraLightingDeviceProvider :
    ILightingDeviceProvider, ILightingFrameContributor, IDeviceStructureSource
{
    public const string DeviceId = "asiahorse-umbra";
    private const string PortIdPrefix = DeviceId + ":port:";

    private readonly UmbraHub _hub;
    private readonly IConfigStore _store;
    private string _lastSignature = "";

    // Reused across refreshes so the writer never sees a fresh zero-filled frame for one tick.
    private readonly Dictionary<string, DeviceFrame> _frameCache = new(StringComparer.Ordinal);

    public AsiahorseUmbraLightingDeviceProvider(UmbraHub hub, IConfigStore store)
    {
        _hub = hub;
        _store = store;
    }

    public bool IsConnected => _hub.IsConnected;

    public event Action? DevicesChanged;

    public static bool IsUmbraId(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith(DeviceId + ":", StringComparison.Ordinal);

    // 1-based, as shown to the user.
    public static string PortId(int portIndex) => PortIdPrefix + (portIndex + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static bool TryParsePortId(string id, out int portIndex)
    {
        portIndex = -1;
        if (!id.StartsWith(PortIdPrefix, StringComparison.Ordinal)
            || !int.TryParse(id.AsSpan(PortIdPrefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)
            || n < 1 || n > UmbraProtocol.PortCount)
        {
            return false;
        }
        portIndex = n - 1;
        return true;
    }

    public void OnHubStateUpdated()
    {
        var sig = BuildSignature();
        if (sig == _lastSignature)
        {
            return;
        }
        _lastSignature = sig;
        try { DevicesChanged?.Invoke(); } catch { /* swallow subscriber failures */ }
    }

    private string BuildSignature()
    {
        if (!_hub.IsConnected)
        {
            return "none";
        }
        // The source takes part: a source change flips the cards' device page reason.
        var sb = new StringBuilder(UmbraLightingPlan.SourceOf(_store.Load().Devices.AsiahorseUmbra)).Append(':');
        foreach (var p in _hub.Snapshot.Ports)
        {
            sb.Append(p.LedCount).Append(',');
        }
        return sb.ToString();
    }

    private bool PublishesZones(NexusSettings settings) =>
        _hub.IsConnected && UmbraLightingPlan.SourceOf(settings.Devices.AsiahorseUmbra) == UmbraLightingPlan.SourceNexus;

    // ── ILightingDeviceProvider ──

    public GetLightingDevicesResponse GetAll()
    {
        var resp = new GetLightingDevicesResponse { IsInit = true };
        if (!_hub.IsConnected)
        {
            return resp;
        }
        var settings = _store.Load();
        // Off the Nexus source the cards stay listed, but the device page sets the look.
        var source = UmbraLightingPlan.SourceOf(settings.Devices.AsiahorseUmbra);
        var reason = source == UmbraLightingPlan.SourceNexus ? null
            : source == UmbraLightingPlan.SourceArgbInput ? LightingDevice.ReasonArgbInput
            : LightingDevice.ReasonFirmware;
        var ports = _hub.Snapshot.Ports;
        var slot = 0;
        for (var p = 0; p < ports.Length; p++)
        {
            if (ports[p].LedCount > 0)
            {
                resp.Devices.Add(BuildCard(BuildPortStructure(p, ports[p].LedCount), slot++, settings, reason));
            }
        }
        return resp;
    }

    private static LightingDevice BuildCard(DeviceStructure structure, int slot, NexusSettings settings, string? devicePageReason)
    {
        var id = structure.DeviceId;
        var disabled = settings.Devices.DisabledLightingDevices;
        var isOn = true;
        for (var i = 0; i < disabled.Count; i++)
        {
            if (disabled[i] == id) { isOn = false; break; }
        }
        settings.Devices.LightingDevicePrefs.TryGetValue(id, out var pref);
        var (defX, defY, defW, defH) = DefaultLayout(slot);
        settings.Lighting.DeviceLayouts.TryGetValue(id, out var layout);
        var ledCount = structure.Segments[0].LedCount;
        return new LightingDevice
        {
            Id = id,
            DeviceKey = structure.DeviceKey,
            Name = structure.Name,
            Type = "ledstrip",
            IconType = "fan",
            LedsOn = isOn,
            Brightness = pref?.Brightness ?? 100,
            Hue = pref?.Hue ?? 0f,
            Saturation = pref?.Saturation ?? 1f,
            LedCount = ledCount,
            EnabledLedCount = ZoneResolution.CountEnabled(structure, null, id, ledCount, zoneHint: 0, settings),
            CanvasX = layout?.X ?? defX,
            CanvasY = layout?.Y ?? defY,
            CanvasW = layout?.W ?? defW,
            CanvasH = layout?.H ?? defH,
            CanvasRotation = NormalizeRotation(layout?.Rotation ?? 0),
            ParentDeviceId = DeviceId,
            ZoneIndex = 0,
            ZoneType = "linear",
            ZoneResizable = false,
            DeviceId = id,
            ZoneCustomizable = false,
            DevicePageReason = devicePageReason,
        };
    }

    public void SetDisabled(IReadOnlyList<string> ids) => _store.Update(s =>
        s.Devices.DisabledLightingDevices = new List<string>(ids));

    public void SetPower(string id, bool on) => _store.Update(s =>
    {
        var current = s.Devices.DisabledLightingDevices;
        if (on)
        {
            if (!current.Contains(id)) return;
            var next = new List<string>(current.Count);
            foreach (var x in current) if (x != id) next.Add(x);
            s.Devices.DisabledLightingDevices = next;
        }
        else
        {
            if (current.Contains(id)) return;
            var next = new List<string>(current.Count + 1);
            next.AddRange(current);
            next.Add(id);
            s.Devices.DisabledLightingDevices = next;
        }
    });

    public void SetBrightness(string id, int brightness) => _store.Update(s =>
    {
        if (!s.Devices.LightingDevicePrefs.TryGetValue(id, out var pref))
        { pref = new LightingDevicePreference(); s.Devices.LightingDevicePrefs[id] = pref; }
        pref.Brightness = Math.Clamp(brightness, 0, 100);
    });

    public void SetHue(string id, float hue) => _store.Update(s =>
    {
        if (!s.Devices.LightingDevicePrefs.TryGetValue(id, out var pref))
        { pref = new LightingDevicePreference(); s.Devices.LightingDevicePrefs[id] = pref; }
        pref.Hue = hue;
    });

    public void SetSaturation(string id, float saturation) => _store.Update(s =>
    {
        if (!s.Devices.LightingDevicePrefs.TryGetValue(id, out var pref))
        { pref = new LightingDevicePreference(); s.Devices.LightingDevicePrefs[id] = pref; }
        pref.Saturation = saturation;
    });

    // A count change restarts the controller, so it is set on the device page, not from a zone resize.
    public void SetZoneLedCount(string id, int count) { }

    public void Identify(string id, int durationMs) { }

    // ── IDeviceStructureSource ──

    public IReadOnlyList<DeviceStructure> GetStructures()
    {
        if (!PublishesZones(_store.Load()))
        {
            return Array.Empty<DeviceStructure>();
        }
        var structures = new List<DeviceStructure>();
        var ports = _hub.Snapshot.Ports;
        for (var p = 0; p < ports.Length; p++)
        {
            if (ports[p].LedCount > 0)
            {
                structures.Add(BuildPortStructure(p, ports[p].LedCount));
            }
        }
        return structures;
    }

    internal static DeviceStructure BuildPortStructure(int portIndex, int ledCount)
    {
        var id = PortId(portIndex);
        var number = portIndex + 1;
        var name = $"Asiahorse Umbra V2 Port {number}";
        var rawName = $"Port {number}";
        var key = DeviceKeyComputer.ForFirstParty(UmbraProtocol.VendorId, UmbraProtocol.ProductId, $"port{number}");
        var structure = new DeviceStructure { DeviceId = id, Name = name, DeviceKey = key, Partitionable = false };
        structure.Segments.Add(new StructureSegment
        {
            Index = 0,
            Name = rawName,
            LedCount = ledCount,
            FrameLedCount = ledCount,
            Resizable = false,
            MaxLedCount = UmbraProtocol.MaxLedCount,
            ZoneType = "linear",
        });
        structure.DefaultZones.Add(new DefaultZoneDef
        {
            Id = id,
            Name = name,
            RawName = rawName,
            DeviceKey = key,
            LegacyZoneIndex = -1,
            Slices = { new ZoneSlice { Segment = 0, Start = 0, Count = ledCount } },
        });
        return structure;
    }

    // ── ILightingFrameContributor ──

    public IReadOnlyList<DeviceFrame> BuildFrames(int startingIndex)
    {
        var settings = _store.Load();
        // The cache survives a disconnect so a reconnect reuses frames that already hold rendered colors.
        if (!PublishesZones(settings))
        {
            return Array.Empty<DeviceFrame>();
        }
        var layouts = settings.Lighting.DeviceLayouts;
        var ports = _hub.Snapshot.Ports;
        var frames = new List<DeviceFrame>();
        var idx = startingIndex;
        var slot = 0;
        for (var p = 0; p < ports.Length; p++)
        {
            if (ports[p].LedCount == 0)
            {
                continue;
            }
            var id = PortId(p);
            var ledCount = ports[p].LedCount;
            var (defX, defY, defW, defH) = DefaultLayout(slot++);
            layouts.TryGetValue(id, out var layout);
            var rot = NormalizeRotation(layout?.Rotation ?? 0);
            var thisIdx = idx++;
            if (_frameCache.TryGetValue(id, out var existing) && existing.Index == thisIdx && existing.LedCount == ledCount)
            {
                existing.X = layout?.X ?? defX;
                existing.Y = layout?.Y ?? defY;
                existing.W = layout?.W ?? defW;
                existing.H = layout?.H ?? defH;
                existing.Rotation = rot;
                frames.Add(existing);
                continue;
            }
            var frame = new DeviceFrame(
                index: thisIdx, id: id, ledCount: ledCount,
                x: layout?.X ?? defX, y: layout?.Y ?? defY,
                w: layout?.W ?? defW, h: layout?.H ?? defH, rotation: rot);
            _frameCache[id] = frame;
            frames.Add(frame);
        }
        if (_frameCache.Count > frames.Count)
        {
            var live = new HashSet<string>(frames.Count, StringComparer.Ordinal);
            foreach (var f in frames) live.Add(f.Id);
            foreach (var k in new List<string>(_frameCache.Keys))
            {
                if (!live.Contains(k)) _frameCache.Remove(k);
            }
        }
        return frames;
    }

    private static int NormalizeRotation(int rotation) => (((rotation % 360) + 360) % 360);

    internal static (float x, float y, float w, float h) DefaultLayout(int slot)
    {
        const float W = 120f;
        const float H = 105f;
        const float GapX = 140f;
        const float GapY = 125f;
        const float BaseX = 40f;
        const float BaseY = 40f;
        const int Cols = 4;
        var s = slot < 0 ? 0 : slot;
        return (BaseX + (s % Cols) * GapX, BaseY + (s / Cols) * GapY, W, H);
    }
}
