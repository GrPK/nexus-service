using System;
using Nexus.Service.Lifecycle;
using Nexus.Service.Persistence;

namespace Nexus.Service.Sensors;

/// <summary>
/// Whether anything needs SMART reads. Monitoring and Diagnostics surface drive
/// temperature and health; with both off the only remaining consumer is a fan
/// curve whose input is a drive sensor, which matters while Cooling is on.
/// No LibreHardwareMonitor types, so it compiles on every platform.
/// </summary>
internal static class SmartPollDemand
{
    /// <summary>True when SMART reads run for at least some drive.</summary>
    public static bool IsActive(FeatureGates gates, NexusSettings settings) =>
        gates.Monitoring || gates.Diagnostics || (gates.Cooling && AnyCurveOnDrive(settings, null));

    /// <summary>True when SMART reads run for this drive (an LHM hardware identifier).</summary>
    public static bool IsNeededFor(FeatureGates gates, NexusSettings settings, string hardwareIdentifier) =>
        gates.Monitoring || gates.Diagnostics || (gates.Cooling && AnyCurveOnDrive(settings, hardwareIdentifier));

    private static bool AnyCurveOnDrive(NexusSettings settings, string? hardwareIdentifier)
    {
        foreach (var curve in Cooling.CoolingSnapshots.Curves(settings.Cooling))
        {
            var id = curve.Input.Id;
            if (hardwareIdentifier is null
                ? id.StartsWith("/nvme/", StringComparison.Ordinal) || id.StartsWith("/hdd/", StringComparison.Ordinal) || id.StartsWith("/ssd/", StringComparison.Ordinal)
                : id.StartsWith(hardwareIdentifier + "/", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }
}
