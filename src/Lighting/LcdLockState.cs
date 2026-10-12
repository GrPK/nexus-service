using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Platform;

namespace Nexus.Service.Lighting;

/// <summary>
/// The session lock as the cooler / AIO LCD drivers see it: a flag, a lock
/// counter, and one event. Fed by <see cref="SessionLockListener.LockChanged"/>,
/// so it carries the same rule: transitions only, never derived at startup.
/// Tests drive <see cref="Set"/> directly.
/// </summary>
public sealed class LcdLockState
{
    private readonly object _gate = new();
    private bool _locked;
    private int _epoch;

    /// <summary>Raised after <see cref="IsLocked"/> flips. True = locked.</summary>
    public event Action<bool>? Changed;

    public bool IsLocked
    {
        get { lock (_gate) { return _locked; } }
    }

    /// <summary>Increments on every lock, so a tracker can tell one lock from the next.</summary>
    public int Epoch
    {
        get { lock (_gate) { return _epoch; } }
    }

    public void Set(bool locked)
    {
        lock (_gate)
        {
            if (_locked == locked)
            {
                return;
            }
            _locked = locked;
            if (locked)
            {
                _epoch++;
            }
        }
        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }
        foreach (var handler in handlers.GetInvocationList())
        {
            try { ((Action<bool>)handler)(locked); }
            catch (Exception ex)
            {
                ServiceLog.Info($"[lcd-lock] subscriber failed on {(locked ? "lock" : "unlock")}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}

/// <summary>
/// Per-driver record of which devices the lock put to sleep. A device is decided
/// once per lock, the first time it is asked about: that is the lock itself for a
/// connected device, or its attach for one that arrives while locked. The decision
/// then holds for the whole lock, so a setting flipped mid-lock neither strands a
/// slept screen dark nor wakes it early. Unlock drains them.
/// </summary>
public sealed class LcdLockSleepSet
{
    private readonly LcdLockState _state;
    private readonly object _gate = new();
    private readonly Dictionary<string, bool> _decided = new(StringComparer.OrdinalIgnoreCase);
    private int _epoch = -1;

    public LcdLockSleepSet(LcdLockState state)
    {
        _state = state;
    }

    /// <summary>True while <paramref name="key"/> is held dark by the lock. <paramref name="settingOn"/> is read only for the first ask of a lock.</summary>
    public bool IsSleeping(string key, Func<bool> settingOn)
    {
        if (!_state.IsLocked)
        {
            return false;
        }
        var epoch = _state.Epoch;
        lock (_gate)
        {
            if (_epoch != epoch)
            {
                _decided.Clear();
                _epoch = epoch;
            }
            if (_decided.TryGetValue(key, out var slept))
            {
                return slept;
            }
        }
        bool on;
        try { on = settingOn(); }
        catch { on = true; }
        lock (_gate)
        {
            if (_epoch != epoch)
            {
                return false;
            }
            return _decided.TryAdd(key, on) ? on : _decided[key];
        }
    }

    /// <summary>Returns the keys this lock put to sleep and forgets them. Call on unlock: it names exactly what to wake, whatever the setting reads by then.</summary>
    public List<string> Drain()
    {
        lock (_gate)
        {
            var list = new List<string>();
            foreach (var pair in _decided)
            {
                if (pair.Value)
                {
                    list.Add(pair.Key);
                }
            }
            _decided.Clear();
            return list;
        }
    }
}

/// <summary>
/// Feeds <see cref="LcdLockState"/> from <see cref="SessionLockListener"/>. A hosted service
/// rather than a constructor dependency: the drivers that read the state are built inside
/// the listener's own dependency chain, so the state itself must have no dependencies.
/// </summary>
public sealed class LcdLockBridge : IHostedService
{
    private readonly SessionLockListener _listener;
    private readonly LcdLockState _state;

    public LcdLockBridge(SessionLockListener listener, LcdLockState state)
    {
        _listener = listener;
        _state = state;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _listener.LockChanged += _state.Set;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _listener.LockChanged -= _state.Set;
        return Task.CompletedTask;
    }
}

/// <summary>
/// One stepped brightness ramp between two levels, read off a millisecond clock so the same
/// answer comes back however often it is asked. Nothing here sends or persists anything: a
/// device samples <see cref="LevelAt"/> and writes the result, and the level a lock fade
/// returns to is whatever the caller captured before the first step.
/// </summary>
public sealed class LcdFade
{
    /// <summary>How often a driver loop samples a ramp. Cheap devices take one step per sample.</summary>
    public static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(40);

    /// <summary>Exponent of the display response the ramp is shaped against: equal steps in perceived lightness, not in percent.</summary>
    private const double Gamma = 2.2;

    private readonly long _startMs;
    private readonly long _durationMs;
    private readonly int _steps;
    private readonly double _fromLightness;
    private readonly double _toLightness;

    /// <param name="steps">Distinct pre-final levels; null takes one step per <see cref="SampleInterval"/> of <paramref name="duration"/>.</param>
    public LcdFade(int from, int to, TimeSpan duration, long startMs, int? steps = null)
    {
        From = from;
        To = to;
        _startMs = startMs;
        _durationMs = Math.Max(1, (long)duration.TotalMilliseconds);
        _steps = Math.Max(1, steps ?? (int)Math.Round(duration / SampleInterval));
        _fromLightness = Lightness(from);
        _toLightness = Lightness(to);
    }

    public int From { get; }
    public int To { get; }
    public bool Down => To < From;

    public bool IsDoneAt(long nowMs) => nowMs - _startMs >= _durationMs;

    // Brightness percent to perceived lightness and back. Interpolating in lightness makes every
    // step look the same size: percent steps linger at the top and lurch at the bottom.
    private static double Lightness(int percent) => Math.Pow(Math.Clamp(percent, 0, 100) / 100.0, 1.0 / Gamma);

    private static int Percent(double lightness) => (int)Math.Round(100.0 * Math.Pow(Math.Clamp(lightness, 0.0, 1.0), Gamma));

    /// <summary>The level at <paramref name="nowMs"/>: exactly <see cref="From"/> at the start, exactly <see cref="To"/> once done, and monotonic between.</summary>
    public int LevelAt(long nowMs)
    {
        if (IsDoneAt(nowMs))
        {
            return To;
        }
        var elapsed = Math.Max(0, nowMs - _startMs);
        var step = (int)Math.Min(_steps - 1, elapsed * _steps / _durationMs);
        if (step <= 0)
        {
            return From;
        }
        var level = Percent(_fromLightness + (_toLightness - _fromLightness) * step / _steps);
        return Math.Clamp(level, Math.Min(From, To), Math.Max(From, To));
    }
}

/// <summary>
/// Drives a sampling callback on one background loop until it reports no more work. Starting
/// it while it runs does nothing, so ramps never stack up: a new or reversed ramp is just new
/// state for the same loop to read. Tests switch the loop off and call the callback themselves.
/// </summary>
public sealed class LcdFadeRunner
{
    private readonly Func<bool> _step;
    private readonly TimeSpan _interval;
    private readonly Action? _onFault;
    private int _running;

    /// <param name="onFault">Drops the ramp when a step throws, so it does not sit half done until the next start.</param>
    public LcdFadeRunner(Func<bool> step, TimeSpan? interval = null, Action? onFault = null)
    {
        _step = step;
        _interval = interval ?? LcdFade.SampleInterval;
        _onFault = onFault;
    }

    /// <summary>False in tests, which step by hand.</summary>
    public bool AutoRun { get; set; } = true;

    public void Kick()
    {
        if (!AutoRun || Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    while (_step())
                    {
                        await Task.Delay(_interval).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    ServiceLog.Info($"[lcd-lock] fade step failed: {ex.GetType().Name}: {ex.Message}");
                    try { _onFault?.Invoke(); } catch { }
                }
                Volatile.Write(ref _running, 0);
                // Work that arrived between the last sample and the flag reset.
                bool more;
                try { more = _step(); }
                catch { more = false; }
                if (!more || Interlocked.Exchange(ref _running, 1) == 1)
                {
                    return;
                }
            }
        });
    }
}
