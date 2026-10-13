using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Platform;
using Nexus.Service.Sensors;
using Nexus.Service.Sockets;

namespace Nexus.Service.Widgets;

/// <summary>
/// Tells the web (apps/changed) when hardware an installed app requires
/// (<see cref="AppPresence"/>) appears or goes, so a device app shows up under
/// Devices without a reload. While an app waits for hardware that only a GPU
/// read can find (a ROG Astral card is probed as the GPUs are read), it reads
/// the GPUs itself now and then, in case nothing else does.
/// </summary>
public sealed class AppPresenceNotifier : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(30);

    private readonly AppRegistry _registry;
    private readonly ISensorProvider _sensors;
    private readonly MultiplexHub _hub;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, bool> _present = new(StringComparer.Ordinal);
    private DateTimeOffset _lastProbe = DateTimeOffset.MinValue;

    public AppPresenceNotifier(AppRegistry registry, ISensorProvider sensors, MultiplexHub hub)
        : this(registry, sensors, hub, TimeProvider.System)
    {
    }

    internal AppPresenceNotifier(AppRegistry registry, ISensorProvider sensors, MultiplexHub hub, TimeProvider time)
    {
        _registry = registry;
        _sensors = sensors;
        _hub = hub;
        _time = time;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckInterval);
        do
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[apps] presence check failed: {ex.Message}");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>One check; returns whether any requirement changed (and apps/changed went out).</summary>
    internal bool Tick()
    {
        var requirements = _registry.All()
            .Select(e => e.Manifest.Requires)
            .Where(r => !string.IsNullOrEmpty(r))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (requirements.Count == 0)
        {
            return false;
        }

        var changed = false;
        foreach (var requirement in requirements)
        {
            var present = AppPresence.Check(requirement, _sensors).Present;
            if (!present && AppPresence.FoundByGpuRead(requirement) && ProbeDue())
            {
                _sensors.GetGpus();
                present = AppPresence.Check(requirement, _sensors).Present;
            }

            var before = _present.TryGetValue(requirement!, out var was) && was;
            _present[requirement!] = present;
            if (present != before)
            {
                changed = true;
                ServiceLog.Info($"[apps] {requirement} {(present ? "detected" : "gone")}");
            }
        }

        if (changed)
        {
            PanelTopics.BroadcastAppsChanged(_hub);
        }
        return changed;
    }

    private bool ProbeDue()
    {
        var now = _time.GetUtcNow();
        if (now - _lastProbe < ProbeInterval)
        {
            return false;
        }
        _lastProbe = now;
        return true;
    }
}
