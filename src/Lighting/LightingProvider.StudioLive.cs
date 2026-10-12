using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Models.Lighting;
using Nexus.Service.Platform;
using Nexus.Service.Sockets;

namespace Nexus.Service.Lighting;

/// <summary>
/// Shader Studio live preview. A session swaps the running effect for the Studio's fragment
/// and puts it back on stop, lease lapse or a conflicting change. It drives the devices the
/// current mode already drives; LEDs-off and uncontrolled devices stay as they are. It never
/// writes a setting: the effect is held in the engine, so a restart mid-session boots the
/// saved setup.
/// </summary>
public sealed partial class LightingProvider
{
    /// <summary>Key the live fragment runs under. The store's id rule has no hyphen, so no saved effect can collide with it.</summary>
    public const string StudioLiveKey = ShaderLibrary.CustomPrefix + StudioLiveId;
    private const string StudioLiveId = "studio-live";

    /// <summary>No POST for this long and the service restores on its own.</summary>
    public const int StudioLeaseMs = 15_000;

    private readonly MultiplexHub? _panelHub;
    private readonly object _studioLock = new();
    private volatile StudioSession? _studio;
    private Timer? _studioTimer;

    // Set only on the thread that is itself driving the engine, so its own effect change is not read as a conflict
    // while another thread's change still is.
    [ThreadStatic]
    private static bool t_studioChanging;

    /// <summary>Clock for the lease; tests replace it.</summary>
    internal Func<long> StudioClockMs { get; set; } = () => Environment.TickCount64;

    public bool StudioLiveActive => _studio is not null;

    private enum StudioEnd
    {
        /// <summary>Put the replaced lighting back, if the engine still runs the preview.</summary>
        Restore,
        /// <summary>Someone else owns the engine now: release only what the session owns.</summary>
        Abandon,
        /// <summary>End bookkeeping only; the caller restores later.</summary>
        Defer,
    }

    private sealed class StudioSession
    {
        public required string Id { get; set; }
        public required ShaderEffect Live { get; set; }
        public required string Frag { get; set; }
        public IEffect? PrevEffect { get; set; }
        public required bool PrevPaused { get; init; }
        public required bool PrevFrozen { get; init; }
        public required bool PrevFullFrame { get; init; }
        public required bool PrevStaticEnabled { get; init; }
        public required bool RgbWasActive { get; init; }
        public required string ReportedSync { get; init; }
        public required string PersistedSync { get; init; }
        public long LeaseDeadline { get; set; }
    }

    public StudioLiveResult StartStudioLive(StudioLiveBody body)
    {
        StudioLiveResult result;
        lock (_studioLock)
        {
            result = StartStudioLiveLocked(body);
        }
        return result;
    }

    private bool EngineRunsLive(StudioSession s) => ReferenceEquals(_engine.CurrentEffect, s.Live);

