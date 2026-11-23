using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Peripherals.Hid;
using Xunit;

namespace Nexus.Service.Tests.BulkPanels;

public class BulkPanelConnectionWorkerTests
{
    [Fact]
    public async Task An_unplugged_panel_is_let_go_and_taken_back_when_it_returns()
    {
        var usb = new SwitchableUsb { Present = true };
        var driver = new FakeDriver();
        var hub = new BulkPanelHub(driver);
        var store = new InMemoryConfigStore();
        store.Update(s => s.Devices.NexusControlEnabled.Add(driver.HandlerId));
        var worker = new BulkPanelConnectionWorker(
            new NoHid(), new PipeFactory(), hub, new DeviceControlGate(store), new HardwarePresence(usb),
            connectPollMs: 100, presencePollMs: 50);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await Eventually(() => hub.IsConnected));

            usb.Present = false;
            Assert.True(await Eventually(() => !hub.IsConnected));
            Assert.Equal(1, driver.Disconnects);

            usb.Present = true;
            Assert.True(await Eventually(() => hub.IsConnected));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_pipe_whose_device_is_gone_is_reopened_while_presence_never_misses()
    {
        var usb = new SwitchableUsb { Present = true };
        var driver = new FakeDriver();
        var hub = new BulkPanelHub(driver);
        var store = new InMemoryConfigStore();
        store.Update(s => s.Devices.NexusControlEnabled.Add(driver.HandlerId));
        var pipes = new PipeFactory();
        var worker = new BulkPanelConnectionWorker(
            new NoHid(), pipes, hub, new DeviceControlGate(store), new HardwarePresence(usb),
            connectPollMs: 100, presencePollMs: 50);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await Eventually(() => hub.IsConnected));
            Assert.Single(pipes.Opened);

            Assert.True(pipes.Opened.TryPeek(out var first));
            first!.Gone = true;
            Assert.True(await Eventually(() => pipes.Opened.Count == 2 && hub.IsConnected));
            Assert.Equal(1, driver.Disconnects);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_denied_open_sets_the_hub_flag_and_a_later_successful_open_clears_it()
    {
        var usb = new SwitchableUsb { Present = true };
        var driver = new FakeDriver();
        var hub = new BulkPanelHub(driver);
        var store = new InMemoryConfigStore();
        store.Update(s => s.Devices.NexusControlEnabled.Add(driver.HandlerId));
        var pipes = new DenyingPipeFactory { Deny = true };
        var worker = new BulkPanelConnectionWorker(
            new NoHid(), pipes, hub, new DeviceControlGate(store), new HardwarePresence(usb),
            connectPollMs: 50, presencePollMs: 50);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await Eventually(() => hub.OpenAccessDenied));
            Assert.True(hub.OpenFailed);
            Assert.False(hub.IsConnected);

            pipes.Deny = false;
            Assert.True(await Eventually(() => hub.IsConnected));
            Assert.False(hub.OpenAccessDenied);
            Assert.False(hub.OpenFailed);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_denied_panel_that_is_unplugged_clears_the_flag()
    {
        var usb = new SwitchableUsb { Present = true };
        var driver = new FakeDriver();
        var hub = new BulkPanelHub(driver);
        var store = new InMemoryConfigStore();
        store.Update(s => s.Devices.NexusControlEnabled.Add(driver.HandlerId));
        var worker = new BulkPanelConnectionWorker(
            new NoHid(), new DenyingPipeFactory { Deny = true }, hub, new DeviceControlGate(store), new HardwarePresence(usb),
            connectPollMs: 50, presencePollMs: 50);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await Eventually(() => hub.OpenAccessDenied));

            usb.Present = false;
            Assert.True(await Eventually(() => !hub.OpenAccessDenied));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_denied_open_records_the_holder_and_unplugging_or_a_later_open_clears_it()
    {
        var usb = new SwitchableUsb { Present = true };
        var driver = new FakeDriver();
        var hub = new BulkPanelHub(driver);
        var store = new InMemoryConfigStore();
        store.Update(s => s.Devices.NexusControlEnabled.Add(driver.HandlerId));
        var pipes = new DenyingPipeFactory { Deny = true };
        var worker = new BulkPanelConnectionWorker(
            new NoHid(), pipes, hub, new DeviceControlGate(store), new HardwarePresence(usb),
            connectPollMs: 50, presencePollMs: 50, holderLookup: _ => "lian-li-l-connect");

        await worker.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await Eventually(() => hub.OpenAccessHolder == "lian-li-l-connect"));

