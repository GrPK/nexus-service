using System;
using Nexus.Service.Models.Widgets;
using Nexus.Service.Sensors;

namespace Nexus.Service.Widgets;

/// <summary>
/// Apps made for one piece of hardware (manifest <c>requires</c>). The web lists
/// every <c>category: "device"</c> app under Devices as connected, so the
/// service decides: while the hardware is not detected the listing carries no
/// category and the app stays an ordinary, unlisted app; once it is, the
/// listing keeps the manifest's category and takes the device's name. Hiding
/// the app outright instead would make the web drop its placed widgets, since
/// at boot the hardware is only detected after the first sensor reads.
/// </summary>
public static class AppPresence
{
    /// <summary>A ROG Astral card with a G-sensor (Level Sense).</summary>
    public const string LevelSense = "gpu.levelsense";


    /// <summary>
    /// Whether the hardware an app requires is present, and its name; unknown
    /// requirements are never present.
    /// </summary>
    public static (bool Present, string? DeviceName) Check(string? requires, ISensorProvider sensors)
    {
        if (!string.Equals(requires, LevelSense, StringComparison.Ordinal))
        {
            return (false, null);
        }
        var states = sensors.GetGpuLevelSenseStates();
        return states.Count == 0 ? (false, null) : (true, ShortName(states[0].Name));
    }

    /// <summary>Applies <see cref="Check"/> to an app's listing; a no-op for apps that require nothing.</summary>
    public static void Apply(AppInstalledListing listing, AppManifest manifest, ISensorProvider sensors)
    {
        if (string.IsNullOrEmpty(manifest.Requires))
        {
            return;
        }
        var (present, deviceName) = Check(manifest.Requires, sensors);
        listing.Category = present ? manifest.Category : null;
        if (present && !string.IsNullOrEmpty(deviceName))
        {
            listing.Name = deviceName;
        }
    }

    // "ASUS ROG Astral RTX 5090" reads as "ROG Astral RTX 5090" in the Devices list.
    private static string ShortName(string name) =>
        name.StartsWith("ASUS ", StringComparison.Ordinal) ? name["ASUS ".Length..] : name;
}
