using System.Collections.Generic;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// One-time move of what the Lighting page kept for the bundled OpenRGB's "Lian Li Universal Screen"
/// card to the native 8.8 ring card: its place on the canvas, power, control state and pin (live and
/// in every saved layout preset), its card preferences, and its device-wide aspect ratio and LED
/// overrides (both rings are the same 60 LEDs). OpenRGB names that card by its location,
/// "USB: 0416:8050", because the board reports no USB serial. A native entry the user already set
/// is left as it is.
/// </summary>
internal static class UniversalScreen88LayoutMigration
{
    /// <summary>RgbDevice.StableId for OpenRGB's controller: "openrgb-l-" + the sanitized "USB: 0416:8050".</summary>
    internal const string OpenRgbId = "openrgb-l-USB__0416_8050";

    /// <summary>True until the move has run once. Read-only, so it can gate an Update.</summary>
    public static bool NeedsApply(NexusSettings settings) => !settings.Devices.UniversalScreen88Lighting.OpenRgbCardMoved;

    /// <summary>Runs once; false when it had already run.</summary>
    public static bool Apply(NexusSettings settings)
    {
        var d = settings.Devices;
        if (d.UniversalScreen88Lighting.OpenRgbCardMoved)
        {
            return false;
        }
        var card = UniversalScreen88LedLightingDeviceProvider.RingZoneId;
        var device = UniversalScreen88LedLightingDeviceProvider.HandlerId;
        Move(settings.Lighting.DeviceLayouts, card);
        Move(settings.Lighting.DevicePins, card);
        foreach (var preset in settings.Lighting.LayoutPresets)
        {
            Move(preset.Layouts, card);
            if (preset.DevicePins is { } pins)
            {
                Move(pins, card);
            }
            preset.DisabledDevices = preset.DisabledDevices is { } off ? Renamed(off, card) : null;
            preset.UncontrolledDevices = preset.UncontrolledDevices is { } skipped ? Renamed(skipped, card) : null;
        }
        Move(d.LightingDevicePrefs, card);
        Move(d.DeviceAspectRatios, device);
        Move(d.DeviceLedOverrides, device);
        // The frame writers read these lists without a lock, so new ones are swapped in.
        d.DisabledLightingDevices = Renamed(d.DisabledLightingDevices, card);
        d.UncontrolledLightingDevices = Renamed(d.UncontrolledLightingDevices, card);
        d.UniversalScreen88Lighting.OpenRgbCardMoved = true;
        return true;
    }

    private static void Move<T>(Dictionary<string, T> map, string to)
    {
        if (!map.Remove(OpenRgbId, out var value))
        {
            return;
        }
        map.TryAdd(to, value);
    }

    private static List<string> Renamed(List<string> ids, string to)
    {
        if (!ids.Contains(OpenRgbId))
        {
            return ids;
        }
        var next = new List<string>(ids.Count);
        foreach (var id in ids)
        {
            var renamed = id == OpenRgbId ? to : id;
            if (!next.Contains(renamed))
            {
                next.Add(renamed);
            }
        }
        return next;
    }
}
