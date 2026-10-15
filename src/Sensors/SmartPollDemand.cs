using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nexus.Service.Lifecycle;
using Nexus.Service.Persistence;

namespace Nexus.Service.Sensors;

/// <summary>
/// What needs SMART reads, captured once so a walk decides every drive from one
/// consistent view. Monitoring and Diagnostics surface drive temperature and
/// health; with both off the only remaining consumer is a fan curve whose input
/// is a drive sensor, which matters while Cooling is on. No LibreHardwareMonitor
/// types, so it compiles on every platform.
/// </summary>
internal sealed class SmartPollDemand
{
    private SmartPollDemand(bool surfaces, bool live, bool cooling, IReadOnlyList<string> curveInputs)
    {
        Surfaces = surfaces || live;
        CoolingOn = cooling;
        CurveInputs = curveInputs;
    }

    /// <summary>Monitoring or Diagnostics is on, or something is showing live sensors.</summary>
    public bool Surfaces { get; }

    public bool CoolingOn { get; }

    /// <summary>Sensor ids of every curve input.</summary>
    public IReadOnlyList<string> CurveInputs { get; }

    /// <summary>
    /// <paramref name="liveConsumers"/> is true while a dashboard, panel, kiosk, overlay or SDK app
    /// is subscribed to live sensor frames, or a poll-style reader (Deck tile, MCP) read SMART data recently.
    /// </summary>
    public static SmartPollDemand Capture(FeatureGates gates, NexusSettings settings, bool liveConsumers = false) =>
        new(
            gates.Monitoring || gates.Diagnostics,
            liveConsumers,
            gates.Cooling,
            Nexus.Service.Cooling.CoolingSnapshots.Curves(settings.Cooling).Select(c => c.Input.Id).ToList());

    /// <summary>True when SMART reads run for this drive (an LHM hardware identifier).</summary>
    public bool IsNeededFor(string hardwareIdentifier) =>
        Surfaces || (CoolingOn && CurveInputs.Any(id => id.StartsWith(hardwareIdentifier + "/", StringComparison.Ordinal)));

    /// <summary>True when SMART reads run for at least one of the drives the walk enumerates.</summary>
    public bool IsActive(IEnumerable<string> driveIdentifiers) =>
        Surfaces || driveIdentifiers.Any(IsNeededFor);

    /// <summary>
    /// The skip count for one drive's walk. A drive nothing needs is never read, Force
    /// included, and forgotten so the first walk after it is needed reads it at once.
    /// A user's Never still wins over the seed and over Force.
    /// </summary>
    public static uint Cycles(bool needed, ISet<string> seeded, string identifier, Func<int> configuredSeconds, bool force)
    {
        if (!needed)
        {
            seeded.Remove(identifier);
            return SmartPollPolicy.ToCycleCount(SmartPollPolicy.NeverSeconds);
        }

        var seconds = configuredSeconds();
        if (seconds <= SmartPollPolicy.NeverSeconds) return SmartPollPolicy.ToCycleCount(seconds);
        if (seeded.Add(identifier) || force) return 1;
        return SmartPollPolicy.ToCycleCount(seconds);
    }
}

/// <summary>Live-consumer signals for <see cref="SmartPollDemand"/>.</summary>
internal static class SmartLiveConsumers
{
    // The composite monitoring frame and the storage frame are the topics whose broadcast reads SMART sensors.
    private static readonly string[] Topics = { "monitoring", "storage" };

    // Poll-style readers (Deck tiles, overlays, MCP) leave no subscription, so a read keeps the demand alive for a while.
    private const long ReaderWindowMs = 15_000;
    private static long _lastReadMs = long.MinValue / 2;

    /// <summary>Called by poll-style readers of SMART sensors.</summary>
    public static void NoteRead() => Volatile.Write(ref _lastReadMs, Environment.TickCount64);

    internal static void Forget() => Volatile.Write(ref _lastReadMs, long.MinValue / 2);

    public static bool Any(Nexus.Service.Sockets.MultiplexHub? hub)
    {
        if (Environment.TickCount64 - Volatile.Read(ref _lastReadMs) < ReaderWindowMs) return true;
        return hub is not null && Topics.Any(hub.TopicHasSubscribers);
    }
}
