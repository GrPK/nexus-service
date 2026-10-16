using Nexus.Service.Devices.Detection;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Rgb;

namespace Nexus.Service.Tests.Lighting;

internal sealed class NullRgbController : IRgbController
{
    public bool IsConnected => false;
    public event Action? DeviceListChanged { add { } remove { } }
    public event Action<bool>? DetectionStateChanged { add { } remove { } }
    public event Action? DetectionProgress { add { } remove { } }
    public event Action<int, uint, uint>? WriteRejected { add { } remove { } }
    public Task<IReadOnlyList<int>> GetControllerAddressesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>());
    public Task<bool> RescanAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<bool> SetSettingsAsync(string key, string valueJson, CancellationToken ct = default) => Task.FromResult(true);
    public Task<bool> TryConnectAsync(CancellationToken ct = default) => Task.FromResult(false);
    public Task DisconnectAsync() => Task.CompletedTask;
    public Task<IReadOnlyList<RgbDevice>> GetDevicesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<RgbDevice>>(Array.Empty<RgbDevice>());
    public Task SetDirectModeAsync(RgbDevice device, CancellationToken ct = default) => Task.CompletedTask;
    public Task PushFrameAsync(int address, ReadOnlyMemory<RgbColor> colors, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetOffAsync(int address, int ledCount, CancellationToken ct = default) => Task.CompletedTask;
    public Task PushZoneFrameAsync(int address, int zoneIndex, ReadOnlyMemory<RgbColor> colors, CancellationToken ct = default) => Task.CompletedTask;
    public Task ResizeZoneAsync(int address, int zoneIndex, int newSize, CancellationToken ct = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public class PushRateLimiterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rate-limiter-" + Guid.NewGuid().ToString("N")[..8]);

    public PushRateLimiterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private RgbBridge NewBridge() => new(
        new OpenRgbProcessManager(Path.Combine(_dir, "missing-openrgb")),
        new NullRgbController(),
        new Nexus.Service.Lighting.Engine.LightingEngine(),
        new TestableConfigStore(Path.Combine(_dir, "settings.json")),
        new StubUsbEnumerator());

    [Theory]
    [InlineData(30, 30)]
    [InlineData(10, 90)]
    [InlineData(1, 900)]
    public void Gap_is_just_under_the_period(int hz, int expectedMs)
    {
        Assert.Equal(expectedMs, PushRateLimiter<int>.GapMs(hz));
    }

    [Fact]
    public void Drops_inside_the_gap_and_passes_after_it()
    {
        var limiter = new PushRateLimiter<int>();
        Assert.True(limiter.TryPass(0, 10, 1000));
        Assert.False(limiter.TryPass(0, 10, 1050));
        Assert.True(limiter.TryPass(0, 10, 1090));
    }

    [Fact]
    public void Keys_are_independent_and_zero_cap_never_drops()
    {
        var limiter = new PushRateLimiter<int>();
        Assert.True(limiter.TryPass(0, 1, 1000));
        Assert.True(limiter.TryPass(1, 1, 1000));
        Assert.True(limiter.TryPass(0, 0, 1001));
        Assert.True(limiter.TryPass(0, 0, 1002));
    }

    [Fact]
    public void Split_zones_sharing_a_push_take_the_lowest_cap()
    {
        var caps = new Dictionary<int, int>();
        RgbBridge.FoldCap(caps, 2, 20);
        RgbBridge.FoldCap(caps, 2, 5);
        RgbBridge.FoldCap(caps, 2, 15);
        RgbBridge.FoldCap(caps, 3, 30);
        Assert.Equal(5, caps[2]);
        Assert.Equal(30, caps[3]);
    }

    [Fact]
    public void Dropped_frame_is_delivered_on_a_later_tick_with_the_newest_content()
    {
        using var bridge = NewBridge();
        var buf = new RgbColor[] { new(1, 0, 0) };
        Assert.True(bridge.ShouldPush(0, buf, 10, 1000));

        buf[0] = new RgbColor(2, 0, 0);
        Assert.False(bridge.ShouldPush(0, buf, 10, 1033));
        buf[0] = new RgbColor(3, 0, 0);
        Assert.False(bridge.ShouldPush(0, buf, 10, 1066));
        Assert.True(bridge.ShouldPush(0, buf, 10, 1099));
        // The send recorded the newest content as the baseline.
        Assert.False(bridge.ShouldPush(0, buf, 10, 1200));
    }

    [Fact]
    public void Identical_frames_do_not_spend_the_gap()
    {
        using var bridge = NewBridge();
        var buf = new RgbColor[] { new(1, 0, 0) };
        Assert.True(bridge.ShouldPush(0, buf, 10, 1000));
        Assert.False(bridge.ShouldPush(0, buf, 10, 1010));
        buf[0] = new RgbColor(2, 0, 0);
        Assert.False(bridge.ShouldPush(0, buf, 10, 1050));
        Assert.True(bridge.ShouldPush(0, buf, 10, 1090));
    }

    [Fact]
    public void Device_reshuffle_clears_the_gap_state()
    {
        using var bridge = NewBridge();
        var buf = new RgbColor[] { new(1, 0, 0) };
        Assert.True(bridge.ShouldPush(0, buf, 1, 1000));
        buf[0] = new RgbColor(2, 0, 0);
        Assert.False(bridge.ShouldPush(0, buf, 1, 1100));

        bridge.SyncPhysicalBuffers(Array.Empty<RgbDevice>());
        Assert.True(bridge.ShouldPush(0, buf, 1, 1101));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(30, true)]
    [InlineData(1, true)]
    [InlineData(25, false)]
    [InlineData(0, false)]
    [InlineData(60, false)]
    [InlineData(-5, false)]
    public void Only_listed_rates_are_valid(int? hz, bool valid)
    {
        Assert.Equal(valid, DeviceRateCap.IsValid(hz));
    }

    [Fact]
    public void Engine_default_interval_is_thirty_hz()
    {
        using var engine = new Nexus.Service.Lighting.Engine.LightingEngine();
        Assert.Equal(33, engine.FrameIntervalMs);
    }
}
