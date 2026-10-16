using System;
using System.Collections.Generic;
using System.Threading;

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>One queued GL job: the work, its lane, and when it was queued.</summary>
internal readonly record struct GlJob(Action Work, bool Background, long EnqueuedTicks);

/// <summary>
/// Two-lane work queue for the single GL thread. Live frames (the engine's
/// main canvas, pins, look-ahead) go in the live lane and are always taken
/// first; thumbnails, validation and benchmark kernels go in the background
/// lane. A backlog of background jobs therefore delays a live frame by at most
/// the one job already running. Completion mirrors BlockingCollection: after
/// <see cref="CompleteAdding"/> a later Add throws InvalidOperationException
/// and Take drains what is queued, then throws.
/// </summary>
internal sealed class GlWorkQueue
{
    private readonly object _gate = new();
    private readonly Queue<GlJob> _live = new();
    private readonly Queue<GlJob> _background = new();
    private bool _completed;

    public void Add(Action work, bool background)
    {
        lock (_gate)
        {
            if (_completed)
            {
                throw new InvalidOperationException("queue completed");
            }
            (background ? _background : _live).Enqueue(new GlJob(work, background, System.Diagnostics.Stopwatch.GetTimestamp()));
            Monitor.Pulse(_gate);
        }
    }

    /// <summary>Block for the next job, live lane first. Throws InvalidOperationException once completed and empty.</summary>
    public GlJob Take()
    {
        lock (_gate)
        {
            while (true)
            {
                if (_live.Count > 0)
                {
                    return _live.Dequeue();
                }
                if (_background.Count > 0)
                {
                    return _background.Dequeue();
                }
                if (_completed)
                {
                    throw new InvalidOperationException("queue completed");
                }
                Monitor.Wait(_gate);
            }
        }
    }

    public bool TryTake(out GlJob job)
    {
        lock (_gate)
        {
            if (_live.Count > 0)
            {
                job = _live.Dequeue();
                return true;
            }
            if (_background.Count > 0)
            {
                job = _background.Dequeue();
                return true;
            }
            job = default;
            return false;
        }
    }

    public void CompleteAdding()
    {
        lock (_gate)
        {
            _completed = true;
            Monitor.PulseAll(_gate);
        }
    }
}

/// <summary>
/// Accumulates queue-wait and run time per lane and reports a one-line window
/// summary, so a backlog of background jobs shows up in nexus-gpu.log with the
/// worst live-frame wait it caused.
/// </summary>
internal sealed class GlQueueStats
{
    private readonly long _windowTicks;
    private long _windowStart;
    private int _liveJobs, _bgJobs;
    private double _liveWaitMax, _bgWaitMax, _bgRunTotal, _bgRunMax;

    public GlQueueStats(TimeSpan window)
    {
        _windowTicks = (long)(window.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
        _windowStart = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    /// <summary>Record a finished job; returns a summary line when a window that saw background work closed, else null.</summary>
    public string? Record(in GlJob job, long startTicks, long endTicks)
    {
        var freq = (double)System.Diagnostics.Stopwatch.Frequency;
        var waitMs = (startTicks - job.EnqueuedTicks) * 1000.0 / freq;
        var runMs = (endTicks - startTicks) * 1000.0 / freq;
        if (job.Background)
        {
            _bgJobs++;
            _bgWaitMax = Math.Max(_bgWaitMax, waitMs);
            _bgRunTotal += runMs;
            _bgRunMax = Math.Max(_bgRunMax, runMs);
        }
        else
        {
            _liveJobs++;
            _liveWaitMax = Math.Max(_liveWaitMax, waitMs);
        }
        if (endTicks - _windowStart < _windowTicks)
        {
            return null;
        }
        string? line = null;
        if (_bgJobs > 0)
        {
            line = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"[gpu] queue window: live jobs {_liveJobs} max wait {_liveWaitMax:0.0}ms; background jobs {_bgJobs} max wait {_bgWaitMax:0}ms, run total {_bgRunTotal:0}ms max {_bgRunMax:0.0}ms");
        }
        _windowStart = endTicks;
        _liveJobs = _bgJobs = 0;
        _liveWaitMax = _bgWaitMax = _bgRunTotal = _bgRunMax = 0;
        return line;
    }
}
