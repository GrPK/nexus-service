using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Effects;

namespace Nexus.Service.Tests;

public class AnimateDeviceLockTrackerTests
{
    [Fact]
    public void Lock_and_unlock_bump_the_version_and_reach_the_store()
    {
        var store = new InMemoryConfigStore();
        var tracker = new AnimateDeviceLockTracker(store);
        var v0 = tracker.Version;

        Assert.True(tracker.Set("dev-a", "plasma"));
        Assert.True(tracker.TryGet("dev-a", out var effect));
        Assert.Equal("plasma", effect);
        Assert.Equal("plasma", store.Load().Lighting.AnimateDeviceLocks["dev-a"]);
        var v1 = tracker.Version;
        Assert.True(v1 > v0);

        // Re-locking onto the same effect changes nothing.
        tracker.Set("dev-a", "plasma");
        Assert.Equal(v1, tracker.Version);

        // A different effect replaces the lock.
        tracker.Set("dev-a", "fire");
        Assert.Equal("fire", store.Load().Lighting.AnimateDeviceLocks["dev-a"]);
        Assert.True(tracker.Version > v1);

        tracker.Clear("dev-a");
        Assert.False(tracker.IsLocked("dev-a"));
        Assert.Empty(store.Load().Lighting.AnimateDeviceLocks);
    }

    [Fact]
    public void Locks_are_rehydrated_after_a_restart_and_after_a_store_swap()
    {
        var store = new InMemoryConfigStore();
        new AnimateDeviceLockTracker(store).Set("dev-a", "plasma");

        var reborn = new AnimateDeviceLockTracker(store);
        Assert.True(reborn.TryGet("dev-a", out var effect));
        Assert.Equal("plasma", effect);

        // A profile switch replaces the whole dictionary.
        var v = reborn.Version;
        store.Update(s => s.Lighting.AnimateDeviceLocks = new Dictionary<string, string> { ["dev-b"] = "fire" });
        Assert.False(reborn.IsLocked("dev-a"));
        Assert.True(reborn.IsLocked("dev-b"));
        Assert.True(reborn.Version > v);
    }

    [Fact]
    public void An_unrelated_store_write_neither_bumps_the_version_nor_loses_the_lock()
    {
        var store = new InMemoryConfigStore();
        var tracker = new AnimateDeviceLockTracker(store);
        tracker.Set("dev-a", "plasma");
        var v = tracker.Version;

        store.Update(s => s.Lighting.Sync = "animate");

        Assert.Equal(v, tracker.Version);
        Assert.True(tracker.IsLocked("dev-a"));
    }

    [Fact]
    public void A_restored_conflict_keeps_the_animation_lock_and_unlocks_the_static_look()
    {
        var store = new InMemoryConfigStore();
        var tracker = new AnimateDeviceLockTracker(store);
        tracker.Set("dev-a", "plasma");

        // A preset activate can restore a locked static look onto an animation-locked device.
        store.Update(s => s.Lighting.StaticDeviceLooks["dev-a"] =
            new Nexus.Service.Persistence.StaticDeviceLook { Effect = "simplered", Locked = true });

        Assert.True(tracker.IsLocked("dev-a"));
        Assert.False(store.Load().Lighting.StaticDeviceLooks["dev-a"].Locked);
        Assert.Equal("simplered", store.Load().Lighting.StaticDeviceLooks["dev-a"].Effect);
    }

    [Theory]
    [InlineData("rainbow", true)]
    [InlineData("plasma", true)]
    [InlineData("gradientlinear", false)]
    [InlineData("simplered", false)]
    [InlineData("nope", false)]
    [InlineData("", false)]
    public void Only_animate_shaders_can_be_locked(string key, bool expected)
    {
        Assert.Equal(expected, AnimateDeviceLockTracker.IsAnimateEffect(key));
    }
}

public class AnimateDeviceLockEngineTests
{
    private sealed class Fill : IEffect
    {
        public byte R, G, B;
        public bool Disposed;
        public bool Throws;
        public Fill(string name, byte r, byte g, byte b) { Name = name; R = r; G = g; B = b; }
        public string Name { get; }
        public void RenderFrame(CanvasBuffer canvas, double tickMs)
        {
            if (Throws) throw new InvalidOperationException("boom");
            for (int y = 0; y < canvas.Height; y++)
                for (int x = 0; x < canvas.Width; x++)
                    canvas.SetPixel(x, y, R, G, B);
        }
        public void Dispose() { Disposed = true; }
    }

    private static DeviceFrame Device(string id = "dev-a") =>
        new(0, id, 5, x: 300, y: 100, w: 40, h: 250, rotation: 90);

    private static async Task<byte[]> WaitFor(DeviceFrame dev, Func<byte[], bool> ok)
    {
        for (var i = 0; i < 200; i++)
        {
            var leds = dev.LedBytes.ToArray();
            if (ok(leds)) return leds;
            await Task.Delay(10);
        }
        return dev.LedBytes.ToArray();
    }

    private static LightingEngine Engine(AnimateDeviceLockTracker locks, DeviceFrame dev, Func<string, IEffect?, IEffect?> factory, Func<string, string?>? tag = null)
    {
        var engine = new LightingEngine
        {
            AnimateLocks = locks,
            AnimateLockEffectFactory = factory,
            AnimateLockLookTag = tag ?? (_ => "t"),
            FrameIntervalMs = 10,
        };
        engine.UpdateDevices(new[] { dev });
        return engine;
    }

