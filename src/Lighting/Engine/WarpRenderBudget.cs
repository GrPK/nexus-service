using System;

namespace Nexus.Service.Lighting.Engine;

/// <summary>
/// Bounds the CPU a shader costs on the WARP software rasterizer. The engine keeps ticking and publishing at its
/// normal rate; this only spaces the shader renders out so their smoothed cost stays under <see cref="CpuFraction"/>
/// of one core, never slower than <see cref="FloorFps"/>. One instance per rendered source, engine thread only.
/// </summary>
internal sealed class WarpRenderBudget
{
    /// <summary>Share of one core a single source's shader work may use.</summary>
    public const double CpuFraction = 0.05;

    /// <summary>Slowest render rate the budget will drop to, however costly the shader.</summary>
    public const int FloorFps = 10;

    public const int MaxIntervalMs = 1000 / FloorFps;

    /// <summary>Weight of the newest cost in the smoothed estimate.</summary>
    public const double Smoothing = 0.25;

    private object? _source;
    private double _costMs;
    private bool _hasCost;
    private long _lastRenderMs;

    /// <summary>The spacing between renders that holds <paramref name="costMs"/> inside the budget.</summary>
    public static int IntervalMs(double costMs, int frameIntervalMs)
    {
        var floor = Math.Max(1, frameIntervalMs);
        var wanted = costMs <= 0 ? floor : (int)Math.Min(int.MaxValue, Math.Ceiling(costMs / CpuFraction));
        return Math.Clamp(wanted, floor, Math.Max(floor, MaxIntervalMs));
    }

    /// <summary>Folds one measured render into the smoothed cost; the first sample seeds it.</summary>
    public static double Smooth(double previousMs, bool hasPrevious, double sampleMs) =>
        hasPrevious ? previousMs + (sampleMs - previousMs) * Smoothing : sampleMs;

    /// <summary>True when the source should render this tick. A new source and an unmeasured one always render.</summary>
    public bool ShouldRender(object source, long nowMs, int frameIntervalMs)
    {
        if (!ReferenceEquals(source, _source))
        {
            _source = source;
            _hasCost = false;
            return true;
        }
        if (!_hasCost)
        {
            return true;
        }
        // Ticks land on a grid, so a render that is nearly due goes on this tick rather than a whole tick later.
        return nowMs - _lastRenderMs + Math.Max(1, frameIntervalMs) / 2 >= IntervalMs(_costMs, frameIntervalMs);
    }

    public void Record(long nowMs, double costMs)
    {
        _costMs = Smooth(_costMs, _hasCost, costMs);
        _hasCost = true;
        _lastRenderMs = nowMs;
    }
}