    private StudioLiveResult StartStudioLiveLocked(StudioLiveBody body)
    {
        if (!_gates.Lighting)
        {
            return Fail("lighting is off");
        }
        var frag = body.Frag ?? "";
        var speed = (float)(body.Speed / 50.0);
        var intensity = body.Intensity > 0 ? body.Intensity : 1f;
        Dictionary<string, float>? extras = body.Params is { Count: > 0 } ? new Dictionary<string, float>(body.Params) : null;

        if (body.SessionId is not null)
        {
            // A renewal. Anything but the running session's own id means the session it belonged to is over.
            if (_studio is not { } renewing || !string.Equals(renewing.Id, body.SessionId, StringComparison.Ordinal))
            {
                return Ended();
            }
            if (!EngineRunsLive(renewing))
            {
                EndStudioLocked(StudioEnd.Abandon);
                return Ended();
            }
            var update = UpdateSession(renewing, body, frag, speed, intensity, extras);
            if (update is null)
            {
                EndStudioLocked(StudioEnd.Abandon);
                return Ended();
            }
            return update.Value;
        }

        if (_studio is { } running)
        {
            // Another tab opening its own session: keep the snapshot, take over the preview, retire the old id.
            if (EngineRunsLive(running))
            {
                var update = UpdateSession(running, body, frag, speed, intensity, extras);
                if (update is { Ok: true })
                {
                    running.Id = NewSessionId();
                    return new StudioLiveResult(StudioLiveOutcome.Ok, "Ok", running.Id);
                }
                if (update is not null)
                {
                    return update.Value;
                }
            }
            EndStudioLocked(StudioEnd.Abandon);
        }

        if (_engine.Blackout)
        {
            return Fail("lighting is held dark for sleep or lock");
        }

        // An ended session's preview can still be running while its restore is pending (a handoff or a queued
        // restore). Its snapshot is the user's lighting and is adopted; a dead preview with none snapshots as Off.
        var adopt = _studioHandoff is { } pending && EngineRunsLive(pending) ? pending : null;
        var deadPreview = adopt is null && _engine.CurrentEffect is ShaderEffect { Name: StudioLiveKey };
        // Everything below the snapshot reads is read before the first change.
        var persistedSync = _store.Load().Lighting.Sync;
        var reportedSync = GetSync();
        var reportedPaused = adopt?.PrevPaused ?? (!deadPreview && _engine.Paused);
        var prevFrozen = adopt?.PrevFrozen ?? (!deadPreview && _engine.Frozen);
        var prevFullFrame = adopt?.PrevFullFrame ?? (!deadPreview && _engine.FullFrameSampling);
        var prevStaticEnabled = adopt?.PrevStaticEnabled ?? (!deadPreview && (_engine.StaticEffects?.Enabled ?? false));
        var rgbWasActive = adopt?.RgbWasActive ?? _rgb?.IsActive ?? false;

        ShaderLibrary.RegisterCustom(StudioLiveId, frag);
        ShaderEffect live;
        try
        {
            live = MakeShader(StudioLiveKey, frag, speed, intensity, body.Hue, body.Colorize, body.Saturation, body.Contrast, extras);
        }
        catch (Exception ex)
        {
            ShaderLibrary.UnregisterCustom(StudioLiveId);
            return Fail(ex.Message);
        }

        // The slot is cleared only now that the new preview exists; a queued restore then finds it empty and does nothing.
        if (adopt is not null)
        {
            _studioHandoff = null;
        }
        var started = new StudioSession
        {
            Id = NewSessionId(),
            Live = live,
            Frag = frag,
            PrevEffect = null,
            PrevPaused = reportedPaused,
            PrevFrozen = prevFrozen,
            PrevFullFrame = prevFullFrame,
            PrevStaticEnabled = prevStaticEnabled,
            RgbWasActive = rgbWasActive,
            ReportedSync = reportedSync,
            PersistedSync = persistedSync,
            LeaseDeadline = StudioClockMs() + StudioLeaseMs,
        };
        // Published before the engine changes, so a change another client lands right after ours is seen as a conflict.
        _studio = started;
        t_studioChanging = true;
        try
        {
            EnsureRgbActive();
            _engine.FullFrameSampling = true;
            var replaced = _engine.SetEffect(live, disposePrevious: false);
            if (replaced is ShaderEffect { Name: StudioLiveKey })
            {
                // The replaced preview is never the lighting to restore.
                try
                { replaced.Dispose(); }
                catch { }
                replaced = null;
            }
            started.PrevEffect = adopt is not null ? adopt.PrevEffect : replaced;
        }
        finally
        {
            t_studioChanging = false;
        }
        _studioTimer ??= new Timer(_ => CheckStudioLease(), null, 1000, 1000);
        return new StudioLiveResult(StudioLiveOutcome.Ok, "Ok", started.Id);
    }

    private static string NewSessionId() => Guid.NewGuid().ToString("N");

    // Applies the look to the running preview and renews the lease. Null when the engine no longer runs the preview.
    private StudioLiveResult? UpdateSession(StudioSession session, StudioLiveBody body, string frag, float speed, float intensity, Dictionary<string, float>? extras)
    {
        if (string.Equals(frag, session.Frag, StringComparison.Ordinal))
        {
            // Same fragment: uniforms in place, no recompile.
            var cur = session.Live;
            cur.Speed = speed;
            cur.Intensity = intensity;
            cur.Hue = body.Hue;
            cur.Colorize = body.Colorize;
            cur.Saturation = body.Saturation;
            cur.Contrast = body.Contrast;
            cur.ExtraParams = extras;
        }
        else
        {
            ShaderEffect fresh;
            try
            {
                ShaderLibrary.RegisterCustom(StudioLiveId, frag);
                fresh = MakeShader(StudioLiveKey, frag, speed, intensity, body.Hue, body.Colorize, body.Saturation, body.Contrast, extras);
            }
            catch (Exception ex)
            {
                ShaderLibrary.RegisterCustom(StudioLiveId, session.Frag);
                return Fail(ex.Message);
            }
            bool swapped;
            t_studioChanging = true;
            try
            {
                swapped = _engine.SwapEffectIfCurrent(session.Live, fresh);
            }
            finally
            {
                t_studioChanging = false;
            }
            if (!swapped)
            {
                fresh.Dispose();
                return null;
            }
            session.Live = fresh;
            session.Frag = frag;
        }
        session.LeaseDeadline = StudioClockMs() + StudioLeaseMs;
        return new StudioLiveResult(StudioLiveOutcome.Ok, "Ok", session.Id);
    }

