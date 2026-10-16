using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests;

/// <summary>
/// A shader that has to compile must not freeze, blank or poll-starve the output.
/// The GL render is replaced by ShaderEffect.TestRender, which reports when the
/// program lands; no GL context is involved.
/// </summary>
public class AsyncCompileSwitchTests
{
    private static GpuContext Context()
    {
        var gpu = new GpuContext(16, 9, null, () => { });
        lock (gpu.Lock)
        {
            gpu.EnsureInitializedLocked();
        }
        Assert.True(gpu.WaitForInit(TimeSpan.FromSeconds(5)));
        gpu.SetParallelCompileForTest(true);
        return gpu;
    }

    private static ShaderEffect Shader(GpuContext gpu, string name, byte r, byte g, byte b, int landsAfterCalls, Action? onCall = null)
    {
        var calls = 0;
        return new ShaderEffect(name, gpu, "")
        {
            TestRender = (canvas, _) =>
            {
                calls++;
                onCall?.Invoke();
                if (calls <= landsAfterCalls)
                {
                    return false;
                }
                for (var y = 0; y < canvas.Height; y++)
                {
                    for (var x = 0; x < canvas.Width; x++)
                    {
                        canvas.SetPixel(x, y, r, g, b);
                    }
                }
                return true;
            },
        };
    }

    [Fact]
    public void The_outgoing_effect_keeps_drawing_until_the_new_program_lands_then_is_disposed_once()
    {
        using var gpu = Context();
        var old = Shader(gpu, "old", 255, 0, 0, 0);
        old.MarkCompiledForTest();
        var incoming = Shader(gpu, "new", 0, 255, 0, 3);
        var canvas = new CanvasBuffer(16, 9);

        Assert.True(incoming.AdoptPredecessor(old));

        // The first frames of a fresh effect used to run the generation reset and drop the predecessor.
        for (var i = 0; i < 3; i++)
        {
            incoming.RenderFrame(canvas, i);
            Assert.True(incoming.IsRenderPending);
            Assert.True(incoming.HasPredecessor);
            Assert.Equal((255, 0, 0), canvas.GetPixel(0, 0));
            Assert.Equal(0, old.DisposeCount);
        }

        incoming.RenderFrame(canvas, 3);
        Assert.False(incoming.IsRenderPending);
        Assert.Equal((0, 255, 0), canvas.GetPixel(0, 0));

        incoming.RenderFrame(canvas, 4);
        incoming.Dispose();
        Assert.Equal(1, old.DisposeCount);
        Assert.False(incoming.HasPredecessor);
    }

    [Fact]
    public void A_switch_during_a_switch_keeps_the_last_effect_that_had_a_program()
    {
        using var gpu = Context();
        var first = Shader(gpu, "a", 255, 0, 0, 0);
        first.MarkCompiledForTest();
        var second = Shader(gpu, "b", 0, 255, 0, 100);
        var third = Shader(gpu, "c", 0, 0, 255, 100);
        var canvas = new CanvasBuffer(16, 9);

        Assert.True(second.AdoptPredecessor(first));
        Assert.True(third.AdoptPredecessor(second));
        third.RenderFrame(canvas, 0);

        Assert.Equal((255, 0, 0), canvas.GetPixel(0, 0));
        Assert.Equal(1, second.DisposeCount);
        Assert.Equal(0, first.DisposeCount);
    }

    [Fact]
    public async Task A_frozen_shader_that_is_still_compiling_is_polled_until_it_renders()
    {
        using var gpu = Context();
        var calls = 0;
        var shader = Shader(gpu, "static", 0, 255, 0, 5, () => Interlocked.Increment(ref calls));
        using var engine = new LightingEngine { FrameIntervalMs = 10 };
        var dev = new DeviceFrame(0, "dev-a", 4, x: 100, y: 100, w: 100, h: 100);
        engine.UpdateDevices(new[] { dev });

        engine.SetEffect(shader);
        engine.SetFrozen(true);

        for (var i = 0; i < 300 && Volatile.Read(ref calls) <= 5; i++)
        {
            await Task.Delay(10);
        }
        Assert.True(Volatile.Read(ref calls) > 5);
        await Task.Delay(50);
        Assert.False(shader.IsRenderPending);
        // Rendered once it landed: a frozen effect then stops rendering.
        var settled = Volatile.Read(ref calls);
        await Task.Delay(100);
        Assert.Equal(settled, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task A_pin_still_compiling_leaves_its_device_on_the_global_canvas()
    {
        using var gpu = Context();
        var pins = new DevicePinTracker();
        pins.Set("dev-a", new DevicePin { Kind = DevicePinKinds.Animate, Effect = "pinned" });
        var pinShader = Shader(gpu, "pinned", 255, 0, 0, 10, () => { });
        using var engine = new LightingEngine
        {
            Pins = pins,
            PinEffectFactory = (_, _) => pinShader,
            PinLookTag = _ => "t",
            FrameIntervalMs = 10,
        };
        var dev = new DeviceFrame(0, "dev-a", 5, x: 300, y: 100, w: 40, h: 250, rotation: 90);
        engine.UpdateDevices(new[] { dev });
        engine.SetEffect(new SolidEffect(0, 255, 0));

        // While the pin compiles the device shows the global green, never the pin's blank canvas.
        byte[] first = Array.Empty<byte>();
        for (var i = 0; i < 300; i++)
        {
            first = dev.LedBytes.ToArray();
            if (first.Length > 0 && (first[1] == 255 || first[0] == 255))
            {
                break;
            }
            await Task.Delay(5);
        }
        Assert.Equal(255, first[1]);
        Assert.Equal(0, first[0]);

        for (var i = 0; i < 300 && dev.LedBytes.ToArray()[0] != 255; i++)
        {
            await Task.Delay(10);
        }
        Assert.Equal(255, dev.LedBytes.ToArray()[0]);
    }

    private sealed class SolidEffect : IEffect
    {
        private readonly byte _r, _g, _b;
        public SolidEffect(byte r, byte g, byte b) { _r = r; _g = g; _b = b; }
        public string Name => "solid";
        public void RenderFrame(CanvasBuffer canvas, double tickMs)
        {
            for (var y = 0; y < canvas.Height; y++)
            {
                for (var x = 0; x < canvas.Width; x++)
                {
                    canvas.SetPixel(x, y, _r, _g, _b);
                }
            }
        }
        public void Dispose() { }
    }
}
