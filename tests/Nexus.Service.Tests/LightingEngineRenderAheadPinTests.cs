using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Effects;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests;

public class LightingEngineRenderAheadPinTests
{
    private static DeviceFrame[] Devices() => new[]
    {
        new DeviceFrame(0, "d-0", 4, x: 0, y: 100),
        new DeviceFrame(1, "d-1", 4, x: 100, y: 100),
        new DeviceFrame(2, "d-2", 4, x: 200, y: 100),
    };

    private static DevicePin Pin(string kind, string? effect = null) => new() { Kind = kind, Effect = effect };

    // Red follows the render time, so a window sampled from "now" instead of its own ticks shows the wrong value.
    private sealed class TickRedEffect : IClockOnlyEffect
    {
        public string Name => "tickred";
        public void RenderFrame(CanvasBuffer canvas, double tickMs) => canvas.Fill((byte)(tickMs % 256), 0, 0);
        public void Dispose() { }
    }

    private sealed class RampEffect : IClockOnlyEffect
    {
        public string Name => "ramp";
        public void RenderFrame(CanvasBuffer canvas, double tickMs)
        {
            for (var y = 0; y < canvas.Height; y++)
            {
                for (var x = 0; x < canvas.Width; x++)
                {
                    var t = canvas.Width > 1 ? (float)x / (canvas.Width - 1) : 0f;
                    canvas.SetPixel(x, y, (byte)(255 * (1 - t)), 0, (byte)(255 * t));
                }
            }
        }
        public void Dispose() { }
    }

    // Stands for a source that reads live input: not clock-only, so it must not roll.
    private sealed class LiveInputEffect : IEffect
    {
        public string Name => "live";
        public void RenderFrame(CanvasBuffer canvas, double tickMs) => canvas.Fill(9, 9, 9);
        public void Dispose() { }
    }

    private sealed class Rig : IDisposable
    {
        public readonly DevicePinTracker Pins = new();
        public readonly LightingEngine Engine;
        public string Tag = "t";
        public Func<string, IEffect?> Build = _ => new TickRedEffect();
        private readonly bool _anchor;

        // d-2 carries a working animate pin, which is what lets a non-shader global roll; every request includes it.
        public Rig(IEffect? global = null, bool anchor = true)
        {
            _anchor = anchor;
            Engine = new LightingEngine
            {
                Pins = Pins,
                FrameIntervalMs = 10,
                PinLookTag = key => key == "anchor" ? "a" : Tag,
                PinEffectFactory = (key, _) => key == "anchor" ? new TickRedEffect() : Build(key),
                StaticPinEffectFactory = _ => new FlatColorEffect(0, 0, 255),
            };
            Engine.UpdateDevices(Devices());
            Engine.SetEffect(global ?? new FlatColorEffect(0, 255, 0));
            if (anchor)
            {
                Pins.Set("d-2", Pin(DevicePinKinds.Animate, "anchor"));
            }
        }

        private string[] With(string[] ids) => _anchor ? ids.Append("d-2").ToArray() : ids;

        public bool Can(params string[] ids) => Engine.CanRenderAhead(With(ids));

        public void Dispose() => Engine.Dispose();

        public async Task<bool> RollsEventually(params string[] ids)
        {
            var end = Environment.TickCount64 + 5000;
            while (Environment.TickCount64 < end)
            {
                if (Can(ids)) return true;
                await Task.Delay(10);
            }
            return false;
        }

        public async Task<bool> RollsEventuallyBriefly(params string[] ids)
        {
            await WaitFrames(60);
            return Can(ids);
        }

        public async Task WaitFrames(int frames)
        {
            var seen = 0;
            Engine.OnFrame += _ => Interlocked.Increment(ref seen);
            var end = Environment.TickCount64 + 5000;
            while (Volatile.Read(ref seen) < frames && Environment.TickCount64 < end)
            {
                await Task.Delay(5);
            }
        }

        public async Task<bool> BecomesFalse(params string[] ids)
        {
            var end = Environment.TickCount64 + 5000;
            while (Environment.TickCount64 < end)
            {
                if (!Can(ids)) return true;
                await Task.Delay(2);
            }
            return false;
        }

        public async Task<DeviceFrame[][]> Window(long[] ticks, params string[] ids)
        {
            Assert.True(await RollsEventually(ids));
            var req = Engine.RequestAhead(With(ids), ticks);
            Assert.True(req.Done.Wait(TimeSpan.FromSeconds(5)));
            Assert.NotNull(req.Frames);
            return req.Frames!;
        }
    }

