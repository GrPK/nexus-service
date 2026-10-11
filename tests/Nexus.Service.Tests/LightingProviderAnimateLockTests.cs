using Nexus.Service.Activity;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Media;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests;

public class LightingProviderAnimateLockTests : IDisposable
{
    private sealed class FakeBeats : IBeatsProvider
    {
        public bool Running;
        public void Start() => Running = true;
        public void Stop() => Running = false;
        public event Action? OnBeat { add { } remove { } }
        public void Dispose() { }
    }

    private readonly string _tempDir;
    private readonly JsonConfigStore _store;
    private readonly LightingEngine _engine;
    private readonly GpuContext _gpu;
    private readonly AnimateDeviceLockTracker _locks;
    private readonly FakeBeats _beats = new();
    private readonly LightingProvider _provider;

    public LightingProviderAnimateLockTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-provider-animlock-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _store = new JsonConfigStore(Path.Combine(_tempDir, "settings.json"));
        _locks = new AnimateDeviceLockTracker(_store);
        _engine = new LightingEngine { AnimateLocks = _locks };
        _gpu = new GpuContext(160, 90);
        _provider = new LightingProvider(_store, _engine, new LightingOutputHub(), _gpu, new MediaLibrary(),
            new Nexus.Service.Platform.DefaultMonitorEnumerator(), beats: _beats);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _gpu.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void An_audio_effect_lock_turns_capture_on_and_unlocking_turns_it_off()
    {
        _store.Update(s => s.Lighting.MusicReactive = true);
        Assert.False(_beats.Running);

        _locks.Set("dev-a", "basspulse");
        Assert.True(_beats.Running);

        _locks.Clear("dev-a");
        Assert.False(_beats.Running);
    }

    [Fact]
    public void A_non_audio_lock_leaves_capture_off()
    {
        _store.Update(s => s.Lighting.MusicReactive = true);
        _locks.Set("dev-a", "plasma");
        Assert.False(_beats.Running);
    }

    [Fact]
    public void A_template_save_moves_the_locked_effects_look()
    {
        _locks.Set("dev-a", "plasma");
        var before = _engine.AnimateLockLookTag!("plasma");
        Assert.NotNull(before);

        var slot = AnimateTemplateDefaults.ResolveSelected(_store.Load().Lighting.Animate.Templates, "plasma")!;
        _provider.SaveAnimateTemplates(new Dictionary<string, AnimateEffectTemplates>
        {
            ["plasma"] = new()
            {
                Selected = 0,
                Slots = new List<AnimateEffectState?>
                {
                    new AnimateEffectState
                    {
                        Speed = slot.Speed,
                        Intensity = slot.Intensity,
                        Hue = slot.Hue + 0.31f,
                        Colorize = slot.Colorize,
                        Saturation = slot.Saturation,
                        Contrast = slot.Contrast,
                    },
                },
            },
        });

        var after = _engine.AnimateLockLookTag!("plasma");
        Assert.NotNull(after);
        Assert.NotEqual(before, after);
    }
}