    private static StudioLiveResult Fail(string message) => new(StudioLiveOutcome.Failed, message, "");

    private static StudioLiveResult Ended() => new(StudioLiveOutcome.Ended, "session_ended", "");

    public bool StopStudioLive(string? sessionId = null)
    {
        bool was;
        lock (_studioLock)
        {
            if (sessionId is not null && (_studio is not { } s || !string.Equals(s.Id, sessionId, StringComparison.Ordinal)))
            {
                return false;
            }
            was = EndStudioLocked(StudioEnd.Restore) is not null;
        }
        return was;
    }

    /// <summary>Ends the session. Returns it, or null when none ran. Caller holds _studioLock.</summary>
    private StudioSession? EndStudioLocked(StudioEnd how)
    {
        var s = _studio;
        if (s is null)
        {
            return null;
        }
        _studio = null;
        _studioTimer?.Dispose();
        _studioTimer = null;
        ShaderLibrary.UnregisterCustom(StudioLiveId);
        if (how == StudioEnd.Defer)
        {
            return s;
        }
        FinishStudio(s, how);
        return s;
    }

    private void FinishStudio(StudioSession s, StudioEnd how)
    {
        t_studioChanging = true;
        try
        {
            // The engine may have moved on since the session was last looked at: only a preview still running is ours to replace.
            if (how == StudioEnd.Restore && EngineRunsLive(s) && RestoreEngine(s))
            {
                return;
            }
            s.PrevEffect?.Dispose();
            if (_engine.CurrentEffect is null)
            {
                _engine.FullFrameSampling = s.PrevFullFrame;
                _engine.StaticEffects?.Enabled = s.PrevStaticEnabled;
            }
        }
        finally
        {
            t_studioChanging = false;
        }
    }

    // False when the engine stopped running the preview before the swap landed; the caller then releases what is left.
    private bool RestoreEngine(StudioSession s)
    {
        if (s.PrevEffect is not null)
        {
            // The sampling flags go back inside the swap, so the frames painted before a pause already use the restored effect's settings.
            if (!_engine.RestoreEffectIfCurrent(s.Live, s.PrevEffect, s.PrevFrozen, s.PrevPaused, () =>
            {
                _engine.FullFrameSampling = s.PrevFullFrame;
                _engine.StaticEffects?.Enabled = s.PrevStaticEnabled;
            }))
            {
                return false;
            }
            return true;
        }
        // The session started from Off: put the engine and the bridge back to Off, as StopAll does, without its settings write.
        if (!_engine.Blackout)
        {
            BlackoutBeforeRelinquish();
        }
        if (!_engine.StopIfCurrent(s.Live))
        {
            return false;
        }
        if (!s.RgbWasActive)
        {
            _rgb?.Deactivate();
            _rgb?.AwaitShutdown();
        }
        _engine.FullFrameSampling = s.PrevFullFrame;
        _engine.StaticEffects?.Enabled = s.PrevStaticEnabled;
        return true;
    }

    // Another client replaced the effect (any mode start, stop, suspend): its change wins and nothing is restored over it.
    private void OnStudioEffectChanged()
    {
        if (_studio is null || t_studioChanging)
        {
            return;
        }
        var ended = false;
        lock (_studioLock)
        {
            if (_studio is { } s && !EngineRunsLive(s))
            {
                ended = EndStudioLocked(StudioEnd.Abandon) is not null;
            }
        }
        if (ended)
        {
            NotifyStudioEnded();
        }
    }

    // A Sync write no caller announced first. The known callers (music, SetSync, preset activation, profile switch) end
    // or hand off the session before they write; this only catches an unknown one. The session ends at once, and the
    // preview is put back on a pool thread, gated on the engine still running it: the swap can block on a paused
    // restore, can start or stop an audio-capture subprocess, and from Off needs the blackout push and a bridge
    // shutdown, none of which belong on the writer's thread (which may hold a profile lock or be the render thread).
    // A start that lands before the pool thread does snapshots the dead preview as Off.
    private void OnStudioStoreChanged()
    {
        if (_studio is null)
        {
            return;
        }
        StudioSession? deferred = null;
        lock (_studioLock)
        {
            if (_studio is { } s && !string.Equals(_store.Load().Lighting.Sync, s.PersistedSync, StringComparison.Ordinal))
            {
                ServiceLog.Warn("[studio-live] saved mode changed under a live session; ending it");
                deferred = EndStudioLocked(StudioEnd.Defer);
                if (deferred is not null)
                {
                    // Held in the handoff slot: a start that lands first adopts it, and the queued restore below then finds it gone.
                    if (_studioHandoff is { } older)
                    {
                        FinishStudio(older, StudioEnd.Abandon);
                    }
                    _studioHandoff = deferred;
                }
            }
        }
        if (deferred is null)
        {
            return;
        }
        NotifyStudioEnded();
        // A from-Off restore can still race a writer that starts its own mode; the engine gate makes that a no-op, but a start that lands between is lost.
        _ = Task.Run(() =>
        {
            BeforeStudioDeferredRestore?.Invoke();
            lock (_studioLock)
            {
                if (ReferenceEquals(_studioHandoff, deferred))
                {
                    _studioHandoff = null;
                    FinishStudio(deferred, StudioEnd.Restore);
                }
            }
            StudioDeferredRestoreDone?.Invoke();
        });
    }