    private static byte[] Led(DeviceFrame frame, int i) => frame.LedBytes.ToArray().Skip(i * 3).Take(3).ToArray();

    [Theory]
    [InlineData(DevicePinKinds.Static, "red")]
    [InlineData(DevicePinKinds.Animate, "plasma")]
    public async Task Static_and_animate_pins_roll(string kind, string effect)
    {
        using var rig = new Rig(new RampEffect());
        rig.Pins.Set("d-0", Pin(kind, effect));
        Assert.True(await rig.RollsEventually("d-0"));
        Assert.True(rig.Can("d-0", "d-1"));
    }

    [Theory]
    [InlineData(DevicePinKinds.Mirror)]
    [InlineData(DevicePinKinds.Media)]
    public async Task Mirror_and_media_pins_force_live(string kind)
    {
        using var rig = new Rig(new RampEffect());
        rig.Pins.Set("d-0", Pin(kind));
        Assert.True(await rig.RollsEventually("d-1"));
        await rig.WaitFrames(40);
        Assert.False(rig.Can("d-0"));
        Assert.False(rig.Can("d-0", "d-1"));
        Assert.True(rig.Can("d-1"));
    }

    [Fact]
    public async Task A_source_that_reads_live_input_forces_live_and_never_drops_a_window()
    {
        using var rig = new Rig(new RampEffect());
        rig.Build = _ => new LiveInputEffect();
        rig.Pins.Set("d-0", Pin(DevicePinKinds.Animate, "plasma"));
        Assert.True(await rig.RollsEventually("d-1"));
        await rig.WaitFrames(40);
        Assert.False(rig.Can("d-0"));
        Assert.False(rig.Can("d-0", "d-1"));
        // The chain without that zone still rolls.
        Assert.True(rig.Can("d-1"));
    }

    [Fact]
    public async Task A_failed_pin_beside_a_working_one_keeps_the_chain_rolling_with_base_color_on_the_failed_zone()
    {
        using var rig = new Rig(new RampEffect());
        rig.Build = key => key == "good" ? new TickRedEffect() : null;
        rig.Pins.Set("d-0", Pin(DevicePinKinds.Animate, "bad"));
        rig.Pins.Set("d-1", Pin(DevicePinKinds.Animate, "good"));
        var frames = await rig.Window(new long[] { 10, 20 }, "d-0", "d-1");
        Assert.Equal(new byte[] { 10, 0, 0 }, Led(frames[0][1], 0));
        Assert.Equal(new byte[] { 20, 0, 0 }, Led(frames[1][1], 0));
        // Base canvas ramp, not the pinned red.
        Assert.NotEqual(Led(frames[0][1], 0), Led(frames[0][0], 0));
        Assert.True(Led(frames[0][0], 0)[2] > 0 || Led(frames[0][0], 0)[0] > 0);
    }

    [Fact]
    public async Task A_pinned_zone_is_rendered_at_each_requested_tick_not_at_now()
    {
        using var rig = new Rig();
        rig.Build = _ => new TickRedEffect();
        rig.Pins.Set("d-0", Pin(DevicePinKinds.Animate, "plasma"));
        var frames = await rig.Window(new long[] { 10, 20, 30 }, "d-0", "d-1");
        Assert.Equal(new byte[] { 10, 0, 0 }, Led(frames[0][0], 0));
        Assert.Equal(new byte[] { 20, 0, 0 }, Led(frames[1][0], 0));
        Assert.Equal(new byte[] { 30, 0, 0 }, Led(frames[2][0], 0));
        Assert.All(frames, f => Assert.Equal(new byte[] { 0, 255, 0 }, Led(f[1], 0)));
    }

    [Fact]
    public async Task A_device_pinned_to_the_active_effect_keeps_the_main_canvas()
    {
        using var rig = new Rig(new RampEffect());
        rig.Build = _ => new TickRedEffect();
        rig.Pins.Set("d-0", Pin(DevicePinKinds.Animate, "ramp"));
        rig.Pins.Set("d-1", Pin(DevicePinKinds.Animate, "plasma"));
        var frames = await rig.Window(new long[] { 10 }, "d-0", "d-1");
        Assert.Equal(new byte[] { 10, 0, 0 }, Led(frames[0][1], 0));
        // d-0 reads the ramp, not the tick-red pin source.
        Assert.NotEqual(10, Led(frames[0][0], 0)[0]);
        Assert.True(Led(frames[0][0], 0)[2] > 0 || Led(frames[0][0], 0)[0] > 10);
    }

