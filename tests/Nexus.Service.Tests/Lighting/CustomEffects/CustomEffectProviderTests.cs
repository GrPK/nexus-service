using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.CustomEffects;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Media;
using Nexus.Service.Models.Lighting;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests.Lighting.CustomEffects;

/// <summary>Real provider and engine with an uninitialised GPU: effect objects are built and swapped, nothing renders.</summary>
public sealed class CustomEffectProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-custom-fx-prov-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _id = CustomFrags.NewId();
    private JsonConfigStore _config;
    private LightingEngine _engine;
    private GpuContext _gpu;
    private LightingProvider _provider;

    public CustomEffectProviderTests()
    {
        Directory.CreateDirectory(_dir);
        (_config, _engine, _gpu, _provider) = Boot();
    }

    private string Key => "custom:" + _id;

    private (JsonConfigStore, LightingEngine, GpuContext, LightingProvider) Boot()
    {
        var config = new JsonConfigStore(Path.Combine(_dir, "settings.json"));
        var engine = new LightingEngine();
        var gpu = new GpuContext(160, 90);
        var provider = new LightingProvider(config, engine, new LightingOutputHub(), gpu, new MediaLibrary(),
            new Nexus.Service.Platform.DefaultMonitorEnumerator());
        return (config, engine, gpu, provider);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _gpu.Dispose();
        ShaderLibrary.UnregisterCustom(_id);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static AnimateHeadlessStart Start(string effect, int speed = 50, params (string, float)[] extras) => new()
    {
        Effect = effect,
        Speed = speed,
        Intensity = 1f,
        Saturation = 1f,
        Contrast = 1f,
        Persist = true,
        Params = extras.Select(e => new ShaderParam { Name = e.Item1, Value = e.Item2 }).ToList(),
    };

    [Fact]
    public void A_custom_key_runs_as_a_shader_named_after_the_key()
    {
        ShaderLibrary.RegisterCustom(_id, CustomFrags.Frag());

        _provider.StartAnimate(Start(Key));

        var shader = Assert.IsType<ShaderEffect>(_engine.CurrentEffect);
        Assert.Equal(Key, shader.Name);
        Assert.Equal(Key, _provider.GetSync());
    }

    [Fact]
    public void An_unknown_custom_id_falls_back_to_a_rainbow_shader_without_throwing()
    {
        _provider.StartAnimate(Start(Key));

        Assert.IsType<ShaderEffect>(_engine.CurrentEffect);
    }

    [Fact]
    public void Static_mode_rejects_a_custom_key()
    {
        ShaderLibrary.RegisterCustom(_id, CustomFrags.Frag());

        Assert.Throws<ArgumentException>(() => _provider.StartStatic(new StaticHeadlessStart { Effect = Key }));
    }

    [Fact]
    public void Reload_rebuilds_the_running_effect_and_keeps_its_params()
    {
        ShaderLibrary.RegisterCustom(_id, CustomFrags.Frag());
        _provider.StartAnimate(Start(Key, 80, ("u_b1_gain", 1.25f)));
        var before = _engine.CurrentEffect;

        ShaderLibrary.RegisterCustom(_id, CustomFrags.Frag(gainDefault: 1.0f));
        _provider.ReloadCustomEffect(Key);

        var after = Assert.IsType<ShaderEffect>(_engine.CurrentEffect);
        Assert.NotSame(before, after);
        Assert.Equal(Key, after.Name);
        Assert.Equal(80 / 50f, after.Speed);
        Assert.Equal(1.25f, after.ExtraParams!["u_b1_gain"]);
    }

    [Fact]
    public void Reload_leaves_a_different_running_effect_alone()
    {
        ShaderLibrary.RegisterCustom(_id, CustomFrags.Frag());
        _provider.StartAnimate(Start("plasma"));
        var before = _engine.CurrentEffect;

        _provider.ReloadCustomEffect(Key);

        Assert.Same(before, _engine.CurrentEffect);
    }

    [Fact]
    public void Drop_switches_the_running_custom_effect_to_rainbow_and_clears_its_saved_look()
    {
        ShaderLibrary.RegisterCustom(_id, CustomFrags.Frag());
        _provider.StartAnimate(Start(Key, 80, ("u_b1_gain", 1.25f)));
        Assert.True(_config.Load().Lighting.Animate.States.ContainsKey(Key));

        ShaderLibrary.UnregisterCustom(_id);
        _provider.DropCustomEffect(Key);

        Assert.Equal("rainbow", _engine.CurrentEffectName);
        Assert.Equal("rainbow", _config.Load().Lighting.Sync);
        Assert.False(_config.Load().Lighting.Animate.States.ContainsKey(Key));
    }

    [Fact]
    public void Drop_leaves_a_different_running_effect_alone()
    {
        _provider.StartAnimate(Start("plasma"));

        _provider.DropCustomEffect(Key);

        Assert.Equal("plasma", _engine.CurrentEffectName);
    }

    [Fact]
    public void Thumbnails_are_unavailable_for_an_unknown_custom_key_and_without_a_gpu()
    {
        Assert.Null(_provider.CaptureAnimateThumbnail(Key, 0));
        ShaderLibrary.RegisterCustom(_id, CustomFrags.Frag());
        Assert.False(_gpu.Available);
        Assert.Null(_provider.CaptureAnimateThumbnail(Key, 0));
    }

    [Fact]
    public void A_running_custom_effect_survives_a_restart_with_its_saved_look()
    {
        var store = new CustomEffectStore(_dir);
        store.Put(_id, "Mine", "{}", CustomFrags.Frag());
        _provider.StartAnimate(Start(Key, 80, ("u_b1_gain", 1.25f)));
        _config.FlushNow();
        _provider.Dispose();
        _gpu.Dispose();
        ShaderLibrary.UnregisterCustom(_id);

        // A fresh process: nothing in memory but the files.
        var reloadedStore = new CustomEffectStore(_dir);
        (_config, _engine, _gpu, _provider) = Boot();
        LiveEngineSync.ApplyLighting(_config, _provider);

        var shader = Assert.IsType<ShaderEffect>(_engine.CurrentEffect);
        Assert.Equal(Key, shader.Name);
        Assert.Equal(80 / 50f, shader.Speed);
        Assert.Equal(1.25f, shader.ExtraParams!["u_b1_gain"]);
        Assert.NotNull(reloadedStore.Get(_id));
    }
}
