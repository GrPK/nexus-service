using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Media;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests.Lighting.CustomEffects;

/// <summary>Devices pinned to a Studio custom effect: validation, the pinned look, deletion and audio demand.</summary>
public sealed class CustomEffectPinTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-custom-pin-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _id = CustomFrags.NewId();
    private readonly JsonConfigStore _config;
    private readonly LightingEngine _engine = new();
    private readonly GpuContext _gpu = new(160, 90);
    private readonly LightingProvider _provider;
    private readonly FakeBeats _beats = new();

    public CustomEffectPinTests()
    {
        Directory.CreateDirectory(_dir);
        _config = new JsonConfigStore(Path.Combine(_dir, "settings.json"));
        _provider = new LightingProvider(_config, _engine, new LightingOutputHub(), _gpu, new MediaLibrary(),
            new Nexus.Service.Platform.DefaultMonitorEnumerator(), beats: _beats);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _gpu.Dispose();
        ShaderLibrary.UnregisterCustom(_id);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class FakeBeats : Nexus.Service.Activity.IBeatsProvider
    {
        public bool Running;
        public event Action? OnBeat { add { } remove { } }
        public void Start() => Running = true;
        public void Stop() => Running = false;
        public void Dispose() { }
    }

    private string Key => "custom:" + _id;

    private static DevicePin Animate(string effect) => new() { Kind = DevicePinKinds.Animate, Effect = effect };

    [Fact]
    public void A_known_custom_key_is_an_acceptable_animate_pin_and_an_unknown_one_is_not()
    {
        Assert.NotNull(DevicePinTracker.Validate(Animate(Key)));

        ShaderLibrary.RegisterCustom(_id, CustomFrags.Frag());

        Assert.Null(DevicePinTracker.Validate(Animate(Key)));
        Assert.NotNull(DevicePinTracker.Validate(Animate("custom:nosuchid")));
        Assert.NotNull(DevicePinTracker.Validate(Animate("custom:")));
    }

    [Fact]
    public void A_pinned_custom_effect_with_no_saved_preset_builds_a_shader_under_its_key()
    {
        ShaderLibrary.RegisterCustom(_id, CustomFrags.Frag());

        _config.Update(s => s.Lighting.DevicePins["dev"] = Animate(Key));

        var effect = _engine.PinEffectFactory!(Key, null);
        var shader = Assert.IsType<ShaderEffect>(effect);
        Assert.Equal(Key, shader.Name);
        Assert.NotNull(_engine.PinLookTag!(Key));
        Assert.Equal(1f, shader.Speed);
    }

    [Fact]
    public void A_saved_preset_slot_is_the_pinned_look()
    {
        ShaderLibrary.RegisterCustom(_id, CustomFrags.Frag());
        _config.Update(s =>
        {
            s.Lighting.Animate.Templates[Key] = new AnimateEffectTemplates
            {
                Selected = 0,
                Slots = [new AnimateEffectState { Speed = 100, Hue = 0.25f }, new(), new(), new()],
            };
            s.Lighting.DevicePins["dev"] = Animate(Key);
        });

        var shader = Assert.IsType<ShaderEffect>(_engine.PinEffectFactory!(Key, null));

        Assert.Equal(2f, shader.Speed);
        Assert.Equal(0.25f, shader.Hue);
    }

    [Fact]
    public void A_changed_source_rebuilds_the_pinned_effect_and_changes_its_tag()
    {
        ShaderLibrary.RegisterCustom(_id, CustomFrags.Frag(0.2f));
        _config.Update(s => s.Lighting.DevicePins["dev"] = Animate(Key));
        var tagBefore = _engine.PinLookTag!(Key);
        var before = _engine.PinEffectFactory!(Key, null);

        ShaderLibrary.RegisterCustom(_id, CustomFrags.Frag(0.9f));
        _provider.ReloadCustomEffect(Key);

        Assert.NotEqual(tagBefore, _engine.PinLookTag!(Key));
        Assert.NotSame(before, _engine.PinEffectFactory!(Key, before));
    }

    [Fact]
    public void Deleting_the_effect_unpins_its_devices_and_leaves_other_pins()
    {
        ShaderLibrary.RegisterCustom(_id, CustomFrags.Frag());
        _config.Update(s =>
        {
            s.Lighting.DevicePins["a"] = Animate(Key);
            s.Lighting.DevicePins["b"] = Animate("rainbow");
        });

        _provider.DropCustomEffect(Key);

        var pins = _config.Load().Lighting.DevicePins;
        Assert.False(pins.ContainsKey("a"));
        Assert.True(pins.ContainsKey("b"));
        Assert.Null(_engine.PinLookTag!(Key));
    }

    [Fact]
    public void A_device_pinned_to_an_audio_custom_effect_holds_audio_capture()
    {
        ShaderLibrary.RegisterCustom(_id, CustomFrags.Frag(audio: true));
        _config.Update(s => s.Lighting.MusicReactive = true);

        _config.Update(s => s.Lighting.DevicePins["dev"] = Animate(Key));
        Assert.True(_beats.Running);

        _config.Update(s => s.Lighting.DevicePins.Remove("dev"));
        Assert.False(_beats.Running);
    }

    [Fact]
    public void A_look_change_updates_the_pinned_shader_in_place_and_only_a_source_change_rebuilds()
    {
        ShaderLibrary.RegisterCustom(_id, CustomFrags.Frag(0.2f));
        _config.Update(s => s.Lighting.DevicePins["dev"] = Animate(Key));
        var first = _engine.PinEffectFactory!(Key, null);

        _config.Update(s => s.Lighting.Animate.Templates[Key] = new AnimateEffectTemplates
        {
            Selected = 0,
            Slots = [new AnimateEffectState { Speed = 100 }, new(), new(), new()],
        });
        var afterLook = _engine.PinEffectFactory!(Key, first);
        Assert.Same(first, afterLook);
        Assert.Equal(2f, ((ShaderEffect)afterLook!).Speed);

        ShaderLibrary.RegisterCustom(_id, CustomFrags.Frag(0.9f));
        _provider.ReloadCustomEffect(Key);
        Assert.NotSame(first, _engine.PinEffectFactory!(Key, first));
    }

    [Fact]
    public void Deleting_the_effect_also_strips_it_from_layout_presets()
    {
        ShaderLibrary.RegisterCustom(_id, CustomFrags.Frag());
        _config.Update(s => s.Lighting.LayoutPresets.Add(new LayoutPreset
        {
            Id = "p",
            DevicePins = new() { ["a"] = Animate(Key), ["b"] = Animate("rainbow") },
        }));

        _provider.DropCustomEffect(Key);

        var pins = _config.Load().Lighting.LayoutPresets[0].DevicePins!;
        Assert.False(pins.ContainsKey("a"));
        Assert.True(pins.ContainsKey("b"));
    }

    [Fact]
    public void A_pin_on_a_custom_key_the_library_does_not_know_builds_nothing()
    {
        _config.Update(s => s.Lighting.DevicePins["dev"] = Animate(Key));

        Assert.Null(_engine.PinLookTag!(Key));
        Assert.Null(_engine.PinEffectFactory!(Key, null));
    }

    [Fact]
    public void The_recovered_event_refreshes_pinned_looks_resolved_while_the_store_was_unreadable()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var dir2 = Path.Combine(_dir, "store");
        Directory.CreateDirectory(dir2);
        new Nexus.Service.Lighting.CustomEffects.CustomEffectStore(dir2).Put(_id, "keep", "", CustomFrags.Frag());
        ShaderLibrary.UnregisterCustom(_id);
        var path = Path.Combine(dir2, "custom-effects.json");
        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            var store = new Nexus.Service.Lighting.CustomEffects.CustomEffectStore(dir2);
            using var provider = new LightingProvider(_config, new LightingEngine(), new LightingOutputHub(), _gpu, new MediaLibrary(),
                new Nexus.Service.Platform.DefaultMonitorEnumerator(), customEffects: store);
            _config.Update(s => s.Lighting.DevicePins["dev"] = Animate(Key));
            Assert.False(store.EnsureAvailable());
            Assert.Null(PinTag(provider));

            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Assert.True(store.EnsureAvailable());

            Assert.NotNull(PinTag(provider));
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    // The pin look tag a provider's engine would use; engines are private to each provider, so read it through the factory seam.
    private string? PinTag(LightingProvider provider) => provider.PinTagForTest(Key);
}
