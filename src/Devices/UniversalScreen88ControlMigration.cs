using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Persistence;

namespace Nexus.Service.Devices;

/// <summary>
/// Branch builds gave the Universal Screen 8.8's bezel LEDs a Nexus Control switch of their own
/// (<see cref="RetiredRingId"/>). The 8.8 is one device again, like the OLED Curved: its switch drives
/// the glass and the ring. A ring the user turned on turns the 8.8 on, so it stays lit; the glass is not
/// pushed out of Windows display mode by that, since leaving it is its own setting. The retired id is
/// dropped from both lists either way. Runs on the stored content, not the schema version, so it runs
/// once on any build and never again once the id is gone.
/// </summary>
public static class UniversalScreen88ControlMigration
{
    public const string RetiredRingId = "lianli-screen88-led";

    /// <summary>True when the document changed and should be saved.</summary>
    public static bool Apply(NexusSettings doc)
    {
        if (doc.Devices is not { } devices)
        {
            return false;
        }
        var ringOn = Contains(devices.NexusControlEnabled, RetiredRingId);
        if (!ringOn && !Contains(devices.NexusControlDisabled, RetiredRingId))
        {
            return false;
        }
        // The gate reads these lists without a lock, so new ones are swapped in, added before removed.
        if (ringOn && !Contains(devices.NexusControlEnabled, UniversalScreen88Driver.Id))
        {
            devices.NexusControlEnabled = devices.NexusControlEnabled.Append(UniversalScreen88Driver.Id).ToList();
        }
        devices.NexusControlEnabled = Without(devices.NexusControlEnabled, RetiredRingId);
        devices.NexusControlDisabled = Without(devices.NexusControlDisabled, RetiredRingId);
        if (ringOn)
        {
            devices.NexusControlDisabled = Without(devices.NexusControlDisabled, UniversalScreen88Driver.Id);
        }
        return true;
    }

    private static bool Contains(List<string> ids, string id) => ids.Contains(id, StringComparer.OrdinalIgnoreCase);

    private static List<string> Without(List<string> ids, string id) =>
        ids.Where(x => !string.Equals(x, id, StringComparison.OrdinalIgnoreCase)).ToList();
}