    [Fact]
    public async Task A_sweep_pin_samples_the_whole_frame_and_a_plain_pin_only_its_own_rect()
    {
        using var rig = new Rig(new TickRedEffect());
        rig.Build = _ => new RampEffect();
        rig.Pins.Set("d-0", Pin(DevicePinKinds.Animate, "sweeprainbow"));
        rig.Pins.Set("d-1", Pin(DevicePinKinds.Animate, "plasma"));
        var frames = await rig.Window(new long[] { 5 }, "d-0", "d-1");
        var sweepSpread = Led(frames[0][0], 0)[0] - Led(frames[0][0], 3)[0];
        var plainSpread = Led(frames[0][1], 0)[0] - Led(frames[0][1], 3)[0];
        Assert.True(sweepSpread > 100, $"sweep spread {sweepSpread}");
        Assert.True(plainSpread < sweepSpread / 2, $"plain spread {plainSpread}");
    }

    [Fact]
    public async Task A_static_color_pin_overrides_the_global_effect_in_the_window()
    {
        using var rig = new Rig();
        rig.Pins.Set("d-0", new DevicePin { Kind = DevicePinKinds.Static, Color = "#102030" });
        var frames = await rig.Window(new long[] { 1, 2, 3 }, "d-0", "d-1");
        foreach (var f in frames)
        {
            for (var i = 0; i < 4; i++)
            {
                Assert.Equal(new byte[] { 0x10, 0x20, 0x30 }, Led(f[0], i));
            }
            Assert.Equal(new byte[] { 0, 255, 0 }, Led(f[1], 0));
        }
    }

    [Fact]
    public async Task A_static_effect_pin_shows_its_own_look_in_the_window()
    {
        using var rig = new Rig(new RampEffect());
        rig.Pins.Set("d-0", Pin(DevicePinKinds.Static, "red"));
        var frames = await rig.Window(new long[] { 1, 2 }, "d-0", "d-1");
        Assert.All(frames, f => Assert.Equal(new byte[] { 0, 0, 255 }, Led(f[0], 0)));
    }

    [Fact]
    public async Task A_pinned_look_change_restarts_only_that_zones_settle_window()
    {
        using var rig = new Rig(new RampEffect());
        rig.Build = _ => new TickRedEffect();
        rig.Pins.Set("d-0", Pin(DevicePinKinds.Animate, "plasma"));
        Assert.True(await rig.RollsEventually("d-0", "d-1"));
        rig.Tag = "t2";
        Assert.True(await rig.BecomesFalse("d-0"));
        Assert.True(rig.Can("d-1"));
        Assert.True(await rig.RollsEventually("d-0"));
    }

    [Fact]
    public async Task A_pin_edit_on_another_zone_does_not_disturb_a_chain_without_it()
    {
        using var rig = new Rig(new RampEffect());
        Assert.True(await rig.RollsEventually("d-0"));
        rig.Pins.Set("d-1", Pin(DevicePinKinds.Animate, "plasma"));
        for (var i = 0; i < 20; i++)
        {
            Assert.True(rig.Can("d-0"));
            await rig.WaitFrames(2);
        }
    }

    [Fact]
    public async Task Flat_rolls_only_beside_an_animate_pin()
    {
        using var rig = new Rig(anchor: false);
        Assert.False(await rig.RollsEventuallyBriefly("d-0", "d-1"));
        rig.Pins.Set("d-0", Pin(DevicePinKinds.Animate, "plasma"));
        rig.Build = _ => new TickRedEffect();
        Assert.True(await rig.RollsEventually("d-0", "d-1"));
        Assert.False(rig.Can("d-1"));
    }

    [Fact]
    public async Task Swapping_the_flat_global_restarts_the_settle_window_and_shader_to_flat_never_rolls_alone()
    {
        using var rig = new Rig();
        rig.Build = _ => new TickRedEffect();
        rig.Pins.Set("d-0", Pin(DevicePinKinds.Animate, "plasma"));
        Assert.True(await rig.RollsEventually("d-0"));
        rig.Engine.SetEffect(new FlatColorEffect(1, 2, 3));
        Assert.True(await rig.BecomesFalse("d-0"));
        Assert.True(await rig.RollsEventually("d-0"));
        rig.Engine.SetEffect(new RampEffect());
        Assert.True(await rig.BecomesFalse("d-0"));
        Assert.True(await rig.RollsEventually("d-0"));
    }
}
