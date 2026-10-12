using System;
using System.Collections.Generic;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// Folds the pre-pin per-device state into pins. Idempotent and safe to call on
/// any settings object: every entry point that loads or replaces lighting or
/// cooling settings (settings.json load, profile read/apply/import, preset
/// activate) calls it, so an older document never reaches the engine in the old
/// shape.
///
/// Lighting: every Static look, locked or not, becomes a static pin; every
/// animation lock becomes an animate pin, which wins when a device had both.
/// An existing pin is never overwritten. Cooling: <c>FanLockOverrides</c> moves
/// into <c>FanPinOverrides</c>.
/// </summary>
public static class PinMigration
{
    /// <summary>True when the document changed.</summary>
    public static bool Apply(NexusSettings? doc)
    {
        if (doc is null) return false;
        var changed = Apply(doc.Lighting);
        changed |= Apply(doc.Cooling);
        return changed;
    }

    public static bool Apply(LightingSettings? lighting)
    {
        if (lighting is null) return false;
        var changed = false;
        lighting.DevicePins ??= new Dictionary<string, DevicePin>(StringComparer.Ordinal);
        if (lighting.StaticDeviceLooks is not null || lighting.AnimateDeviceLocks is not null)
        {
            Fold(lighting.DevicePins, lighting.StaticDeviceLooks, lighting.AnimateDeviceLocks);
            lighting.StaticDeviceLooks = null;
            lighting.AnimateDeviceLocks = null;
            changed = true;
        }
        if (lighting.LayoutPresets is { } presets)
        {
            foreach (var preset in presets)
            {
                if (preset is null) continue;
                if (preset.StaticDeviceLooks is null && preset.AnimateDeviceLocks is null) continue;
                // A pre-pin preset that captured either field replaces the live
                // pins on activate, as it replaced the live static looks.
                preset.DevicePins ??= new Dictionary<string, DevicePin>(StringComparer.Ordinal);
                Fold(preset.DevicePins, preset.StaticDeviceLooks, preset.AnimateDeviceLocks);
                preset.StaticDeviceLooks = null;
                preset.AnimateDeviceLocks = null;
                changed = true;
            }
        }
        return changed;
    }

    public static bool Apply(CoolingSettings? cooling)
    {
        if (cooling?.FanLockOverrides is not { } legacy) return false;
        cooling.FanPinOverrides ??= new Dictionary<string, bool>();
        foreach (var (id, pinned) in legacy)
        {
            cooling.FanPinOverrides.TryAdd(id, pinned);
        }
        cooling.FanLockOverrides = null;
        return true;
    }

    private static void Fold(
        Dictionary<string, DevicePin> pins,
        Dictionary<string, StaticDeviceLook>? looks,
        Dictionary<string, string>? locks)
    {
        var fromLegacy = new Dictionary<string, DevicePin>(StringComparer.Ordinal);
        if (looks is not null)
        {
            foreach (var (id, look) in looks)
            {
                if (string.IsNullOrEmpty(id) || look is null || string.IsNullOrEmpty(look.Effect)) continue;
                fromLegacy[id] = new DevicePin
                {
                    Kind = DevicePinKinds.Static,
                    Effect = look.Effect,
                    Color = string.IsNullOrEmpty(look.Color) ? null : look.Color,
                    Intensity = look.Intensity,
                    Hue = look.Hue,
                    Colorize = look.Colorize,
                    Saturation = look.Saturation,
                    Contrast = look.Contrast,
                    Params = look.Params is { Count: > 0 } ? new Dictionary<string, float>(look.Params) : null,
                    Slot = look.Slot,
                };
            }
        }
        if (locks is not null)
        {
            foreach (var (id, effect) in locks)
            {
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(effect)) continue;
                fromLegacy[id] = new DevicePin { Kind = DevicePinKinds.Animate, Effect = effect };
            }
        }
        foreach (var (id, pin) in fromLegacy)
        {
            pins.TryAdd(id, pin);
        }
    }
}
