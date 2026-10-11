using System;
using System.Collections.Generic;
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

    public LcdLockState(SessionLockListener? listener = null)
    {
        listener?.LockChanged += Set;
    }

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
        try { Changed?.Invoke(locked); }
        catch (Exception ex)
        {
            ServiceLog.Info($"[lcd-lock] subscriber failed on {(locked ? "lock" : "unlock")}: {ex.GetType().Name}: {ex.Message}");
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
