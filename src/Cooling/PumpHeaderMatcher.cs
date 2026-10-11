using System;
using System.Collections.Generic;
using Nexus.Service.Models.Cooling;

namespace Nexus.Service.Cooling;

/// <summary>
/// A cooler that reports its pump's speed over its own link while the pump itself runs off a
/// motherboard header: the Tryx Panorama reads the pump's tach, the board drives the pump.
/// </summary>
public interface IPumpTachSource
{
    /// <summary>The pump's latest speed in rpm, or null while the cooler is not reporting one.</summary>
    int? PumpRpm { get; }
}

/// <summary>
/// Finds the motherboard header a cooler's pump is plugged into by matching the header's tach
/// against the speed the cooler reports, and appends <see cref="Suffix"/> to that header's
/// hardware name ("CPU Fan / Pump"), which the composite's name-based pump inference then picks
/// up. A header qualifies once it alone has tracked <see cref="MatchesNeeded"/> successive
/// changes of the reported speed, so a case fan that merely sits near the pump's speed does not;
/// it keeps the label until <see cref="MissesToForget"/> successive readings go elsewhere.
/// </summary>
internal sealed class PumpHeaderMatcher
{
    internal const string Suffix = " / Pump";
    internal const int MatchesNeeded = 3;
    internal const int MissesToForget = 5;

    private readonly object _lock = new();
    private int? _lastPumpRpm;
    private string? _candidate;
    private int _streak;
    private string? _matched;
    private int _misses;

    public void Label(List<FanChannel> channels, int? pumpRpm)
    {
        lock (_lock)
        {
            // Only a changed reading is new evidence: the cooler repeats its last value between samples.
            if (pumpRpm is { } rpm && rpm > 0 && rpm != _lastPumpRpm)
            {
                _lastPumpRpm = rpm;
                Observe(channels, rpm);
            }
            if (_matched is null) return;
            foreach (var ch in channels)
            {
                if (ch.Id == _matched && !ch.Name.EndsWith(Suffix, StringComparison.Ordinal))
                {
                    ch.Name += Suffix;
                }
            }
        }
    }

    private void Observe(List<FanChannel> channels, int pumpRpm)
    {
        string? match = null;
        var matches = 0;
        foreach (var ch in channels)
        {
            if (IsHeader(ch) && Tracks(ch.Rpm, pumpRpm))
            {
                match = ch.Id;
                matches++;
            }
        }
        if (matches != 1) match = null;

        if (_matched is not null)
        {
            _misses = match == _matched ? 0 : _misses + 1;
            if (_misses >= MissesToForget)
            {
                _matched = null;
                _misses = 0;
            }
            return;
        }
        _streak = match is null ? 0 : match == _candidate ? _streak + 1 : 1;
        _candidate = match;
        if (_streak >= MatchesNeeded)
        {
            _matched = match;
            _streak = 0;
        }
    }

    // A spinning motherboard header (no DeviceId) that is not already a pump; the matched one
    // stays a candidate after its label makes it one.
    private bool IsHeader(FanChannel ch) =>
        string.IsNullOrEmpty(ch.DeviceId) && !ch.ReadOnly && ch.Rpm > 0
        && (ch.Id == _matched
            || (ch.Kind == FanKinds.Fan && !ch.Name.Contains("pump", StringComparison.OrdinalIgnoreCase)));

    // The cooler reports in 30 rpm steps on its own sampling clock, so while the speed moves the
    // header leads or lags it by a step or two: 5 % slack, at least 100 rpm.
    private static bool Tracks(int headerRpm, int pumpRpm) =>
        Math.Abs(headerRpm - pumpRpm) <= Math.Max(100, pumpRpm / 20);
}
