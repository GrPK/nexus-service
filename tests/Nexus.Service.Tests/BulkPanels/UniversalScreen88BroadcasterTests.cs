using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Sockets;
using Xunit;

namespace Nexus.Service.Tests.BulkPanels;

/// <summary>
/// The "lianli-screen88" push: one frame per real change of what the 8.8's settings route reports (glass
/// attached, Windows display identity present, clip playing), none for wakes that change nothing.
/// </summary>
public class UniversalScreen88BroadcasterTests
{
    private const string Topic = UniversalScreen88Broadcaster.Topic;

    private sealed class Rig : IDisposable
    {
        public readonly MultiplexHub Sockets = new();
        public readonly ConcurrentQueue<string> Sent = new();
        public volatile bool Connected;
        public volatile bool InWindowsDisplay;
        public volatile string? Playing;
        public int Reads;
        public readonly UniversalScreen88Broadcaster Broadcaster;
        private IDisposable? _subscription;

        public Rig(int settleMs = 0, int backstopMs = 600_000)
        {
            Sockets.OnBroadcastForTest += (topic, envelope) =>
            {
                if (topic == Topic)
                {
                    Sent.Enqueue(Encoding.UTF8.GetString(envelope.Span));
                }
            };
            Broadcaster = new UniversalScreen88Broadcaster(Sockets, () =>
            {
                Interlocked.Increment(ref Reads);
                return new UniversalScreen88PushState(Connected, InWindowsDisplay, Playing);
            }, settleMs, backstopMs);
        }

        public void Subscribe() => _subscription = Sockets.AddTestSubscription(Topic);

        public void Unsubscribe()
        {
            _subscription?.Dispose();
            _subscription = null;
        }

        public void Dispose() => Unsubscribe();
    }

    [Fact]
    public void The_first_read_is_a_baseline_and_unchanged_reads_send_nothing()
    {
        using var rig = new Rig();
        rig.Subscribe();

        Assert.False(rig.Broadcaster.Check());
        for (int i = 0; i < 10; i++)
        {
            Assert.False(rig.Broadcaster.Check());
        }

        Assert.Empty(rig.Sent);
    }

    [Fact]
    public void Each_reported_change_sends_one_frame_with_the_new_state()
    {
        using var rig = new Rig();
        rig.Subscribe();
        rig.Broadcaster.Check();

        // Panel to Windows display: the glass detaches, then 1A86:AD21 arrives.
        rig.Connected = true;
        Assert.True(rig.Broadcaster.Check());
        rig.Connected = false;
        Assert.True(rig.Broadcaster.Check());
        rig.InWindowsDisplay = true;
        Assert.True(rig.Broadcaster.Check());
        Assert.False(rig.Broadcaster.Check());
        rig.InWindowsDisplay = false;
        Assert.True(rig.Broadcaster.Check());
        // A clip starts, another replaces it, then the panel is back.
        rig.Playing = "clip-a";
        Assert.True(rig.Broadcaster.Check());
        rig.Playing = "clip-b";
        Assert.True(rig.Broadcaster.Check());
        rig.Playing = null;
        Assert.True(rig.Broadcaster.Check());

        Assert.Equal(7, rig.Sent.Count);
    }

    [Fact]
    public void The_frame_carries_the_topic_a_revision_and_the_state()
    {
        using var rig = new Rig();
        rig.Subscribe();
        rig.Broadcaster.Check();
        rig.InWindowsDisplay = true;
        rig.Playing = "clip-a";

        Assert.True(rig.Broadcaster.Check());

        Assert.True(rig.Sent.TryDequeue(out var json));
        using var doc = JsonDocument.Parse(json!);
        Assert.Equal("lianli-screen88", doc.RootElement.GetProperty("t").GetString());
        var d = doc.RootElement.GetProperty("d");
        Assert.True(d.GetProperty("revision").GetInt64() > 0);
        Assert.False(d.GetProperty("connected").GetBoolean());
        Assert.True(d.GetProperty("inWindowsDisplay").GetBoolean());
        Assert.Equal("clip-a", d.GetProperty("playing").GetString());
    }

    [Fact]
    public void Without_subscribers_nothing_is_sent_but_the_baseline_follows()
    {
        using var rig = new Rig();
        rig.Broadcaster.Check();
        rig.Connected = true;

        Assert.False(rig.Broadcaster.Check());
        rig.Subscribe();
        // The change already seen is not sent late to a new subscriber.
        Assert.False(rig.Broadcaster.Check());

        Assert.Empty(rig.Sent);
    }

    [Fact]
    public async Task A_wake_sends_the_change_at_once_and_a_burst_of_wakes_reads_once()
    {
        using var rig = new Rig(settleMs: 100);
        rig.Subscribe();
        await rig.Broadcaster.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await Eventually(() => Volatile.Read(ref rig.Reads) >= 1));
            var reads = Volatile.Read(ref rig.Reads);

            rig.InWindowsDisplay = true;
            // Several PnP notifications for one replug.
            for (int i = 0; i < 5; i++)
            {
                rig.Broadcaster.Wake();
            }

