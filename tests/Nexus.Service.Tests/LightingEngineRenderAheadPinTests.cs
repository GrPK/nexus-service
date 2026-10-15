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
    };

    private static DevicePin Pin(string kind, string? effect = null) => new() { Kind = kind, Effect = effect };

    private static LightingEngine Engine(DevicePinTracker pins, bool pinsRender = true)
    {
        var engine = new LightingEngine
        {
            Pins = pins,
            FrameIntervalMs = 10,
            PinLookTag = _ => "t",
            PinEffectFactory = (_, _) => pinsRender ? new FlatColorEffect(255, 0, 0) : null,
            StaticPinEffectFactory = _ => new FlatColorEffect(0, 0, 255),
        };
        engine.UpdateDevices(Devices());
        engine.SetEffect(new FlatColorEffect(0, 255, 0));
        return engine;
    }

    private static async Task<bool> SettledAhead(LightingEngine engine, params string[] ids)
    {
        for (var i = 0; i < 100; i++)
        {
            await Task.Delay(50);
            if (engine.CanRenderAhead(ids)) return true;
        }
        return false;
    }

    [Theory]
    [InlineData(DevicePinKinds.Static, "red", true)]
    [InlineData(DevicePinKinds.Animate, "plasma", true)]
    [InlineData(DevicePinKinds.Animate, "spectrumbars", false)]
    [InlineData(DevicePinKinds.Mirror, null, false)]
    [InlineData(DevicePinKinds.Media, null, false)]
    public async Task Pin_kinds_roll_only_when_time_pure(string kind, string? effect, bool expected)
    {
        var pins = new DevicePinTracker();
        pins.Set("d-0", Pin(kind, effect));
        using var engine = Engine(pins);
        var rolls = await SettledAhead(engine, "d-0");
        Assert.Equal(expected, rolls);
        // A chain without the pinned device is unaffected.
        Assert.True(await SettledAhead(engine, "d-1"));
    }

    [Fact]
    public async Task A_mixed_chain_rolls_when_every_pin_is_time_pure_and_not_when_one_is_not()
    {
        var pins = new DevicePinTracker();
        pins.Set("d-0", Pin(DevicePinKinds.Animate, "plasma"));
        using var engine = Engine(pins);
        Assert.True(await SettledAhead(engine, "d-0", "d-1"));
        pins.Set("d-1", Pin(DevicePinKinds.Mirror));
        await Task.Delay(500);
        Assert.False(engine.CanRenderAhead(new[] { "d-0", "d-1" }));
    }

    [Fact]
    public async Task Ahead_window_shows_each_zones_own_source()
    {
        var pins = new DevicePinTracker();
        pins.Set("d-0", Pin(DevicePinKinds.Animate, "plasma"));
        using var engine = Engine(pins);
        Assert.True(await SettledAhead(engine, "d-0", "d-1"));

        var req = engine.RequestAhead(new[] { "d-0", "d-1" }, new long[] { 1, 2, 3 });
        Assert.True(req.Done.Wait(TimeSpan.FromSeconds(5)));
        Assert.NotNull(req.Frames);
        foreach (var frame in req.Frames!)
        {
            Assert.Equal(new byte[] { 255, 0, 0 }, frame[0].LedBytes.ToArray().Take(3).ToArray());
            Assert.Equal(new byte[] { 0, 255, 0 }, frame[1].LedBytes.ToArray().Take(3).ToArray());
        }
    }

    [Fact]
    public async Task Ahead_window_is_dropped_when_a_pinned_source_cannot_render()
    {
        var pins = new DevicePinTracker();
        pins.Set("d-0", Pin(DevicePinKinds.Animate, "plasma"));
        using var engine = Engine(pins, pinsRender: false);
        await Task.Delay(500);
        var req = engine.RequestAhead(new[] { "d-0" }, new long[] { 1 });
        Assert.True(req.Done.Wait(TimeSpan.FromSeconds(5)));
        Assert.Null(req.Frames);
    }

    private static async Task<DeviceFrame[][]> Window(LightingEngine engine, params string[] ids)
    {
        Assert.True(await SettledAhead(engine, ids));
        var req = engine.RequestAhead(ids, new long[] { 1, 2, 3 });
        Assert.True(req.Done.Wait(TimeSpan.FromSeconds(5)));
        Assert.NotNull(req.Frames);
        return req.Frames!;
    }

    [Fact]
    public async Task Ahead_window_paints_a_static_color_pin_over_the_global_effect()
    {
        var pins = new DevicePinTracker();
        pins.Set("d-0", new DevicePin { Kind = DevicePinKinds.Static, Color = "#102030" });
        using var engine = Engine(pins);
        foreach (var frame in await Window(engine, "d-0", "d-1"))
        {
            Assert.All(Enumerable.Range(0, 4), i => Assert.Equal(new byte[] { 0x10, 0x20, 0x30 }, frame[0].LedBytes.ToArray().Skip(i * 3).Take(3).ToArray()));
            Assert.Equal(new byte[] { 0, 255, 0 }, frame[1].LedBytes.ToArray().Take(3).ToArray());
        }
    }

    [Fact]
    public async Task Ahead_window_paints_a_static_effect_pin_with_its_own_look()
    {
        var pins = new DevicePinTracker();
        pins.Set("d-0", Pin(DevicePinKinds.Static, "red"));
        using var engine = Engine(pins);
        foreach (var frame in await Window(engine, "d-0", "d-1"))
        {
            Assert.Equal(new byte[] { 0, 0, 255 }, frame[0].LedBytes.ToArray().Take(3).ToArray());
            Assert.Equal(new byte[] { 0, 255, 0 }, frame[1].LedBytes.ToArray().Take(3).ToArray());
        }
    }
}
