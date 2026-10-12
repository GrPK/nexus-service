using System;
using System.Collections.Generic;
using System.Threading;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Effects;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Models.Lighting;
using Nexus.Service.Sockets;

namespace Nexus.Service.Lighting;

/// <summary>
/// Shader Studio live preview. A session is an engine overlay (<see cref="LightingEngine.SetStudioOverlay"/>)
/// painted over every device above pins and the running effect, so nothing global changes: no mode,
/// setting or effect is touched and ending the session just clears the overlay.
/// </summary>
public sealed partial class LightingProvider
{
    /// <summary>Name the overlay shader runs under.</summary>
    public const string StudioLiveKey = "studio-live";

    /// <summary>No POST for this long and the service clears the overlay on its own.</summary>
    public const int StudioLeaseMs = 15_000;

    private readonly MultiplexHub? _panelHub;
    private readonly object _studioLock = new();
    private volatile StudioSession? _studio;
    private Timer? _studioTimer;
    // Runs the engine loop while the mode is Off; the loop only ticks with an effect.
    private volatile IEffect? _studioIdle;
    // The overlay's fragment reads audio uniforms, which counts as audio demand for capture.
    private volatile bool _studioAudio;

    /// <summary>Clock for the lease; tests replace it.</summary>
    internal Func<long> StudioClockMs { get; set; } = () => Environment.TickCount64;

    public bool StudioLiveActive => _studio is not null;

    internal ShaderEffect? StudioLiveEffect => _studio?.Live;

    private sealed class StudioSession
    {
        public required string Id { get; set; }
        public required ShaderEffect Live { get; set; }
        public required string Frag { get; set; }
        public bool RgbWasActive { get; set; }
        public long LeaseDeadline { get; set; }
    }

    public StudioLiveResult StartStudioLive(StudioLiveBody body)
    {
        lock (_studioLock)
        {
            return StartStudioLiveLocked(body);
        }
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

        var session = _studio;
        if (body.SessionId is not null && (session is null || !string.Equals(session.Id, body.SessionId, StringComparison.Ordinal)))
        {
            return new StudioLiveResult(StudioLiveOutcome.Ended, "session_ended", "");
        }

        if (session is not null)
        {
            if (!ApplyLook(session, body, frag, speed, intensity, extras, out var failure))
            {
                return Fail(failure);
            }
            // Another tab opening its own session takes over the overlay; the old id is retired.
            if (body.SessionId is null)
            {
                session.Id = NewSessionId();
            }
        }
        else
        {
            // From Off the loop has to be started, which would end a sleep or lock hold; refuse while it holds.
            if (_engine.CurrentEffect is null && _engine.Blackout)
            {
                return Fail("lighting is held dark for sleep or lock");
            }
            ShaderEffect live;
            try
            {
                live = MakeShader(StudioLiveKey, frag, speed, intensity, body.Hue, body.Colorize, body.Saturation, body.Contrast, extras);
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
            session = new StudioSession { Id = NewSessionId(), Live = live, Frag = frag };
            _studio = session;
            _engine.SetStudioOverlay(live);
            SetStudioAudio(frag);
            _studioTimer ??= new Timer(_ => CheckStudioLease(), null, 1000, 1000);
        }
        EnsureEngineLoop(session);
        session.LeaseDeadline = StudioClockMs() + StudioLeaseMs;
        return new StudioLiveResult(StudioLiveOutcome.Ok, "Ok", session.Id);
    }

    private static string NewSessionId() => Guid.NewGuid().ToString("N");

    private bool ApplyLook(StudioSession session, StudioLiveBody body, string frag, float speed, float intensity, Dictionary<string, float>? extras, out string failure)
    {
        failure = "";
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
            return true;
        }
        ShaderEffect fresh;
        try
        {
            fresh = MakeShader(StudioLiveKey, frag, speed, intensity, body.Hue, body.Colorize, body.Saturation, body.Contrast, extras);
        }
        catch (Exception ex)
        {
            failure = ex.Message;
            return false;
        }
        var old = _engine.SetStudioOverlay(fresh);
        session.Live = fresh;
        session.Frag = frag;
        old?.Dispose();
        SetStudioAudio(frag);
        return true;
    }

    // The engine loop only ticks while an effect runs. In Off a placeholder effect keeps it going (and the bridge up)
    // for the session; a mode started later by anyone replaces it and the overlay carries on over that.
    private void EnsureEngineLoop(StudioSession session)
    {
        // Installing the placeholder would end a sleep or lock hold; the renewal after the hold lifts puts it in.
        if (_engine.CurrentEffect is not null || _engine.Blackout)
        {
            return;
        }
        session.RgbWasActive = _rgb?.IsActive ?? false;
        EnsureRgbActive();
        var idle = new StudioIdleEffect();
        _studioIdle = idle;
        _engine.SetEffect(idle);
    }

    private void SetStudioAudio(string? frag)
    {
        _studioAudio = frag is not null && ShaderLibrary.ReadsAudio(frag);
        ReconcileAudioCapture();
    }

    private static StudioLiveResult Fail(string message) => new(StudioLiveOutcome.Failed, message, "");

    public bool StopStudioLive(string? sessionId = null)
    {
        bool was;
        lock (_studioLock)
        {
            if (sessionId is not null && (_studio is not { } s || !string.Equals(s.Id, sessionId, StringComparison.Ordinal)))
            {
                return false;
            }
            was = EndStudioLocked();
        }
        return was;
    }

    /// <summary>Clears the overlay. When the session itself kept the loop alive over an Off mode, Off is put back as StopAll leaves it, without its settings write. Caller holds _studioLock.</summary>
    private bool EndStudioLocked()
    {
        var s = _studio;
        if (s is null)
        {
            return false;
        }
        _studio = null;
        _studioTimer?.Dispose();
        _studioTimer = null;
        _engine.SetStudioOverlay(null)?.Dispose();
        SetStudioAudio(null);
        var idle = _studioIdle;
        _studioIdle = null;
        // Only while the placeholder is still the running effect: a mode someone started meanwhile is theirs,
        // and both the hold and the stop are conditional on it atomically with a concurrent SetEffect.
        if (idle is not null)
        {
            var held = _engine.Blackout ? ReferenceEquals(_engine.CurrentEffect, idle) : BlackoutBeforeRelinquish(idle);
            if (held && _engine.StopIfCurrent(idle) && !s.RgbWasActive)
            {
                _rgb?.Deactivate();
                _rgb?.AwaitShutdown();
            }
        }
        return true;
    }

    internal void CheckStudioLease()
    {
        var ended = false;
        lock (_studioLock)
        {
            if (_studio is { } s && StudioClockMs() > s.LeaseDeadline)
            {
                ended = EndStudioLocked();
            }
        }
        if (ended && _panelHub is not null)
        {
            PanelTopics.BroadcastLighting(_panelHub);
        }
    }
}