            Assert.True(await Eventually(() => rig.Sent.Count == 1, timeoutMs: 2000));
            await Task.Delay(300);
            Assert.Single(rig.Sent);
            Assert.Equal(reads + 1, Volatile.Read(ref rig.Reads));
        }
        finally
        {
            await rig.Broadcaster.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task While_subscribed_a_backstop_check_catches_a_change_no_wake_reported()
    {
        using var rig = new Rig(backstopMs: 100);
        rig.Subscribe();
        await rig.Broadcaster.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await Eventually(() => Volatile.Read(ref rig.Reads) >= 1));
            rig.Connected = true;

            Assert.True(await Eventually(() => rig.Sent.Count == 1, timeoutMs: 2000));
        }
        finally
        {
            await rig.Broadcaster.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Without_subscribers_there_is_no_backstop_polling()
    {
        using var rig = new Rig(backstopMs: 50);
        await rig.Broadcaster.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await Eventually(() => Volatile.Read(ref rig.Reads) >= 1));
            var reads = Volatile.Read(ref rig.Reads);
            await Task.Delay(400);
            Assert.Equal(reads, Volatile.Read(ref rig.Reads));

            // The first subscriber wakes it, and the backstop runs from then on.
            rig.Subscribe();
            Assert.True(await Eventually(() => Volatile.Read(ref rig.Reads) > reads + 2, timeoutMs: 2000));
        }
        finally
        {
            await rig.Broadcaster.StopAsync(CancellationToken.None);
        }
    }

    // ── the triggers: the hub's attach and detach, and the USB cache's invalidation ──

    [Fact]
    public void The_hub_reports_each_attach_and_detach_once()
    {
        var driver = new FlakyDriver();
        using var hub = new BulkPanelHub(driver);
        var changes = new List<bool>();
        hub.ConnectionChanged += () => changes.Add(hub.IsConnected);

        hub.Detach();
        driver.Answer = null;
        Assert.False(hub.Attach(new NullPipe(), null));
        Assert.Empty(changes);

        driver.Answer = (1920, 480);
        Assert.True(hub.Attach(new NullPipe(), null));
        Assert.True(hub.Renegotiate());
        driver.Answer = null;
        Assert.False(hub.Renegotiate());
        driver.Answer = (1920, 480);
        Assert.True(hub.Attach(new NullPipe(), null));
        hub.Detach();
        hub.Detach();

        Assert.Equal(new[] { true, false, true, false }, changes);
    }

    [Fact]
    public void Disposing_an_attached_hub_reports_the_detach()
    {
        var driver = new FlakyDriver { Answer = (480, 1920) };
        var hub = new BulkPanelHub(driver);
        Assert.True(hub.Attach(new NullPipe(), null));
        var changes = 0;
        hub.ConnectionChanged += () => changes++;

        hub.Dispose();
        hub.Dispose();

        Assert.Equal(1, changes);
    }

    [Fact]
    public void A_throwing_listener_does_not_fail_the_attach()
    {
        using var hub = new BulkPanelHub(new FlakyDriver { Answer = (1920, 480) });
        hub.ConnectionChanged += () => throw new InvalidOperationException("listener");

        Assert.True(hub.Attach(new NullPipe(), null));
        Assert.True(hub.IsConnected);
    }

    [Fact]
    public void The_usb_cache_reports_each_invalidation()
    {
        var cache = new CachingUsbEnumerator(new EmptyUsb());
        var invalidated = 0;
        cache.Invalidated += () => invalidated++;

        cache.Invalidate();
        cache.Invalidate();

        Assert.Equal(2, invalidated);
    }

    [Fact]
    public void A_throwing_cache_listener_does_not_break_the_invalidation()
    {
        var usb = new EmptyUsb();
        var cache = new CachingUsbEnumerator(usb);
        cache.Enumerate();
        cache.Invalidated += () => throw new InvalidOperationException("listener");

        cache.Invalidate();
        cache.Enumerate();

        Assert.Equal(2, usb.Scans);
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
            await Task.Delay(20);
        }
        return condition();
    }

    private sealed class FlakyDriver : IBulkPanelDriver
    {
        public (int Width, int Height)? Answer;

        public string HandlerId => "fake-flaky";
        public string Name => "Fake panel";
        public int VendorId => 0x1234;
        public IReadOnlyList<int> ProductIds { get; } = new[] { 0x5678 };
        public string Surface => "monitor";
        public int Fps => 30;
        public byte WritePipeId => 0x01;
        public byte ReadPipeId => 0x81;
        public bool NeedsHidChannel => false;

        public (int Width, int Height)? Connect(IBulkUsbPipe pipe, IHidDevice? hid) => Answer;
        public bool SendFrame(IBulkUsbPipe pipe, IHidDevice? hid, ReadOnlySpan<byte> bgra) => true;
        public void Disconnect(IBulkUsbPipe pipe, IHidDevice? hid) { }
    }

    private sealed class NullPipe : IBulkUsbPipe
    {
        public bool IsGone => false;
        public bool Write(ReadOnlySpan<byte> data) => true;
        public bool Write(byte pipeId, ReadOnlySpan<byte> data) => true;
        public int Read(Span<byte> buffer, int timeoutMs) => 0;
        public void Dispose() { }
    }

    private sealed class EmptyUsb : IUsbEnumerator
    {
        public int Scans;

        public List<UsbDeviceEntry> Enumerate()
        {
            Scans++;
            return new List<UsbDeviceEntry>();
        }
    }
}
