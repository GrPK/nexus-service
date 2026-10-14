using Nexus.Service.Devices.Detection;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests.Lighting.Rgb;

/// <summary>
/// A blackout pushed mid-rescan reaches a device list the daemon is rebuilding,
/// so it waits for the rescan to commit, and its report says how many physical
/// devices were written so a zero-write run can never read as success.
/// </summary>
public class RgbBridgeBlackoutSettleTests : IDisposable
{
    private sealed class ScriptedController : IRgbController
    {
        private int _pushes;

        public bool Connected { get; set; } = true;
        public bool IsConnected => Connected;
        public int Pushes => Volatile.Read(ref _pushes);
        public event Action? DeviceListChanged { add { } remove { } }
        public event Action<bool>? DetectionStateChanged;
        public event Action? DetectionProgress { add { } remove { } }
        public event Action<int, uint, uint>? WriteRejected { add { } remove { } }

        public void RaiseDetecting(bool detecting) => DetectionStateChanged?.Invoke(detecting);

        public IReadOnlyList<RgbDevice> Devices { get; set; } = Array.Empty<RgbDevice>();
        public async Task<IReadOnlyList<int>> GetControllerAddressesAsync(CancellationToken ct = default) =>
            (await GetDevicesAsync(ct)).Select(d => d.Address).ToList();
        public Task<bool> RescanAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> SetSettingsAsync(string key, string valueJson, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> TryConnectAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task<IReadOnlyList<RgbDevice>> GetDevicesAsync(CancellationToken ct = default) => Task.FromResult(Devices);
        public Task SetDirectModeAsync(RgbDevice device, CancellationToken ct = default) => Task.CompletedTask;

        public Task PushFrameAsync(int address, ReadOnlyMemory<RgbColor> colors, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _pushes);
            return Task.CompletedTask;
        }

        public Task SetOffAsync(int address, int ledCount, CancellationToken ct = default) => Task.CompletedTask;
        public Task PushZoneFrameAsync(int address, int zoneIndex, ReadOnlyMemory<RgbColor> colors, CancellationToken ct = default) => Task.CompletedTask;
        public Task ResizeZoneAsync(int address, int zoneIndex, int newSize, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private readonly string _tempDir;
    private readonly ScriptedController _controller = new();
    private readonly LightingEngine _engine = new();
    private readonly RgbBridge _bridge;

    public RgbBridgeBlackoutSettleTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-test-blackoutsettle-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _controller.Devices = new[]
        {
            new RgbDevice { Index = 0, Address = 0, Name = "DRAM", LedCount = 4 },
        };
        _bridge = new RgbBridge(
            new OpenRgbProcessManager(Path.Combine(_tempDir, "missing-openrgb")),
            _controller,
            _engine,
            new TestableConfigStore(Path.Combine(_tempDir, "settings.json")),
            new StubUsbEnumerator());
    }

    public void Dispose()
    {
        _bridge.Dispose();
        _bridge.AwaitShutdown();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static DateTime Until(double seconds) => DateTime.UtcNow + TimeSpan.FromSeconds(seconds);

    private async Task ActivateAndWaitForBuffersAsync()
    {
        _bridge.Activate();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline && _bridge.PhysicalBufferCount == 0)
        {
            await Task.Delay(20);
        }
        Assert.True(_bridge.PhysicalBufferCount > 0, "per-physical frame buffers were never allocated");

        // The daemon's initial detection pass ends with a COMPLETE; the fake has to say so.
        _controller.RaiseDetecting(false);
        await Task.Delay(600);
    }

    [Fact]
    public async Task Blackout_on_a_settled_bridge_reports_every_known_device_pushed()
    {
        await ActivateAndWaitForBuffersAsync();

        var report = await _bridge.BlackoutSettledAsync(Until(5));

        Assert.Equal(BlackoutSkip.None, report.Skipped);
        Assert.True(report.Known > 0);
        Assert.Equal(report.Known, report.Pushed);
        Assert.Equal($"pushed {report.Pushed}/{report.Known}", report.ToString());
    }

