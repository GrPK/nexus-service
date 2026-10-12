using Nexus.Service.Sockets;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Media;
using Nexus.Service.Models.Lighting;
using Nexus.Service.Persistence;
using Nexus.Service.Tests.Lighting.CustomEffects;

namespace Nexus.Service.Tests.Lighting;

/// <summary>Real provider, engine and config store with an uninitialised GPU: effects are built and swapped, nothing renders.</summary>
public sealed class StudioLiveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-studio-live-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly JsonConfigStore _config;
    private readonly LightingEngine _engine;
    private readonly GpuContext _gpu;
    private readonly LightingProvider _provider;
    private long _now = 1_000;

    public StudioLiveTests()
    {
        Directory.CreateDirectory(_dir);
        _config = new JsonConfigStore(Path.Combine(_dir, "settings.json"));
        _engine = new LightingEngine();
        _gpu = new GpuContext(160, 90);
        _provider = new LightingProvider(_config, _engine, new LightingOutputHub(), _gpu, new MediaLibrary(),
            new Nexus.Service.Platform.DefaultMonitorEnumerator());
        _provider.StudioClockMs = () => _now;
    }

    public void Dispose()
    {
        _provider.Dispose();
        _gpu.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class FakeEffect(string name, byte red = 0, Action? onRender = null) : IEffect
    {
        private int _renders;
        public string Name { get; } = name;
        public bool Disposed { get; private set; }
        public int Renders => Volatile.Read(ref _renders);
        public void RenderFrame(CanvasBuffer canvas, double tickMs)
        {
            canvas.Fill(red, 0, 0);
            onRender?.Invoke();
            Interlocked.Increment(ref _renders);
        }
        public void Dispose() => Disposed = true;
    }

    private static void WaitFor(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 3000;
        while (!condition() && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(5);
        }
    }

    private static StudioLiveBody Body(string? frag = null, int speed = 50, float hue = 0f, string? sessionId = null) => new()
    {
        SessionId = sessionId,
        Frag = frag ?? CustomFrags.Frag(),
        Speed = speed,
        Hue = hue,
    };

    private string SettingsJson()
    {
        _config.FlushNow();
        return File.ReadAllText(_config.SettingsPath);
    }

    private static AnimateHeadlessStart Animate(string effect) => new()
    {
        Effect = effect,
        Speed = 50,
        Intensity = 1f,
        Saturation = 1f,
        Contrast = 1f,
        Persist = true,
    };

    [Fact]
    public void Start_runs_the_fragment_under_the_reserved_key_with_full_frame_sampling()
    {
        var result = _provider.StartStudioLive(Body());

        Assert.True(result.Ok);
        var shader = Assert.IsType<ShaderEffect>(_engine.CurrentEffect);
        Assert.Equal(LightingProvider.StudioLiveKey, shader.Name);
        Assert.True(_engine.FullFrameSampling);
        Assert.True(_provider.StudioLiveActive);
    }

    [Fact]
    public void The_reserved_key_cannot_be_a_stored_custom_effect_id()
    {
        var id = LightingProvider.StudioLiveKey[ShaderLibrary.CustomPrefix.Length..];

        Assert.False(Nexus.Service.Lighting.CustomEffects.CustomEffectStore.IsValidId(id));
    }

    [Fact]
    public void Animate_is_restored_as_the_same_effect_instance_and_sampling_flag()
    {
        _provider.StartAnimate(Animate("rainbow"));
        var before = _engine.CurrentEffect;
        var sync = _provider.GetSync();

        _provider.StartStudioLive(Body());
        Assert.NotSame(before, _engine.CurrentEffect);
        Assert.True(_provider.StopStudioLive());

        Assert.Same(before, _engine.CurrentEffect);
        Assert.Equal(sync, _provider.GetSync());
        Assert.False(_engine.FullFrameSampling);
        Assert.False(_provider.StudioLiveActive);
    }

    [Fact]
    public void A_sweep_effects_full_frame_sampling_is_restored()
    {
        Assert.True(ShaderLibrary.IsSweepEffect("sweepbreathing"));
        _provider.StartAnimate(Animate("sweepbreathing"));
        Assert.True(_engine.FullFrameSampling);
        var before = _engine.CurrentEffect;

        _provider.StartStudioLive(Body());
        _provider.StopStudioLive();

        Assert.Same(before, _engine.CurrentEffect);
        Assert.True(_engine.FullFrameSampling);
    }

    [Fact]
    public void A_non_sweep_effect_restores_sampling_off()
    {
        _provider.StartAnimate(Animate("rainbow"));

        _provider.StartStudioLive(Body());
        _provider.StopStudioLive();

        Assert.False(_engine.FullFrameSampling);
    }

    [Fact]
    public void Static_mode_is_restored_frozen_with_its_reported_sync()
    {
        _provider.StartStatic(new StaticHeadlessStart { Effect = StaticEffectCatalog.DefaultEffect, Persist = true, Saturation = 1f, Contrast = 1f });
        var before = _engine.CurrentEffect;
        Assert.True(_engine.Frozen);
        Assert.Equal("static", _provider.GetSync());

        _provider.StartStudioLive(Body());
        Assert.False(_engine.Frozen);
        Assert.Equal("static", _provider.GetSync());
        _provider.StopStudioLive();

        Assert.Same(before, _engine.CurrentEffect);
        Assert.True(_engine.Frozen);
    }

    [Fact]
    public void Screen_mode_is_restored_as_the_same_effect()
    {
        _provider.StartScreen(new ScreenHeadlessStart());
        var before = _engine.CurrentEffect;
        Assert.Equal("screen", _provider.GetSync());

        _provider.StartStudioLive(Body());
        Assert.Equal("screen", _provider.GetSync());
        _provider.StopStudioLive();

        Assert.Same(before, _engine.CurrentEffect);
    }

    [Fact]
    public void Media_idle_is_restored_as_the_same_effect()
    {
        _provider.StartMediaIdle();
        var before = _engine.CurrentEffect;

        _provider.StartStudioLive(Body());
        _provider.StopStudioLive();

        Assert.Same(before, _engine.CurrentEffect);
        Assert.Equal("media", _provider.GetSync());
    }

    [Fact]
    public void Game_sync_is_restored_as_the_same_effect()
    {
        _provider.StartGameSync();
        var before = _provider.ActiveGameSyncEffect();
        Assert.NotNull(before);

        _provider.StartStudioLive(Body());
        Assert.Null(_provider.ActiveGameSyncEffect());
        _provider.StopStudioLive();

        Assert.Same(before, _provider.ActiveGameSyncEffect());
    }

    [Fact]
    public void Off_is_restored_as_no_effect_and_the_saved_mode_is_untouched()
    {
        _provider.StartAnimate(Animate("rainbow"));
        _provider.StopAll();
        Assert.Null(_engine.CurrentEffect);
        Assert.Equal("none", _provider.GetSync());

        _provider.StartStudioLive(Body());
        Assert.NotNull(_engine.CurrentEffect);
        Assert.Equal("none", _provider.GetSync());
        _provider.StopStudioLive();

        Assert.Null(_engine.CurrentEffect);
        Assert.Equal("none", _provider.GetSync());
        Assert.Equal("none", _config.Load().Lighting.Sync);
    }

    [Fact]
    public void A_paused_effect_is_restored_paused_and_reported_paused_meanwhile()
    {
        _provider.StartAnimate(Animate("rainbow"));
        _provider.SetPaused(true);

        _provider.StartStudioLive(Body());
        Assert.True(_provider.IsPaused);
        Assert.False(_engine.Paused);
        _provider.StopStudioLive();

        Assert.True(_engine.Paused);
    }

    [Fact]
    public void Another_client_sees_the_state_before_the_session()
    {
        _provider.StartAnimate(Animate("rainbow"));

        _provider.StartStudioLive(Body());

        Assert.Equal("rainbow", _provider.GetSync());
    }

    [Fact]
    public void Stop_is_idempotent()
    {
        Assert.False(_provider.StopStudioLive());
        _provider.StartStudioLive(Body());
        Assert.True(_provider.StopStudioLive());
        Assert.False(_provider.StopStudioLive());
        Assert.False(_provider.StudioLiveActive);
    }

    [Fact]
    public void The_lease_lapsing_restores_on_its_own()
    {
        _provider.StartAnimate(Animate("rainbow"));
        var before = _engine.CurrentEffect;
        _provider.StartStudioLive(Body());

        _now += 15_000;
        _provider.CheckStudioLease();
        Assert.True(_provider.StudioLiveActive);

        _now += 1;
        _provider.CheckStudioLease();

        Assert.False(_provider.StudioLiveActive);
        Assert.Same(before, _engine.CurrentEffect);
    }

    [Fact]
    public void Every_post_renews_the_lease()
    {
        _provider.StartStudioLive(Body());
        _now += 10_000;
        _provider.StartStudioLive(Body());
        _now += 10_000;

        _provider.CheckStudioLease();

        Assert.True(_provider.StudioLiveActive);
    }

    [Fact]
    public void The_same_fragment_updates_uniforms_in_place()
    {
        _provider.StartStudioLive(Body(speed: 50, hue: 0f));
        var first = Assert.IsType<ShaderEffect>(_engine.CurrentEffect);

        _provider.StartStudioLive(Body(speed: 100, hue: 0.25f));

        var second = Assert.IsType<ShaderEffect>(_engine.CurrentEffect);
        Assert.Same(first, second);
        Assert.Equal(2f, second.Speed);
        Assert.Equal(0.25f, second.Hue);
    }

    [Fact]
    public void A_different_fragment_swaps_the_shader_and_still_restores_the_original()
    {
        _provider.StartAnimate(Animate("rainbow"));
        var before = _engine.CurrentEffect;
        _provider.StartStudioLive(Body(CustomFrags.Frag(0.2f)));
        var first = _engine.CurrentEffect;

        _provider.StartStudioLive(Body(CustomFrags.Frag(0.9f)));

        Assert.NotSame(first, _engine.CurrentEffect);
        Assert.Equal(LightingProvider.StudioLiveKey, _engine.CurrentEffectName);
        Assert.Contains("0.9", ShaderLibrary.Get(LightingProvider.StudioLiveKey));
        _provider.StopStudioLive();
        Assert.Same(before, _engine.CurrentEffect);
    }

    [Fact]
    public void A_session_writes_nothing_to_settings()
    {
        _provider.StartAnimate(Animate("rainbow"));
        var before = SettingsJson();

        _provider.StartStudioLive(Body());
        _provider.StartStudioLive(Body(CustomFrags.Frag(0.9f)));
        var during = SettingsJson();
        _provider.StopStudioLive();
        var after = SettingsJson();

        Assert.Equal(before, during);
        Assert.Equal(before, after);
        Assert.DoesNotContain("studio-live", after);
    }

    [Fact]
    public void Another_client_starting_an_effect_ends_the_session_and_wins()
    {
        var prior = new FakeEffect("prior");
        _engine.SetEffect(prior);
        _provider.StartStudioLive(Body());

        _provider.StartAnimate(Animate("rainbow"));

        Assert.False(_provider.StudioLiveActive);
        Assert.Equal("rainbow", _engine.CurrentEffectName);
        Assert.True(prior.Disposed);
        Assert.Equal("rainbow", _provider.GetSync());
    }

    [Fact]
    public void Stop_all_from_another_client_ends_the_session_without_restoring()
    {
        var prior = new FakeEffect("prior");
        _engine.SetEffect(prior);
        _provider.StartStudioLive(Body());

        _provider.StopAll();

        Assert.False(_provider.StudioLiveActive);
        Assert.Null(_engine.CurrentEffect);
        Assert.True(prior.Disposed);
    }

    [Fact]
    public void Music_ends_the_session_with_a_restore_before_it_writes()
    {
        _provider.StartAnimate(Animate("rainbow"));
        var before = _engine.CurrentEffect;
        _provider.StartStudioLive(Body());

        _provider.StartMusic(new MusicHeadlessStart());

        Assert.False(_provider.StudioLiveActive);
        Assert.Same(before, _engine.CurrentEffect);
        Assert.Equal("music", _config.Load().Lighting.Sync);
    }

    [Fact]
    public void Set_sync_ends_the_session_with_a_restore_before_it_writes()
    {
        _provider.StartAnimate(Animate("rainbow"));
        var before = _engine.CurrentEffect;
        _provider.StartStudioLive(Body());

        _provider.SetSync("screen");

        Assert.False(_provider.StudioLiveActive);
        Assert.Same(before, _engine.CurrentEffect);
    }

    [Fact]
    public void A_handed_off_preview_is_replaced_by_the_modes_own_start_and_the_held_effect_is_released()
    {
        var prior = new FakeEffect("prior");
        _engine.SetEffect(prior);
        _provider.StartStudioLive(Body());

        _provider.DetachStudioLive();
        Assert.False(_provider.StudioLiveActive);
        Assert.Equal(_config.Load().Lighting.Sync, _provider.GetSync());
        Assert.False(prior.Disposed);
        _provider.StartAnimate(Animate("plasma"));
        _provider.FinishStudioLiveHandoff();

        Assert.Equal("plasma", _engine.CurrentEffectName);
        Assert.True(prior.Disposed);
    }

    // The mode a preset names starts no effect: the preview must not be left running.
    [Theory]
    [InlineData("music")]
    [InlineData("gif")]
    [InlineData("media")]
    [InlineData("")]
    public void A_handed_off_preview_is_put_back_when_the_mode_starts_no_effect(string mode)
    {
        _provider.StartAnimate(Animate("rainbow"));
        var before = _engine.CurrentEffect;
        _provider.StartStudioLive(Body());

        _provider.DetachStudioLive();
        _config.Update(c => c.Lighting.Sync = mode);
        switch (mode)
        {
            case "music":
                _provider.StartMusic(new MusicHeadlessStart());
                break;
            case "media":
                Assert.False(_provider.StartMedia("missing"));
                break;
        }
        _provider.FinishStudioLiveHandoff();

        Assert.Same(before, _engine.CurrentEffect);
        Assert.NotEqual(LightingProvider.StudioLiveKey, _engine.CurrentEffectName);
    }

    [Fact]
    public void Finishing_with_nothing_detached_is_a_no_op()
    {
        _provider.FinishStudioLiveHandoff();

        Assert.False(_provider.StudioLiveActive);
    }

    [Fact]
    public void A_new_session_adopts_a_pending_handoffs_snapshot_instead_of_the_dead_preview()
    {
        var prior = new FakeEffect("prior");
        _engine.SetEffect(prior);
        _provider.StartStudioLive(Body());
        _provider.DetachStudioLive();

        _provider.StartStudioLive(Body(CustomFrags.Frag(0.9f)));
        _provider.FinishStudioLiveHandoff();
        Assert.True(_provider.StudioLiveActive);
        _provider.StopStudioLive();

        Assert.Same(prior, _engine.CurrentEffect);
        Assert.False(prior.Disposed);
    }

    [Fact]
    public void A_new_session_adopts_a_queued_restores_snapshot_and_the_queued_restore_does_nothing()
    {
        var prior = new FakeEffect("prior");
        _engine.SetEffect(prior);
        _provider.StartStudioLive(Body());
        using var hold = new ManualResetEventSlim();
        using var done = new ManualResetEventSlim();
        _provider.BeforeStudioDeferredRestore = () => hold.Wait(5000);
        _provider.StudioDeferredRestoreDone = done.Set;

        _config.Update(c => c.Lighting.Sync = "unknown-writer");
        Assert.False(_provider.StudioLiveActive);
        _provider.StartStudioLive(Body(CustomFrags.Frag(0.9f)));
        hold.Set();
        Assert.True(done.Wait(5000));

        Assert.True(_provider.StudioLiveActive);
        Assert.Equal(LightingProvider.StudioLiveKey, _engine.CurrentEffectName);
        _provider.StopStudioLive();
        Assert.Same(prior, _engine.CurrentEffect);
        Assert.False(prior.Disposed);
    }

    [Fact]
    public void A_dead_preview_with_no_pending_snapshot_snapshots_as_off()
    {
        _provider.StartStudioLive(Body());
        _provider.EndStudioLiveBeforeSyncChange(restore: false);

        _provider.StartStudioLive(Body(CustomFrags.Frag(0.9f)));
        _provider.StopStudioLive();

        Assert.Null(_engine.CurrentEffect);
    }

    [Fact]
    public void A_profile_switch_hands_the_preview_off_before_its_settings_load_and_leaves_nothing_running()
    {
        var pm = new ProfileManager(_config);
        pm.Initialize();
        pm.CreateProfile("Other");
        var first = pm.GetManifest().Profiles[0].Id;
        var loading = 0;
        pm.ProfileLoading += () =>
        {
            loading++;
            _provider.DetachStudioLive();
        };
        pm.ProfileLoadSettled += _provider.FinishStudioLiveHandoff;
        // What AppBootstrap.ReapplyAfterProfileSwitch does first: stop the engine before restarting the profile's mode.
        pm.OnProfileSwitched += () => _engine.Stop();
        var prior = new FakeEffect("prior");
        _engine.SetEffect(prior);
        _provider.StartStudioLive(Body());

        pm.SwitchProfile(first);

        Assert.Equal(1, loading);
        Assert.False(_provider.StudioLiveActive);
        Assert.Null(_engine.CurrentEffect);
        Assert.True(prior.Disposed);
    }

    [Fact]
    public void A_profile_switch_that_throws_after_the_load_still_settles_the_handoff()
    {
        var pm = new ProfileManager(_config);
        pm.Initialize();
        pm.CreateProfile("Other");
        var first = pm.GetManifest().Profiles[0].Id;
        pm.ProfileLoading += _provider.DetachStudioLive;
        pm.ProfileLoadSettled += _provider.FinishStudioLiveHandoff;
        pm.OnProfileSwitched += () => throw new InvalidOperationException("earlier subscriber failed");
        _provider.StartAnimate(Animate("rainbow"));
        var before = _engine.CurrentEffect;
        _provider.StartStudioLive(Body());

        Assert.Throws<InvalidOperationException>(() => pm.SwitchProfile(first));

        Assert.False(_provider.StudioLiveActive);
        Assert.Same(before, _engine.CurrentEffect);
    }

    [Fact]
    public void An_unannounced_sync_write_ends_the_session_and_restores_off_the_writers_thread()
    {
        _provider.StartAnimate(Animate("rainbow"));
        var before = _engine.CurrentEffect;
        _provider.StartStudioLive(Body());

        _config.Update(c => c.Lighting.Sync = "unknown-writer");

        Assert.False(_provider.StudioLiveActive);
        WaitFor(() => ReferenceEquals(_engine.CurrentEffect, before));
        Assert.Same(before, _engine.CurrentEffect);
    }

    [Fact]
    public void An_unannounced_sync_write_from_off_stops_the_preview_off_thread()
    {
        _provider.StartStudioLive(Body());

        _config.Update(c => c.Lighting.Sync = "unknown-writer");

        Assert.False(_provider.StudioLiveActive);
        WaitFor(() => _engine.CurrentEffect is null);
        Assert.Null(_engine.CurrentEffect);
    }

    [Fact]
    public void A_sync_write_after_another_client_took_the_engine_abandons_and_leaves_it_alone()
    {
        var prior = new FakeEffect("prior");
        _engine.SetEffect(prior);
        _provider.StartStudioLive(Body());
        _provider.StartAnimate(Animate("plasma"));
        var other = _engine.CurrentEffect;

        _config.Update(c => c.Lighting.Sync = "later");

        Assert.Same(other, _engine.CurrentEffect);
        Assert.False(_provider.StudioLiveActive);
    }

    [Fact]
    public void The_replaced_effect_is_not_disposed_while_a_session_holds_it()
    {
        var prior = new FakeEffect("prior");
        _engine.SetEffect(prior);

        _provider.StartStudioLive(Body());
        Assert.False(prior.Disposed);
        _provider.StopStudioLive();

        Assert.Same(prior, _engine.CurrentEffect);
        Assert.False(prior.Disposed);
    }

    [Fact]
    public void The_reserved_key_is_never_saved_as_an_animate_choice()
    {
        _provider.StartAnimate(Animate("rainbow"));
        _provider.StartStudioLive(Body());

        _provider.StartAnimate(Animate(LightingProvider.StudioLiveKey));

        Assert.True(_provider.StudioLiveActive);
        Assert.Equal("rainbow", _config.Load().Lighting.Sync);
        Assert.DoesNotContain("studio-live", SettingsJson());
    }

    [Fact]
    public void A_held_dark_engine_refuses_a_session()
    {
        _provider.StartAnimate(Animate("rainbow"));
        _engine.SetBlackout(true);

        var result = _provider.StartStudioLive(Body());

        Assert.False(result.Ok);
        Assert.False(_provider.StudioLiveActive);
    }

    [Fact]
    public void A_lapsed_session_can_start_again()
    {
        _provider.StartStudioLive(Body());
        _now += 20_000;
        _provider.CheckStudioLease();

        Assert.True(_provider.StartStudioLive(Body()).Ok);
        Assert.True(_provider.StudioLiveActive);
    }

    [Fact]
    public void Stop_does_not_restore_over_an_effect_another_client_started()
    {
        var prior = new FakeEffect("prior");
        _engine.SetEffect(prior);
        _provider.StartStudioLive(Body());
        _provider.StartAnimate(Animate("plasma"));
        var other = _engine.CurrentEffect;

        Assert.False(_provider.StopStudioLive());

        Assert.Same(other, _engine.CurrentEffect);
        Assert.True(prior.Disposed);
    }

    [Fact]
    public void A_pause_restore_shows_the_restored_effects_frame_not_the_previews()
    {
        _engine.UpdateDevices([new DeviceFrame(0, "d", 1)]);
        _engine.StaticEffects = new StaticDeviceEffectTracker();
        _engine.StaticEffects.Enabled = true;
        // What the restored effect samples with each time it paints: a pause must not hold a frame made with the preview's settings.
        var samples = new System.Collections.Concurrent.ConcurrentQueue<(bool Full, bool Static)>();
        var prior = new FakeEffect("prior", red: 200, onRender: () => samples.Enqueue((_engine.FullFrameSampling, _engine.StaticEffects!.Enabled)));
        _engine.SetEffect(prior);
        WaitFor(() => prior.Renders > 0);
        _engine.SetPaused(true);
        var rendersBefore = prior.Renders;
        _provider.StartStudioLive(Body());

        _provider.StopStudioLive();

        Assert.True(_engine.Paused);
        Assert.True(prior.Renders > rendersBefore);
        Assert.Equal(200, _engine.Devices[0].LedBytes[0]);
        Assert.All(samples, x => Assert.Equal((false, true), x));
    }

    [Fact]
    public void The_opening_post_returns_an_id_and_a_renewal_with_it_keeps_the_session()
    {
        var open = _provider.StartStudioLive(Body());
        Assert.False(string.IsNullOrEmpty(open.SessionId));

        var renew = _provider.StartStudioLive(Body(sessionId: open.SessionId));

        Assert.True(renew.Ok);
        Assert.Equal(open.SessionId, renew.SessionId);
    }

    [Fact]
    public void A_renewal_with_an_unknown_id_is_ended_and_starts_nothing()
    {
        var result = _provider.StartStudioLive(Body(sessionId: "nope"));

        Assert.Equal(StudioLiveOutcome.Ended, result.Outcome);
        Assert.Equal("session_ended", result.Message);
        Assert.False(_provider.StudioLiveActive);
    }

    [Fact]
    public void A_renewal_after_a_conflict_ended_the_session_is_ended_and_leaves_the_other_effect_alone()
    {
        var open = _provider.StartStudioLive(Body());
        _provider.StartAnimate(Animate("plasma"));
        var other = _engine.CurrentEffect;

        var renew = _provider.StartStudioLive(Body(sessionId: open.SessionId));

        Assert.Equal(StudioLiveOutcome.Ended, renew.Outcome);
        Assert.False(_provider.StudioLiveActive);
        Assert.Same(other, _engine.CurrentEffect);
    }

    [Fact]
    public void A_second_post_without_an_id_takes_over_keeps_the_snapshot_and_retires_the_old_id()
    {
        _provider.StartAnimate(Animate("rainbow"));
        var before = _engine.CurrentEffect;
        var first = _provider.StartStudioLive(Body(CustomFrags.Frag(0.2f)));

        var second = _provider.StartStudioLive(Body(CustomFrags.Frag(0.9f)));

        Assert.NotEqual(first.SessionId, second.SessionId);
        Assert.Equal(StudioLiveOutcome.Ended, _provider.StartStudioLive(Body(sessionId: first.SessionId)).Outcome);
        Assert.True(_provider.StudioLiveActive);
        _provider.StopStudioLive(second.SessionId);
        Assert.Same(before, _engine.CurrentEffect);
    }

    [Fact]
    public void Stop_with_a_stale_id_is_a_no_op_and_without_an_id_ends_whatever_runs()
    {
        var open = _provider.StartStudioLive(Body());

        Assert.False(_provider.StopStudioLive("stale"));
        Assert.True(_provider.StudioLiveActive);
        Assert.True(_provider.StopStudioLive());
        Assert.False(_provider.StudioLiveActive);
        Assert.False(_provider.StopStudioLive(open.SessionId));
    }

    [Fact]
    public void A_lease_lapse_during_a_blackout_restores_the_effect_without_lighting_the_devices()
    {
        _provider.StartAnimate(Animate("rainbow"));
        var before = _engine.CurrentEffect;
        _provider.StartStudioLive(Body());
        _engine.SetBlackout(true);

        _now += 20_000;
        _provider.CheckStudioLease();

        Assert.Same(before, _engine.CurrentEffect);
        Assert.True(_engine.Blackout);
        _engine.SetBlackout(false);
        Assert.Same(before, _engine.CurrentEffect);
    }

    [Fact]
    public void Dropping_the_custom_effect_the_session_replaced_restores_it_first()
    {
        var id = CustomFrags.NewId();
        ShaderLibrary.RegisterCustom(id, CustomFrags.Frag());
        try
        {
            _provider.StartAnimate(Animate("custom:" + id));
            _provider.StartStudioLive(Body());

            _provider.DropCustomEffect("custom:" + id);

            Assert.False(_provider.StudioLiveActive);
            Assert.NotEqual(LightingProvider.StudioLiveKey, _engine.CurrentEffectName);
            Assert.Equal("rainbow", _engine.CurrentEffectName);
        }
        finally
        {
            ShaderLibrary.UnregisterCustom(id);
        }
    }

    [Fact]
    public void Saving_the_custom_effect_the_session_replaced_keeps_the_session_and_restores_the_new_source()
    {
        var id = CustomFrags.NewId();
        ShaderLibrary.RegisterCustom(id, CustomFrags.Frag(0.2f));
        try
        {
            _provider.StartAnimate(Animate("custom:" + id));
            var old = _engine.CurrentEffect;
            var open = _provider.StartStudioLive(Body());
            ShaderLibrary.RegisterCustom(id, CustomFrags.Frag(0.8f));

            _provider.ReloadCustomEffect("custom:" + id);

            Assert.True(_provider.StudioLiveActive);
            Assert.True(_provider.StartStudioLive(Body(sessionId: open.SessionId)).Ok);
            _provider.StopStudioLive();
            Assert.NotSame(old, _engine.CurrentEffect);
            Assert.Equal("custom:" + id, _engine.CurrentEffectName);
        }
        finally
        {
            ShaderLibrary.UnregisterCustom(id);
        }
    }

    [Fact]
    public void Reloading_the_reserved_preview_key_does_not_orphan_the_preview()
    {
        _provider.StartStudioLive(Body());
        var live = _engine.CurrentEffect;

        _provider.ReloadCustomEffect(LightingProvider.StudioLiveKey);

        Assert.Same(live, _engine.CurrentEffect);
        Assert.True(_provider.StudioLiveActive);
    }

    [Fact]
    public void A_paused_restore_during_a_blackout_pauses_after_the_hold_ends()
    {
        var prior = new FakeEffect("prior", red: 90);
        _engine.SetEffect(prior);
        WaitFor(() => prior.Renders > 0);
        _engine.SetPaused(true);
        _provider.StartStudioLive(Body());
        _engine.SetBlackout(true);

        _now += 20_000;
        _provider.CheckStudioLease();

        Assert.Same(prior, _engine.CurrentEffect);
        Assert.True(_engine.PausePending);
        Assert.True(_provider.IsPaused);
        var rendersBefore = prior.Renders;
        _engine.SetBlackout(false);
        WaitFor(() => _engine.Paused);
        Assert.True(_engine.Paused);
        Assert.False(_engine.PausePending);
        Assert.True(prior.Renders > rendersBefore);
    }

    [Fact]
    public void A_session_leaves_uncontrolled_and_led_off_devices_as_saved()
    {
        _config.Update(c =>
        {
            c.Devices.DisabledLightingDevices = ["off"];
            c.Devices.UncontrolledLightingDevices = ["free"];
        });
        var before = SettingsJson();

        _provider.StartStudioLive(Body());

        Assert.Equal(["off"], _config.Load().Devices.DisabledLightingDevices);
        Assert.Equal(["free"], _config.Load().Devices.UncontrolledLightingDevices);
        _provider.StopStudioLive();
        Assert.Equal(before, SettingsJson());
    }

    [Fact]
    public void A_client_effect_that_lands_while_a_start_adopts_a_pending_snapshot_wins_and_is_not_leaked()
    {
        var prior = new FakeEffect("prior");
        _engine.SetEffect(prior);
        _provider.StartStudioLive(Body());
        _provider.DetachStudioLive();
        var other = new FakeEffect("other");
        _provider.BeforeStudioStartSwap = () =>
        {
            // From another thread, as a real client would; its change handler waits for the lock this start holds.
            _ = Task.Run(() => _engine.SetEffect(other));
            WaitFor(() => ReferenceEquals(_engine.CurrentEffect, other));
        };

        Assert.True(_provider.StartStudioLive(Body(CustomFrags.Frag(0.9f))).Ok);
        _provider.BeforeStudioStartSwap = null;

        Assert.True(prior.Disposed);
        Assert.False(other.Disposed);
        Assert.True(_provider.StudioLiveActive);
        _provider.StopStudioLive();
        Assert.Same(other, _engine.CurrentEffect);
    }

    [Fact]
    public void A_background_load_of_an_inactive_profile_does_not_settle_someone_elses_handoff()
    {
        var pm = new ProfileManager(_config);
        pm.Initialize();
        pm.CreateProfile("Active");
        pm.ProfileLoading += _provider.DetachStudioLive;
        pm.ProfileLoadSettled += _provider.FinishStudioLiveHandoff;
        var prior = new FakeEffect("prior");
        _engine.SetEffect(prior);
        _provider.StartStudioLive(Body());
        _provider.DetachStudioLive();

        pm.ImportProfile("Pulled", new NexusSettings());
        pm.SwitchProfile(pm.GetManifest().ActiveProfileId);

        Assert.False(prior.Disposed);
        Assert.Equal(LightingProvider.StudioLiveKey, _engine.CurrentEffectName);
        _provider.FinishStudioLiveHandoff();
        Assert.Same(prior, _engine.CurrentEffect);
    }

    [Fact]
    public void A_throwing_settle_handler_does_not_replace_the_calls_own_exception()
    {
        var pm = new ProfileManager(_config);
        pm.Initialize();
        pm.CreateProfile("Other");
        var first = pm.GetManifest().Profiles[0].Id;
        pm.ProfileLoadSettled += () => throw new ApplicationException("settle failed");
        pm.OnProfileSwitched += () => throw new InvalidOperationException("original");

        var ex = Assert.Throws<InvalidOperationException>(() => pm.SwitchProfile(first));

        Assert.Equal("original", ex.Message);
    }
}
