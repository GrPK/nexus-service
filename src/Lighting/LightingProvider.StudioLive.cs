using System;
using System.Collections.Generic;
using System.Threading;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Models.Lighting;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;

namespace Nexus.Service.Lighting;

/// <summary>
/// Shader Studio live preview. A session swaps the running effect for the Studio's fragment on
/// every device, and puts everything back on stop, lease lapse or a conflicting change. It
/// never writes a setting: the effect is held in the engine, the device overrides are an
/// in-memory overlay on the settings document (see <see cref="EphemeralDeviceOverlay"/>), and
/// the persisted lists are never touched, so a restart mid-session boots the saved setup.
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
    // True while this class itself drives the engine, so its own effect changes are not read as a conflict.
    private volatile bool _studioChanging;
    private Timer? _studioTimer;

    /// <summary>Clock for the lease; tests replace it.</summary>
    internal Func<long> StudioClockMs { get; set; } = () => Environment.TickCount64;

    public bool StudioLiveActive => _studio is not null;

    private sealed class StudioSession
    {
        public required ShaderEffect Live { get; set; }
        public required string Frag { get; init; }
        public required IEffect? PrevEffect { get; init; }
        public required bool PrevPaused { get; init; }
        public required bool PrevFrozen { get; init; }
        public required bool PrevFullFrame { get; init; }
        public required bool PrevStaticEnabled { get; init; }
        public required bool RgbWasActive { get; init; }
        public required string ReportedSync { get; init; }
        public required bool ReportedPaused { get; init; }
        public required string PersistedSync { get; init; }
        public required EphemeralDeviceOverlay Overlay { get; set; }
        public required List<string> Skipped { get; init; }
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

        if (_studio is { } session)
        {
            return UpdateSession(session, body, frag, speed, intensity, extras);
        }

        if (_engine.Blackout)
        {
            return Fail("lighting is held dark for sleep or lock");
        }

        // Everything below the snapshot reads is read before the first change.
        var settings = _store.Load();
        var persistedSync = settings.Lighting.Sync;
        var reportedSync = GetSync();
        var reportedPaused = _engine.Paused;
        var prevFrozen = _engine.Frozen;
        var prevFullFrame = _engine.FullFrameSampling;
        var prevStaticEnabled = _engine.StaticEffects?.Enabled ?? false;
        var rgbWasActive = _rgb?.IsActive ?? false;

        var (overlay, skipped) = BuildDeviceOverlay(settings);
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

        settings.Devices.SetOverlay(overlay);
        IEffect? prev;
        _studioChanging = true;
        try
        {
            EnsureRgbActive();
            _engine.FullFrameSampling = true;
            prev = _engine.SetEffect(live, disposePrevious: false);
        }
        finally
        {
            _studioChanging = false;
        }
        var started = new StudioSession
        {
            Live = live,
            Frag = frag,
            PrevEffect = prev,
            PrevPaused = reportedPaused,
            PrevFrozen = prevFrozen,
            PrevFullFrame = prevFullFrame,
            PrevStaticEnabled = prevStaticEnabled,
            RgbWasActive = rgbWasActive,
            ReportedSync = reportedSync,
            ReportedPaused = reportedPaused,
            PersistedSync = persistedSync,
            Overlay = overlay,
            Skipped = skipped,
            LeaseDeadline = StudioClockMs() + StudioLeaseMs,
        };
        _studio = started;
        _studioTimer ??= new Timer(_ => CheckStudioLease(), null, 1000, 1000);
        if (overlay.ForceOn.Count + overlay.ForceControlled.Count > 0)
        {
            _rgb?.RequestTopologyRefresh();
        }
        return Succeed(started);
    }

    private StudioLiveResult UpdateSession(StudioSession session, StudioLiveBody body, string frag, float speed, float intensity, Dictionary<string, float>? extras)
    {
        // A settings reload swaps the document; put the overlay back on whichever one is live.
        var settings = _store.Load();
        if (!ReferenceEquals(settings.Devices.Overlay, session.Overlay))
        {
            settings.Devices.SetOverlay(session.Overlay);
        }
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
            _engine.SetFrozen(false);
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
            _studioChanging = true;
            try
            {
                swapped = _engine.SwapEffectIfCurrent(session.Live, fresh);
            }
            finally
            {
                _studioChanging = false;
            }
            if (!swapped)
            {
                fresh.Dispose();
                EndStudioLocked(restore: false);
                return Fail("another client changed the lighting; live preview ended");
            }
            session.Live = fresh;
            // Frag is init-only on purpose elsewhere; the session object is replaced to carry the new text.
            _studio = CloneWithFrag(session, frag);
            session = _studio;
        }
        session.LeaseDeadline = StudioClockMs() + StudioLeaseMs;
        return Succeed(session);
    }

    private static StudioSession CloneWithFrag(StudioSession s, string frag) => new()
    {
        Live = s.Live,
        Frag = frag,
        PrevEffect = s.PrevEffect,
        PrevPaused = s.PrevPaused,
        PrevFrozen = s.PrevFrozen,
        PrevFullFrame = s.PrevFullFrame,
        PrevStaticEnabled = s.PrevStaticEnabled,
        RgbWasActive = s.RgbWasActive,
        ReportedSync = s.ReportedSync,
        ReportedPaused = s.ReportedPaused,
        PersistedSync = s.PersistedSync,
        Overlay = s.Overlay,
        Skipped = s.Skipped,
        LeaseDeadline = s.LeaseDeadline,
    };

    private static StudioLiveResult Fail(string message) => new(false, message, 0, Array.Empty<string>());

    private static StudioLiveResult Succeed(StudioSession s) =>
        new(true, "Ok", s.Overlay.ForceOn.Count + s.Overlay.ForceControlled.Count, s.Skipped);

    /// <summary>
    /// Every device that streams black (LEDs off) is read as on. Every device Nexus does not
    /// control is read as controlled, except one whose OpenRGB controller is detached or held
    /// off: bringing those back needs a persisted exclusion change and a daemon restart.
    /// </summary>
    private static (EphemeralDeviceOverlay Overlay, List<string> Skipped) BuildDeviceOverlay(NexusSettings settings)
    {
        var forceOn = new HashSet<string>(settings.Devices.PersistedDisabledLightingDevices, StringComparer.Ordinal);
        var forceControlled = new HashSet<string>(StringComparer.Ordinal);
        var skipped = new List<string>();
        foreach (var id in settings.Devices.PersistedUncontrolledLightingDevices)
        {
            if (OpenRgbDetectorExclusions.ExcludedBaseOf(id, settings) is not null
                || OpenRgbGamepadDefaults.IsHeldOffKey(settings, id))
            {
                skipped.Add(id);
                continue;
            }
            forceControlled.Add(id);
        }
        return (new EphemeralDeviceOverlay(forceOn, forceControlled), skipped);
    }

    public bool StopStudioLive()
    {
        bool was;
        lock (_studioLock)
        {
            was = EndStudioLocked(restore: true);
        }
        return was;
    }

    /// <summary>Ends the session. restore false leaves the engine to whoever changed it. Returns false when none ran. Caller holds _studioLock.</summary>
    private bool EndStudioLocked(bool restore)
    {
        var s = _studio;
        if (s is null)
        {
            return false;
        }
        _studio = null;
        _studioTimer?.Dispose();
        _studioTimer = null;
        s.Overlay.Revoke();
        var settings = _store.Load();
        if (ReferenceEquals(settings.Devices.Overlay, s.Overlay))
        {
            settings.Devices.SetOverlay(null);
        }
        ShaderLibrary.UnregisterCustom(StudioLiveId);

        _studioChanging = true;
        try
        {
            if (restore)
            {
                RestoreEngine(s);
            }
            else
            {
                s.PrevEffect?.Dispose();
                if (_engine.CurrentEffect is null)
                {
                    _engine.FullFrameSampling = s.PrevFullFrame;
                    _engine.StaticEffects?.Enabled = s.PrevStaticEnabled;
                }
            }
        }
        finally
        {
            _studioChanging = false;
        }
        if (s.Overlay.ForceOn.Count + s.Overlay.ForceControlled.Count > 0)
        {
            _rgb?.RequestTopologyRefresh();
        }
        return true;
    }

    private void RestoreEngine(StudioSession s)
    {
        if (s.PrevEffect is not null)
        {
            _engine.SetEffect(s.PrevEffect, disposePrevious: true);
            if (s.PrevFrozen)
            {
                _engine.SetFrozen(true);
            }
            if (s.PrevPaused)
            {
                _engine.SetPaused(true);
            }
        }
        else
        {
            // The session started from Off: put the engine and the bridge back to Off, as StopAll does, without its settings write.
            BlackoutBeforeRelinquish();
            _engine.Stop();
            if (!s.RgbWasActive)
            {
                _rgb?.Deactivate();
                _rgb?.AwaitShutdown();
            }
        }
        _engine.FullFrameSampling = s.PrevFullFrame;
        _engine.StaticEffects?.Enabled = s.PrevStaticEnabled;
    }

    // Another client replaced the effect (any mode start, stop, suspend): its change wins and nothing is restored over it.
    private void OnStudioEffectChanged()
    {
        if (_studio is null || _studioChanging)
        {
            return;
        }
        var ended = false;
        lock (_studioLock)
        {
            if (_studio is { } s && !_studioChanging && !ReferenceEquals(_engine.CurrentEffect, s.Live))
            {
                ended = EndStudioLocked(restore: false);
            }
        }
        if (ended)
        {
            NotifyStudioEnded();
        }
    }

    // The saved mode changed without touching the engine (music, for one). The saved mode wins; the preview stops and the engine goes back.
    private void OnStudioStoreChanged()
    {
        if (_studio is null)
        {
            return;
        }
        var ended = false;
        lock (_studioLock)
        {
            if (_studio is { } s && !string.Equals(_store.Load().Lighting.Sync, s.PersistedSync, StringComparison.Ordinal))
            {
                ended = EndStudioLocked(restore: ReferenceEquals(_engine.CurrentEffect, s.Live));
            }
        }
        if (ended)
        {
            NotifyStudioEnded();
        }
    }

    internal void CheckStudioLease()
    {
        var ended = false;
        lock (_studioLock)
        {
            if (_studio is { } s && StudioClockMs() > s.LeaseDeadline)
            {
                ended = EndStudioLocked(restore: true);
            }
        }
        if (ended)
        {
            NotifyStudioEnded();
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
