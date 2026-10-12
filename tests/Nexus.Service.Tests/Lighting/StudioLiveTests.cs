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

    private sealed class FakeEffect(string name) : IEffect
    {
        public string Name { get; } = name;
        public bool Disposed { get; private set; }
        public void RenderFrame(CanvasBuffer canvas, double tickMs) { }
        public void Dispose() => Disposed = true;
    }

    private static StudioLiveBody Body(string? frag = null, int speed = 50, float hue = 0f) => new()
    {
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
    public void Led_off_and_uncontrolled_devices_are_driven_in_memory_only()
    {
        _config.Update(s =>
        {
            s.Devices.DisabledLightingDevices = ["dev-off"];
            s.Devices.UncontrolledLightingDevices = ["dev-free", "dev-free-2"];
        });
        var before = SettingsJson();

        var result = _provider.StartStudioLive(Body());

        Assert.Equal(3, result.DevicesForced);
        var live = _config.Load().Devices;
        Assert.Empty(live.DisabledLightingDevices);
        Assert.Empty(live.UncontrolledLightingDevices);
        Assert.Equal(before, SettingsJson());
        Assert.Contains("dev-free-2", SettingsJson());

        _provider.StopStudioLive();
        Assert.Equal(["dev-off"], _config.Load().Devices.DisabledLightingDevices);
        Assert.Equal(["dev-free", "dev-free-2"], _config.Load().Devices.UncontrolledLightingDevices);
        Assert.Equal(before, SettingsJson());
    }

    [Fact]
    public void A_settings_write_during_a_session_starts_from_the_saved_lists()
    {
        _config.Update(s => s.Devices.UncontrolledLightingDevices = ["dev-free"]);
        _provider.StartStudioLive(Body());

        LightingControlledState.SetControlled("dev-new", false, _config);

        var json = SettingsJson();
        Assert.Contains("dev-free", json);
        Assert.Contains("dev-new", json);
        Assert.Equal(["dev-new"], _config.Load().Devices.UncontrolledLightingDevices);
        _provider.StopStudioLive();
        Assert.Equal(["dev-free", "dev-new"], _config.Load().Devices.UncontrolledLightingDevices);
    }

    [Fact]
    public void A_detached_openrgb_device_is_reported_skipped_not_forced()
    {
        _config.Update(s =>
        {
            s.Devices.UncontrolledLightingDevices = ["openrgb-s-AAA", "openrgb-s-AAA-1", "dev-free"];
            s.Devices.OpenRgbDetectorExclusions = new() { ["openrgb-s-AAA"] = new OpenRgbDetectorExclusion { DetectorName = "d", DeviceName = "n" } };
        });
        var before = SettingsJson();

        var result = _provider.StartStudioLive(Body());

        Assert.Equal(1, result.DevicesForced);
        Assert.Equal(["openrgb-s-AAA", "openrgb-s-AAA-1"], result.Skipped);
        Assert.Contains("openrgb-s-AAA", _config.Load().Devices.UncontrolledLightingDevices);
        _provider.StopStudioLive();
        Assert.Equal(before, SettingsJson());
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
    public void A_saved_mode_change_that_leaves_the_engine_alone_restores_the_prior_effect()
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
}