            usb.Present = false;
            Assert.True(await Eventually(() => hub.OpenAccessHolder is null));

            usb.Present = true;
            Assert.True(await Eventually(() => hub.OpenAccessHolder == "lian-li-l-connect"));

            pipes.Deny = false;
            Assert.True(await Eventually(() => hub.IsConnected));
            Assert.Null(hub.OpenAccessHolder);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    // ── a woken worker: a turn reaches the glass at once, presence keeps its poll ──

    /// <summary>Long enough that only a wake can explain anything the worker does within a test.</summary>
    private const int NeverPollMs = 600_000;

    [Fact]
    public async Task A_woken_worker_renegotiates_a_turned_session_without_waiting_for_its_poll()
    {
        var usb = new SwitchableUsb { Present = true };
        var driver = new TurningDriver();
        var hub = new BulkPanelHub(driver);
        var store = new InMemoryConfigStore();
        store.Update(s => s.Devices.NexusControlEnabled.Add(driver.HandlerId));
        var resized = 0;
        hub.GeometryChanged += () => Interlocked.Increment(ref resized);
        var worker = new BulkPanelConnectionWorker(
            new NoHid(), new PipeFactory(), hub, new DeviceControlGate(store), new HardwarePresence(usb),
            connectPollMs: 50, presencePollMs: NeverPollMs);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await Eventually(() => hub.IsConnected && driver.Connects == 1));
            Assert.Equal((1920, 480), (hub.Width, hub.Height));

            driver.Portrait = true;
            hub.WakeWorker();

            Assert.True(await Eventually(() => hub.Width == 480 && hub.Height == 1920, timeoutMs: 3000));
            Assert.Equal(2, driver.Connects);
            Assert.True(await Eventually(() => Volatile.Read(ref resized) == 1));
            Assert.Equal(0, driver.Disconnects);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_wake_with_nothing_stale_leaves_the_session_alone()
    {
        var usb = new SwitchableUsb { Present = true };
        var driver = new TurningDriver();
        var hub = new BulkPanelHub(driver);
        var store = new InMemoryConfigStore();
        store.Update(s => s.Devices.NexusControlEnabled.Add(driver.HandlerId));
        var worker = new BulkPanelConnectionWorker(
            new NoHid(), new PipeFactory(), hub, new DeviceControlGate(store), new HardwarePresence(usb),
            connectPollMs: 50, presencePollMs: NeverPollMs);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await Eventually(() => hub.IsConnected && driver.Connects == 1));

            for (int i = 0; i < 5; i++)
            {
                hub.WakeWorker();
                await Task.Delay(20);
            }

            Assert.Equal(1, driver.Connects);
            Assert.True(hub.IsConnected);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Wakes_are_not_presence_scans_so_they_never_count_as_misses()
    {
        var usb = new SwitchableUsb { Present = true };
        var driver = new TurningDriver();
        var hub = new BulkPanelHub(driver);
        var store = new InMemoryConfigStore();
        store.Update(s => s.Devices.NexusControlEnabled.Add(driver.HandlerId));
        var worker = new BulkPanelConnectionWorker(
            new NoHid(), new PipeFactory(), hub, new DeviceControlGate(store), new HardwarePresence(usb),
            connectPollMs: 50, presencePollMs: NeverPollMs);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await Eventually(() => hub.IsConnected && driver.Connects == 1));
            // One incomplete bus scan must not end a session, and a wake is no scan at all.
            usb.Present = false;
            var scans = usb.Scans;
            for (int i = 0; i < 5; i++)
            {
                hub.WakeWorker();
                await Task.Delay(20);
            }

