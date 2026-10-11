using System;
using System.Collections.Generic;
using System.Threading;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// Per-device animation locks: a device pinned to one Animate shader that it
/// keeps running in every mode until unlocked. A lock is only the assignment of
/// shader to device; the shader's look stays the global per-effect state, so a
/// slider tweak reaches the locked device like it would the active effect.
///
/// Same lifecycle as <see cref="StaticDeviceEffectTracker"/>: a shared singleton
/// hydrated from the store and re-hydrated on every store change, so a profile
/// switch or preset activate replaces the locks.
/// </summary>
public sealed class AnimateDeviceLockTracker
{
    private readonly object _lock = new();
    private readonly IConfigStore? _store;
    // Replaced wholesale, never mutated: the engine and routes read it lock-free.
    private volatile Dictionary<string, string> _locks = new(StringComparer.Ordinal);
    private int _version;
    private int _changeStamp;

    public AnimateDeviceLockTracker(IConfigStore? store = null)
    {
        _store = store;
        if (store is null) return;
        Hydrate();
        store.OnChanged += OnStoreChanged;
    }

    /// <summary>Bumped when the set of locks changes.</summary>
    public int Version => Volatile.Read(ref _version);

    /// <summary>Bumped on every store change, locks or not: a locked effect's look
    /// lives in the animate settings, so the engine re-resolves it on this.</summary>
    public int ChangeStamp => Volatile.Read(ref _changeStamp);

    /// <summary>True for an Animate shader key: one with a template bundle that is
    /// not a Static fill or pattern. Keys are lower case, as StartAnimate stores them.</summary>
    public static bool IsAnimateEffect(string? key) =>
        !string.IsNullOrEmpty(key)
        && AnimateTemplateDefaults.All.ContainsKey(key)
        && !StaticEffectCatalog.Contains(key);

    public bool Any => _locks.Count > 0;

    /// <summary>Current device id to effect key map. Treat as read-only.</summary>
    public IReadOnlyDictionary<string, string> Snapshot() => _locks;

    public bool TryGet(string id, out string effect)
    {
        effect = "";
        if (string.IsNullOrEmpty(id)) return false;
        return _locks.TryGetValue(id, out effect!);
    }

    public bool IsLocked(string id) => !string.IsNullOrEmpty(id) && _locks.ContainsKey(id);

    /// <summary>Locks the device onto the effect, replacing any previous animation lock.</summary>
    public bool Set(string id, string effect)
    {
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(effect)) return false;
        lock (_lock)
        {
            if (_locks.TryGetValue(id, out var current) && string.Equals(current, effect, StringComparison.Ordinal))
            {
                return true;
            }
            var next = new Dictionary<string, string>(_locks, StringComparer.Ordinal) { [id] = effect };
            _locks = next;
            Interlocked.Increment(ref _version);
        }
        _store?.Update(s => s.Lighting.AnimateDeviceLocks[id] = effect);
        return true;
    }

    /// <summary>Releases the device back to the shared canvas.</summary>
    public void Clear(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        lock (_lock)
        {
            if (_locks.ContainsKey(id))
            {
                var next = new Dictionary<string, string>(_locks, StringComparer.Ordinal);
                next.Remove(id);
                _locks = next;
                Interlocked.Increment(ref _version);
            }
        }
        _store?.Update(s => s.Lighting.AnimateDeviceLocks.Remove(id));
    }

    private void OnStoreChanged()
    {
        Interlocked.Increment(ref _changeStamp);
        Hydrate();
    }

    /// <summary>Rebuild from the store, only when the content differs: Set/Clear
    /// write through the store and re-enter here.</summary>
    private void Hydrate()
    {
        var store = _store;
        if (store is null) return;
        var stored = store.Load().Lighting.AnimateDeviceLocks;
        var rebuilt = new Dictionary<string, string>(StringComparer.Ordinal);
        if (stored is not null)
        {
            foreach (var (id, effect) in stored)
            {
                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(effect)) rebuilt[id] = effect;
            }
        }
        lock (_lock)
        {
            var mine = _locks;
            if (mine.Count == rebuilt.Count)
            {
                var same = true;
                foreach (var (id, effect) in rebuilt)
                {
                    if (!mine.TryGetValue(id, out var m) || !string.Equals(m, effect, StringComparison.Ordinal))
                    {
                        same = false;
                        break;
                    }
                }
                if (same) return;
            }
            _locks = rebuilt;
            Interlocked.Increment(ref _version);
        }
    }
}
