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

        // Everything below the snapshot reads is read before the first change.
        var persistedSync = _store.Load().Lighting.Sync;
        var reportedSync = GetSync();
        var reportedPaused = _engine.Paused;
        var prevFrozen = _engine.Frozen;
        var prevFullFrame = _engine.FullFrameSampling;
        var prevStaticEnabled = _engine.StaticEffects?.Enabled ?? false;
        var rgbWasActive = _rgb?.IsActive ?? false;

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
            started.PrevEffect = _engine.SetEffect(live, disposePrevious: false);
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

    // A Sync write the callers that start no effect (music, SetSync, preset activation) did not announce first. The
    // known callers end the session explicitly before they write; this only catches an unknown one.
    // The preview no longer running: nothing to restore, abandon and log. Still running with an effect to put back:
    // swap it back right here under the lock; that is a pointer swap with no blackout or bridge work, so it is safe
    // on the writer's thread. Started from Off: stopping needs the blackout push and a bridge shutdown, which can
    // block for seconds, so that restore goes to a pool thread (gated on the preview still running, no delay).
    private void OnStudioStoreChanged()
    {
        if (_studio is null)
        {
            return;
        }
        StudioSession? offRestore = null;
        var ended = false;
        lock (_studioLock)
        {
            if (_studio is { } s && !string.Equals(_store.Load().Lighting.Sync, s.PersistedSync, StringComparison.Ordinal))
            {
                ended = true;
                if (!EngineRunsLive(s))
                {
                    ServiceLog.Warn("[studio-live] saved mode changed under a live session; abandoned, the engine had moved on");
                    EndStudioLocked(StudioEnd.Abandon);
                }
                else if (s.PrevEffect is not null)
                {
                    ServiceLog.Warn("[studio-live] saved mode changed under a live session; restored");
                    EndStudioLocked(StudioEnd.Restore);
                }
                else
                {
                    ServiceLog.Warn("[studio-live] saved mode changed under a live session; restoring Off");
                    offRestore = EndStudioLocked(StudioEnd.Defer);
                }
            }
        }
        if (!ended)
        {
            return;
        }
        NotifyStudioEnded();
        if (offRestore is not null)
        {
            _ = Task.Run(() =>
            {
                lock (_studioLock)
                {
                    FinishStudio(offRestore, StudioEnd.Restore);
                }
            });
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