            Assert.Equal(scans, usb.Scans);
            Assert.True(hub.IsConnected);
            Assert.Equal(0, driver.Disconnects);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<bool> Eventually(Func<bool> condition, int timeoutMs = 4000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return true;
            }
            await Task.Delay(50);
        }
        return condition();
    }

    private sealed class SwitchableUsb : IUsbEnumerator
    {
        public volatile bool Present;
        private int _scans;

        public int Scans => Volatile.Read(ref _scans);

        public List<UsbDeviceEntry> Enumerate()
        {
            Interlocked.Increment(ref _scans);
            return Present ? new List<UsbDeviceEntry> { new() { VendorId = FakeDriver.Vid, ProductId = FakeDriver.Pid } } : new();
        }
    }

    /// <summary>A glass that turns in its handshake, like the 8.8: a mounting change leaves its session stale.</summary>
    private sealed class TurningDriver : IBulkPanelDriver
    {
        public int Disconnects;
        private int _connects;
        private volatile bool _sessionPortrait;

        public volatile bool Portrait;

        public int Connects => Volatile.Read(ref _connects);

        public string HandlerId => "fake-turning";
        public string Name => "Fake turning panel";
        public int VendorId => FakeDriver.Vid;
        public IReadOnlyList<int> ProductIds { get; } = new[] { FakeDriver.Pid };
        public string Surface => "monitor";
        public int Fps => 30;
        public byte WritePipeId => 0x01;
        public byte ReadPipeId => 0x81;
        public bool NeedsHidChannel => false;
        public bool GeometryStale => Portrait != _sessionPortrait;

        public (int Width, int Height)? Connect(IBulkUsbPipe pipe, IHidDevice? hid)
        {
            Interlocked.Increment(ref _connects);
            _sessionPortrait = Portrait;
            return _sessionPortrait ? (480, 1920) : (1920, 480);
        }

        public bool SendFrame(IBulkUsbPipe pipe, IHidDevice? hid, ReadOnlySpan<byte> bgra) => true;
        public void Disconnect(IBulkUsbPipe pipe, IHidDevice? hid) => Interlocked.Increment(ref Disconnects);
    }

    private sealed class FakeDriver : IBulkPanelDriver
    {
        public const int Vid = 0x1234;
        public const int Pid = 0x5678;

        public int Disconnects;

        public string HandlerId => "fake-bulk";
        public string Name => "Fake bulk panel";
        public int VendorId => Vid;
        public IReadOnlyList<int> ProductIds { get; } = new[] { Pid };
        public string Surface => "lcd-square";
        public int Fps => 30;
        public byte WritePipeId => 0x01;
        public byte ReadPipeId => 0x81;
        public bool NeedsHidChannel => false;

        public (int Width, int Height)? Connect(IBulkUsbPipe pipe, IHidDevice? hid) => (320, 320);
        public bool SendFrame(IBulkUsbPipe pipe, IHidDevice? hid, ReadOnlySpan<byte> bgra) => true;
        public void Disconnect(IBulkUsbPipe pipe, IHidDevice? hid) => Interlocked.Increment(ref Disconnects);
    }

    private sealed class PipeFactory : IBulkUsbPipeFactory
    {
        public readonly ConcurrentQueue<NullPipe> Opened = new();

        public IBulkUsbPipe? Open(int vendorId, int productId, byte writePipeId, byte readPipeId)
        {
            var pipe = new NullPipe();
            Opened.Enqueue(pipe);
            return pipe;
        }
    }

    private sealed class DenyingPipeFactory : IBulkUsbPipeFactory
    {
        public volatile bool Deny;

        public IBulkUsbPipe? Open(int vendorId, int productId, byte writePipeId, byte readPipeId) =>
            Open(vendorId, productId, writePipeId, readPipeId, out _);

        public IBulkUsbPipe? Open(int vendorId, int productId, byte writePipeId, byte readPipeId, out bool accessDenied)
        {
            accessDenied = Deny;
            return Deny ? null : new NullPipe();
        }
    }

    private sealed class NullPipe : IBulkUsbPipe
    {
        public volatile bool Gone;

        public bool IsGone => Gone;
        public bool Write(ReadOnlySpan<byte> data) => true;
        public bool Write(byte pipeId, ReadOnlySpan<byte> data) => true;
        public int Read(Span<byte> buffer, int timeoutMs) => 0;
        public void Dispose() { }
    }

    private sealed class NoHid : IHidEnumerator
    {
        public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId) => Array.Empty<HidDeviceInfo>();
        public IReadOnlyList<HidDeviceInfo> FindAll() => Array.Empty<HidDeviceInfo>();
        public IHidDevice? Open(string path, bool forInput = false) => null;
    }
}
