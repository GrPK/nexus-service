using System;
using System.Collections.Generic;
using Nexus.Service.Devices;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Mappings;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Models.Devices;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// The Universal Screen 8.8's 60 bezel LEDs, one fixed zone streamed frame by frame by
/// <see cref="UniversalScreen88LedBoard"/>. Owns the ring OpenRGB used to drive, so the bridge never
/// writes to it while Nexus does.
/// </summary>
public sealed class UniversalScreen88LedLightingDeviceProvider :
    ILightingDeviceProvider, ILightingFrameContributor, IDeviceStructureSource, IOpenRgbDeviceOwner
{
    public const string HandlerId = UniversalScreen88LedProtocol.HandlerId;
    public const string RingZoneId = HandlerId + ":ring";

    private static readonly string DeviceKey = DeviceKeyComputer.ForFirstParty(
        UniversalScreen88LedProtocol.VendorId, UniversalScreen88LedProtocol.ProductId, "led");

    private readonly UniversalScreen88LedBoard _board;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private readonly Dictionary<string, DeviceFrame> _frameCache = new();

    public UniversalScreen88LedLightingDeviceProvider(UniversalScreen88LedBoard board, IConfigStore store, Np50IdentifyTracker identify)
    {
        _board = board;
        _store = store;
        _identify = identify;
        _board.AvailabilityChanged += () =>
        {
            // First sight of the native ring takes over what the Lighting page kept for OpenRGB's card.
            if (_board.IsAvailable && UniversalScreen88LayoutMigration.NeedsApply(_store.Load()))
            {
                _store.Update(s => UniversalScreen88LayoutMigration.Apply(s));
            }
            DevicesChanged?.Invoke();
        };
    }

    public bool IsConnected => _board.IsAvailable;

    public event Action? DevicesChanged;

    public static bool IsUniversalScreen88LedId(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith(HandlerId + ":", StringComparison.Ordinal);

    public bool OwnsOpenRgbDevice(RgbDevice device) =>
        IsConnected && NativeDetectorClaims.Owns(NativeDetectorClaims.UniversalScreen88, device);

    public GetLightingDevicesResponse GetAll()
    {
        var resp = new GetLightingDevicesResponse { IsInit = true };
        if (!IsConnected)
        {
            return resp;
        }
        var settings = _store.Load();
        var structure = BuildStructure();
        foreach (var zone in ZoneResolution.Resolve(structure, settings))
        {
            resp.Devices.Add(BuildCard(structure, zone, settings));
        }
        return resp;
    }

    private static LightingDevice BuildCard(DeviceStructure structure, ResolvedZone zone, NexusSettings settings)
    {
        var isOn = !settings.Devices.DisabledLightingDevices.Contains(zone.Id);
        settings.Devices.LightingDevicePrefs.TryGetValue(zone.Id, out var pref);
        settings.Lighting.DeviceLayouts.TryGetValue(zone.Id, out var layout);
        var (defX, defY, defW, defH) = DefaultLayout;
        return new LightingDevice
        {
            Id = zone.Id,
            DeviceKey = zone.DeviceKey,
            Name = zone.Name,
            Type = "ledstrip",
            IconType = "strip",
            LedsOn = isOn,
            Brightness = pref?.Brightness ?? 100,
            Hue = pref?.Hue ?? 0f,
            Saturation = pref?.Saturation ?? 1f,
            LedCount = zone.LedCount,
            EnabledLedCount = ZoneResolution.CountEnabled(structure, zone, zone.Id, zone.LedCount, zoneHint: 0, settings),
            CanvasX = layout?.X ?? defX,
            CanvasY = layout?.Y ?? defY,
            CanvasW = layout?.W ?? defW,
            CanvasH = layout?.H ?? defH,
            CanvasRotation = (((layout?.Rotation ?? 0) % 360) + 360) % 360,
            ParentDeviceId = UniversalScreen88Driver.Id,
            ZoneIndex = zone.Ordinal,
            ZoneType = "linear",
            ZoneResizable = false,
            DeviceId = structure.DeviceId,
            ZoneCustomizable = false,
        };
    }

    public void SetDisabled(IReadOnlyList<string> ids) => _store.Update(s =>
        s.Devices.DisabledLightingDevices = new List<string>(ids));

    public void SetPower(string id, bool on) => _store.Update(s =>
    {
        var current = s.Devices.DisabledLightingDevices;
        if (on == !current.Contains(id)) return;
        var next = new List<string>(current);
        if (on) next.Remove(id); else next.Add(id);
        s.Devices.DisabledLightingDevices = next;
    });

    public void SetBrightness(string id, int brightness) =>
        UpdatePref(id, pref => pref.Brightness = Math.Clamp(brightness, 0, 100));

    public void SetHue(string id, float hue) => UpdatePref(id, pref => pref.Hue = hue);

    public void SetSaturation(string id, float saturation) => UpdatePref(id, pref => pref.Saturation = saturation);

    public void SetZoneLedCount(string id, int count) { }

    public void Identify(string id, int durationMs) => _identify.Schedule(id, durationMs);

    private void UpdatePref(string id, Action<LightingDevicePreference> apply) => _store.Update(s =>
    {
        if (!s.Devices.LightingDevicePrefs.TryGetValue(id, out var pref))
        {
            pref = new LightingDevicePreference();
            s.Devices.LightingDevicePrefs[id] = pref;
        }
        apply(pref);
    });

    public IReadOnlyList<DeviceStructure> GetStructures() =>
        IsConnected ? new[] { BuildStructure() } : Array.Empty<DeviceStructure>();

    public IReadOnlyList<DeviceFrame> BuildFrames(int startingIndex)
    {
        if (!IsConnected)
        {
            return Array.Empty<DeviceFrame>();
        }
        var settings = _store.Load();
        var frames = new List<DeviceFrame>(1);
        var index = startingIndex;
        foreach (var zone in ZoneResolution.Resolve(BuildStructure(), settings))
        {
            settings.Lighting.DeviceLayouts.TryGetValue(zone.Id, out var layout);
            var (defX, defY, defW, defH) = DefaultLayout;
            var x = layout?.X ?? defX;
            var y = layout?.Y ?? defY;
            var w = layout?.W ?? defW;
            var h = layout?.H ?? defH;
            var rotation = (((layout?.Rotation ?? 0) % 360) + 360) % 360;
            var thisIndex = index++;
            if (_frameCache.TryGetValue(zone.Id, out var frame)
                && frame.Index == thisIndex
                && frame.LedCount == zone.FrameLedCount)
            {
                frame.X = x;
                frame.Y = y;
                frame.W = w;
                frame.H = h;
                frame.Rotation = rotation;
            }
            else
            {
                frame = new DeviceFrame(thisIndex, zone.Id, zone.FrameLedCount, x, y, w, h, rotation);
                _frameCache[zone.Id] = frame;
            }
            frames.Add(frame);
        }
        return frames;
    }

    /// <summary>One 60-LED ring placed like OpenRGB's view of it (<see cref="UniversalScreenRing"/>).</summary>
    internal static DeviceStructure BuildStructure()
    {
        const int leds = UniversalScreen88LedProtocol.LedCount;
        var (u, v) = UniversalScreenRing.Positions();
        var structure = new DeviceStructure
        {
            DeviceId = HandlerId,
            Name = "Universal Screen 8.8",
            DeviceKey = DeviceKey,
            Partitionable = false,
        };
        structure.Segments.Add(new StructureSegment
        {
            Index = 0,
            Name = "Screen Lighting",
            LedCount = leds,
            FrameLedCount = leds,
            Resizable = false,
            ZoneType = "linear",
            DefaultU = u,
            DefaultV = v,
        });
        structure.DefaultZones.Add(new DefaultZoneDef
        {
            Id = RingZoneId,
            Name = "Universal Screen 8.8 Lighting",
            RawName = "Screen Lighting",
            DeviceKey = DeviceKey,
            LegacyZoneIndex = -1,
            Slices = { new ZoneSlice { Segment = 0, Start = 0, Count = leds } },
        });
        return structure;
    }

    /// <summary>The 1920x480 glass's 4:1 shape on the effect canvas, the size OpenRGB's card used.</summary>
    private static (float X, float Y, float W, float H) DefaultLayout => (700f, 300f, 200f, 50f);
}