    private StudioSession? _studioHandoff;

    // Test seams around the queued restore.
    internal Action? BeforeStudioDeferredRestore;
    internal Action? StudioDeferredRestoreDone;

    /// <summary>
    /// A caller is about to change the saved mode and then start the mode's own effect (preset activation, profile
    /// switch). Ends the session but leaves the preview running and keeps the snapshot; the caller finishes with
    /// <see cref="FinishStudioLiveHandoff"/> once its effect start has run.
    /// </summary>
    public void DetachStudioLive()
    {
        lock (_studioLock)
        {
            var s = EndStudioLocked(StudioEnd.Defer);
            if (s is null)
            {
                return;
            }
            if (_studioHandoff is { } older)
            {
                FinishStudio(older, StudioEnd.Abandon);
            }
            _studioHandoff = s;
        }
        NotifyStudioEnded();
    }

    /// <summary>
    /// Completes a <see cref="DetachStudioLive"/>: if the engine still runs the preview (the mode started no effect)
    /// the replaced lighting goes back, otherwise the held effect is released. No-op with nothing detached.
    /// </summary>
    public void FinishStudioLiveHandoff()
    {
        StudioSession? s;
        lock (_studioLock)
        {
            s = _studioHandoff;
            _studioHandoff = null;
            if (s is not null)
            {
                FinishStudio(s, StudioEnd.Restore);
            }
        }
        if (s is not null)
        {
            NotifyStudioEnded();
        }
    }

    /// <summary>
    /// A caller is about to change the saved mode (or start an effect for a new one). Ends the session first.
    /// With <paramref name="restore"/> the replaced lighting goes back, for callers whose change starts no effect
    /// (music, SetSync). Without it nothing is touched, for a caller whose own effect start replaces the preview.
    /// </summary>
    public bool EndStudioLiveBeforeSyncChange(bool restore)
    {
        bool ended;
        lock (_studioLock)
        {
            ended = EndStudioLocked(restore ? StudioEnd.Restore : StudioEnd.Abandon) is not null;
        }
        if (ended)
        {
            NotifyStudioEnded();
        }
        return ended;
    }

    internal void CheckStudioLease()
    {
        var ended = false;
        lock (_studioLock)
        {
            if (_studio is { } s && StudioClockMs() > s.LeaseDeadline)
            {
                ended = EndStudioLocked(StudioEnd.Restore) is not null;
            }
        }
        if (ended)
        {
            NotifyStudioEnded();
        }
    }

    // A custom effect the session replaced was deleted: put it back first, so the drop runs against it and a deleted
    // effect is never resurrected.
    private void EndStudioForCustomEdit(string key)
    {
        var ended = false;
        lock (_studioLock)
        {
            if (_studio is { PrevEffect: ShaderEffect prev } && prev.Name == key)
            {
                ended = EndStudioLocked(StudioEnd.Restore) is not null;
            }
        }
        if (ended)
        {
            NotifyStudioEnded();
        }
    }

    // A custom effect the session replaced was saved: rebuild the held effect from the new source and keep the
    // session, so leaving the Studio is what ends it, not saving.
    private void RefreshStudioSnapshot(string key)
    {
        lock (_studioLock)
        {
            if (_studio is not { PrevEffect: ShaderEffect prev } s || prev.Name != key || !ShaderLibrary.IsKnownKey(key))
            {
                return;
            }
            ShaderEffect fresh;
            try
            {
                var extras = prev.ExtraParams is null ? null : new Dictionary<string, float>(prev.ExtraParams);
                fresh = MakeShader(key, ShaderLibrary.Get(key), prev.Speed, prev.Intensity, prev.Hue, prev.Colorize, prev.Saturation, prev.Contrast, extras);
            }
            catch (Exception)
            {
                return;
            }
            s.PrevEffect = fresh;
            try
            { prev.Dispose(); }
            catch { }
        }
    }

    private void NotifyStudioEnded()
    {
        if (_panelHub is not null)
        {
            PanelTopics.BroadcastLighting(_panelHub);
        }
    }
}