    [Fact]
    public async Task A_locked_device_shows_its_effect_while_the_main_effect_differs()
    {
        var locks = new AnimateDeviceLockTracker();
        locks.Set("dev-a", "red");
        var dev = Device();
        using var engine = Engine(locks, dev, (_, _) => new Fill("red", 255, 0, 0));
        engine.SetEffect(new Fill("green", 0, 255, 0));

        var leds = await WaitFor(dev, l => l[0] == 255);
        Assert.Equal(255, leds[0]);
        Assert.Equal(0, leds[1]);
    }

    [Fact]
    public async Task An_unlocked_device_keeps_sampling_the_main_canvas()
    {
        var locks = new AnimateDeviceLockTracker();
        locks.Set("someone-else", "red");
        var dev = Device();
        using var engine = Engine(locks, dev, (_, _) => new Fill("red", 255, 0, 0));
        engine.SetEffect(new Fill("green", 0, 255, 0));

        var leds = await WaitFor(dev, l => l[1] == 255);
        Assert.Equal(255, leds[1]);
        Assert.Equal(0, leds[0]);
    }

    [Fact]
    public async Task A_lock_onto_the_active_effect_samples_the_main_canvas_without_a_second_render()
    {
        var locks = new AnimateDeviceLockTracker();
        locks.Set("dev-a", "wave");
        var dev = Device();
        var builds = 0;
        using var engine = Engine(locks, dev, (_, _) => { builds++; return new Fill("wave", 255, 0, 0); });
        engine.SetEffect(new Fill("wave", 0, 255, 0));

        var leds = await WaitFor(dev, l => l[1] == 255);
        Assert.Equal(255, leds[1]);
        Assert.Equal(0, builds);
    }

    [Fact]
    public async Task An_animation_lock_beats_a_static_assignment_and_works_outside_static_mode()
    {
        var statics = new StaticDeviceEffectTracker { Enabled = true };
        statics.Set("dev-a", new StaticDeviceAssignment { Effect = "blue", Locked = true });
        var locks = new AnimateDeviceLockTracker();
        locks.Set("dev-a", "red");
        var dev = Device();
        using var engine = Engine(locks, dev, (_, _) => new Fill("red", 255, 0, 0));
        engine.StaticEffects = statics;
        engine.StaticEffectFactory = _ => new Fill("blue", 0, 0, 255);
        engine.SetEffect(new Fill("green", 0, 255, 0));

        var leds = await WaitFor(dev, l => l[0] == 255);
        Assert.Equal(255, leds[0]);
        Assert.Equal(0, leds[2]);
    }

    [Fact]
    public async Task A_changed_look_updates_the_running_effect_in_place()
    {
        // A store change is what tells the engine to look again.
        var store = new InMemoryConfigStore();
        var locks = new AnimateDeviceLockTracker(store);
        locks.Set("dev-a", "red");
        var tag = "one";
        var dev = Device();
        Fill? built = null;
        var builds = 0;
        using var engine = Engine(locks, dev, (_, existing) =>
        {
            if (existing is Fill f)
            {
                f.R = 10;
                f.B = 200;
                return f;
            }
            builds++;
            return built = new Fill("red", 255, 0, 0);
        }, _ => tag);
        engine.SetEffect(new Fill("green", 0, 255, 0));
        await WaitFor(dev, l => l[0] == 255);

        tag = "two";
        store.Update(s => s.Lighting.Sync = "touched");
        var leds = await WaitFor(dev, l => l[2] == 200);

        Assert.Equal(200, leds[2]);
        Assert.Equal(1, builds);
        Assert.NotNull(built);
    }

    [Fact]
    public async Task A_lock_for_an_absent_device_builds_nothing()
    {
        var locks = new AnimateDeviceLockTracker();
        locks.Set("not-here", "red");
        var dev = Device();
        var builds = 0;
        using var engine = Engine(locks, dev, (_, _) => { builds++; return new Fill("red", 255, 0, 0); });
        engine.SetEffect(new Fill("green", 0, 255, 0));

        await WaitFor(dev, l => l[1] == 255);
        await Task.Delay(100);
        Assert.Equal(0, builds);
    }

    [Fact]
    public async Task A_locked_effect_that_throws_lets_the_device_fall_through()
    {
        var locks = new AnimateDeviceLockTracker();
        locks.Set("dev-a", "red");
        var dev = Device();
        using var engine = Engine(locks, dev, (_, _) => new Fill("red", 255, 0, 0) { Throws = true });
        engine.SetEffect(new Fill("green", 0, 255, 0));

        var leds = await WaitFor(dev, l => l[1] == 255);
        Assert.Equal(255, leds[1]);
        Assert.Equal(0, leds[0]);
    }

    [Fact]
    public async Task Unlocking_releases_the_device_and_disposes_the_instance()
    {
        var locks = new AnimateDeviceLockTracker();
        locks.Set("dev-a", "red");
        var dev = Device();
        Fill? built = null;
        using var engine = Engine(locks, dev, (_, _) => built = new Fill("red", 255, 0, 0));
        engine.SetEffect(new Fill("green", 0, 255, 0));
        await WaitFor(dev, l => l[0] == 255);

        locks.Clear("dev-a");
        var leds = await WaitFor(dev, l => l[1] == 255);

        Assert.Equal(255, leds[1]);
        Assert.NotNull(built);
        for (var i = 0; i < 100 && !built!.Disposed; i++) await Task.Delay(10);
        Assert.True(built!.Disposed);
    }
}
