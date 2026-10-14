using System;
using System.Linq;
using Nexus.Service.Cooling;
using Nexus.Service.Defaults;
using Nexus.Service.Persistence;

namespace Nexus.Service.Migration;

/// <summary>Maps Nexus 2's Recommended mode onto a Nexus 3 built-in preset, and decides whether Nexus 3's cooling is still a fresh install.</summary>
internal static class Nexus2RecommendedPreset
{
    /// <summary>The Nexus 3 preset for a Nexus 2 easy-mode value, or null for anything unrecognized.</summary>
    public static string? Map(string? selected) => selected switch
    {
        "silent" => "silent",
        "balanced" => "balanced",
        "performance" => "turbo",
        _ => null,
    };

    /// <summary>
    /// True only when nothing in cooling was set up by the user: no curves beyond the unedited built-in preset curves,
    /// no manual duties, offsets, pins, uncontrolled channels, saved presets or global modifier, and the active mode is
    /// still the install default (or already the target, which makes a re-import a no-op).
    /// </summary>
    public static bool IsUntouched(CoolingSettings c, string target)
    {
        var fresh = InstallDefaults.Cooling;
        if (c.ActivePresetId is not null
            || c.Presets.Count > 0
            || c.ManualSpeeds.Count > 0
            || c.CustomManualSpeeds.Count > 0
            || c.FanOffsets.Count > 0
            || c.FanPinOverrides.Count > 0
            || (c.FanLockOverrides?.Count ?? 0) > 0
            || c.UncontrolledFanChannels.Count > 0
            || c.CustomFanCurveAssignments.Count > 0
            || c.HealedChannels.Count > 0
            || c.HealSnapshot is not null
            || c.GlobalSpeedModifier != fresh.GlobalSpeedModifier)
        {
            return false;
        }

        var active = c.ActivePreset;
        if (active != fresh.ActivePreset && active != target)
        {
            return false;
        }

        var seen = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        foreach (var curve in c.Curves)
        {
            if (curve?.Preset is not { } name || !seen.Add(name) || !FanProfiles.IsPresetCurveAtDefaults(curve))
            {
                return false;
            }
            // Channels may sit on the active preset's curve only; under the fresh mode nothing is attached.
            if (curve.Outputs.Count > 0 && (active != name || active == fresh.ActivePreset))
            {
                return false;
            }
        }
        return true;
    }
}