    [Fact]
    public async Task Blackout_waits_for_a_detection_in_progress_then_pushes()
    {
        await ActivateAndWaitForBuffersAsync();
        var before = _controller.Pushes;

        _controller.RaiseDetecting(true);
        var blackout = _bridge.BlackoutSettledAsync(Until(20));

        await Task.Delay(300);
        Assert.False(blackout.IsCompleted);
        Assert.Equal(before, _controller.Pushes);

        _controller.RaiseDetecting(false);
        var report = await blackout;

        Assert.Equal(BlackoutSkip.None, report.Skipped);
        Assert.True(report.Pushed > 0);
        Assert.True(_controller.Pushes > before);
    }

    [Fact]
    public async Task Blackout_whose_wait_runs_out_still_pushes_to_the_known_devices()
    {
        await ActivateAndWaitForBuffersAsync();
        var before = _controller.Pushes;

        _controller.RaiseDetecting(true);
        var report = await _bridge.BlackoutSettledAsync(Until(0.2));

        Assert.Equal(BlackoutSkip.Unsettled, report.Skipped);
        Assert.True(report.Pushed > 0);
        Assert.True(_controller.Pushes > before);
        Assert.Contains("still settling", report.ToString());
    }

    [Fact]
    public async Task Blackout_with_no_settle_deadline_does_not_wait_on_a_detection()
    {
        await ActivateAndWaitForBuffersAsync();
        _controller.RaiseDetecting(true);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var report = await _bridge.BlackoutSettledAsync(null);

        Assert.True(sw.ElapsedMilliseconds < 500);
        Assert.True(report.Pushed > 0);
    }

    [Fact]
    public async Task Blackout_on_an_inactive_bridge_reports_inactive_not_ok()
    {
        var report = await _bridge.BlackoutSettledAsync(Until(5));

        Assert.Equal(new BlackoutReport(0, 0, BlackoutSkip.Inactive), report);
        Assert.Equal("skipped:inactive", report.ToString());
    }

    [Fact]
    public async Task Blackout_on_a_disconnected_bridge_never_reads_as_pushed()
    {
        await ActivateAndWaitForBuffersAsync();
        _controller.Connected = false;
        var before = _controller.Pushes;

        // The bridge may treat the drop as a detection in progress and wait it
        // out; either way nothing is written and the report says so.
        var report = await _bridge.BlackoutSettledAsync(Until(0.5));

        Assert.NotEqual(BlackoutSkip.None, report.Skipped);
        Assert.Equal(0, report.Pushed);
        Assert.Equal(before, _controller.Pushes);
        Assert.StartsWith("skipped:", report.ToString());
    }

    [Fact]
    public async Task Shutdown_blackout_after_a_conflict_exit_rescan_started_waits_for_it_and_pushes()
    {
        await ActivateAndWaitForBuffersAsync();
        var store = new TestableConfigStore(Path.Combine(_tempDir, "coordinator-settings.json"));
        var coordinator = new SleepBlackoutCoordinator(_engine, store, _bridge);
        var before = _controller.Pushes;

        // Windows closes the competing apps, the recovery re-detects (the rescan
        // is in flight, the daemon reports detecting), then preshutdown runs the
        // lights-off. IsOsShutdown is not set yet when the rescan starts.
        _bridge.RecoverAfterConflictExit();
        _controller.RaiseDetecting(true);
        var shutdown = Task.Run(coordinator.OnHostShutdown);

        await Task.Delay(500);
        Assert.False(shutdown.IsCompleted);
        Assert.Equal(before, _controller.Pushes);

        // The deferred re-detect the first pass releases is a second pass.
        _controller.RaiseDetecting(false);
        _controller.RaiseDetecting(true);
        _controller.RaiseDetecting(false);
        await shutdown.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(_engine.Blackout);
        Assert.True(_controller.Pushes > before);
    }
}
