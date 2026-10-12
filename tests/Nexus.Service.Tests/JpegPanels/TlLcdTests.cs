using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.JpegPanels;
using Nexus.Service.Tests.StreamDeck;
using Xunit;

namespace Nexus.Service.Tests.JpegPanels;

/// <summary>
/// Byte layouts are pinned against L-Connect 3's own TL LCD driver (LCDPacket,
/// LCDControlSetting, SerialNumberInfo); no unit has been run against this code.
/// </summary>
public class TlLcdTests
{
    private const int ReportLength = 512;

    [Fact]
    public void A_frame_chunk_carries_501_bytes_behind_the_sequenced_header()
    {
        var jpeg = new byte[1200];
        var report = new byte[ReportLength];

        int written = JpegPanelProtocol.FillChunk(
            report, JpegPanelModel.TlLcd.HeaderStyle, JpegPanelModel.TlLcd.Selector, jpeg, offset: 501, chunkIndex: 1);

        Assert.Equal(501, written);
        // Report 2, SyncJPG, total length BE32, packet number BE24, chunk length BE16.
        Assert.Equal(new byte[] { 0x02, 0x46, 0x00, 0x00, 0x04, 0xB0, 0x00, 0x00, 0x01, 0x01, 0xF5 }, report[..11]);
        Assert.Equal(3, JpegPanelProtocol.ChunkCount(JpegPanelModel.TlLcd.HeaderStyle, ReportLength, jpeg.Length));
    }

    [Fact]
    public void Attach_only_reads_the_identity_and_firmware()
    {
        var device = new ScriptedTlLcd();
        device.Queue(Reply(0x3E, SerialPayload("6f1c2a90-7d3e-4b8e-9a51-0c2d4e6f", port: 1, index: 2)));
        device.Queue(Reply(0x3D, Encoding.ASCII.GetBytes("V1.2.3")));
        device.Queue(Reply(0x3D, Encoding.ASCII.GetBytes("2023-05-01")));
        var handshake = new TlLcdHandshake();

        Assert.True(handshake.OnAttach(device, ReportLength));

        Assert.Equal(new TlLcdIdentity(1, 2, "6f1c2a90-7d3e-4b8e-9a51-0c2d4e6f"), handshake.Identity);
        Assert.Equal("V1.2.3 (2023-05-01)", handshake.Firmware);
        // A screen left on its stock firmware gets no writes beyond these two reads.
        Assert.Equal(new byte[] { 0x3E, 0x3D }, device.Writes.Select(w => w[1]).ToArray());
        Assert.All(device.Writes, w => Assert.Equal(ReportLength, w.Length));
    }

    [Fact]
    public void The_display_setting_matches_l_connects_rotation_packet()
    {
        var device = new ScriptedTlLcd();

        Assert.True(TlLcdHandshake.WriteDisplaySetting(device, new byte[ReportLength], rotation: 2));

        // LCDSetting, JPG index 0, L-Connect's default brightness and video fps, the rotation, no test color.
        var control = Assert.Single(device.Writes);
        Assert.Equal(new byte[] { 0x02, 0x40, 0x00, 0x00, 0x00, 0x0B, 0x00, 0x00, 0x00, 0x00, 0x0B }, control[..11]);
        Assert.Equal(new byte[] { 0x05, 0x00, 0x00, 0x00, 50, 30, 0x02, 0x00, 0x00, 0x00, 0x00 }, control[11..22]);
        Assert.All(control[22..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void Attach_never_sends_a_command_that_writes_the_panels_flash()
    {
        var device = new ScriptedTlLcd();
        new TlLcdHandshake().OnAttach(device, ReportLength);

        // Firmware (0x3A, 0x3B), serial number (0x3F), boot media (0x47, 0x48).
        byte[] flashWrites = { 0x3A, 0x3B, 0x3F, 0x47, 0x48 };
        Assert.DoesNotContain(device.Writes, w => flashWrites.Contains(w[1]));
    }

    [Fact]
    public void A_silent_panel_still_attaches_with_an_unknown_identity()
    {
        var device = new ScriptedTlLcd();
        var handshake = new TlLcdHandshake();

        Assert.True(handshake.OnAttach(device, ReportLength));
        Assert.Equal(new TlLcdIdentity(-1, -1, null), handshake.Identity);
        Assert.Null(handshake.Firmware);
    }

    [Fact]
    public void A_refused_read_command_drops_the_handle()
    {
        var device = new ScriptedTlLcd { FailWritesFrom = 0 };

        Assert.False(new TlLcdHandshake().OnAttach(device, ReportLength));
    }

    [Fact]
    public void A_blank_serial_and_out_of_range_place_read_as_unknown()
    {
        var identity = TlLcdHandshake.ParseIdentity(Reply(0x3E, SerialPayload("", port: 7, index: 3)));

        Assert.Equal(new TlLcdIdentity(-1, -1, null), identity);
    }

    [Fact]
    public void A_dead_handle_skips_the_frame_and_flags_the_screen()
    {
        var device = new ScriptedTlLcd { DeadHandle = true };
        var handshake = new TlLcdHandshake();

        Assert.False(handshake.BeforeFrame(device, ReportLength, 0));
        Assert.True(handshake.HandleDead);
    }

    [Fact]
    public void Keys_prefer_the_serial_then_the_place_then_the_path()
    {
        var panels = new TlLcdPanels();

        Assert.Equal("abc", panels.KeyFor(new TlLcdIdentity(0, 1, "abc"), "path-a"));
        Assert.Equal("p0i1", panels.KeyFor(new TlLcdIdentity(0, 1, null), "path-a"));
        Assert.Matches("^[0-9a-f]{8}$", panels.KeyFor(new TlLcdIdentity(-1, -1, null), "path-a"));
        Assert.Equal(
            panels.KeyFor(new TlLcdIdentity(-1, -1, null), "PATH-A"),
            panels.KeyFor(new TlLcdIdentity(-1, -1, null), "path-a"));
    }

    [Fact]
    public void Two_screens_with_one_serial_get_separate_records()
    {
        var panels = new TlLcdPanels();
        var first = new TlLcdIdentity(0, 0, "dup");
        panels.Add(new TlLcdScreen(panels.KeyFor(first, "a"), "A", "a", new JpegPanelHub(JpegPanelModel.TlLcd), new TlLcdHandshake()));

        Assert.Equal("p0i1", panels.KeyFor(new TlLcdIdentity(0, 1, "dup"), "b"));
    }

    [Fact]
    public void Screens_are_named_after_the_fan_hubs_channels()
    {
        Assert.Equal("Lian Li Uni Fan TL LCD #2.3", TlLcdPanels.DisplayNameFor(new TlLcdIdentity(1, 2, null)));
        Assert.Equal("Lian Li Uni Fan TL LCD", TlLcdPanels.DisplayNameFor(new TlLcdIdentity(-1, 0, null)));
    }

    [Fact]
    public void The_worker_attaches_every_screen_and_drops_the_one_that_leaves()
    {
        var gate = new DeviceControlGate(new InMemoryConfigStore());
        gate.SetEnabled(TlLcdPanels.GateHandlerId, true);
        var hid = new FakeWorkerHidEnumerator();
        var screens = new List<HidDeviceInfo>();
        for (int i = 0; i < 3; i++)
        {
            var path = $"tl-lcd-{i}";
            screens.Add(new HidDeviceInfo { VendorId = 0x04FC, ProductId = 0x7393, Path = path, OutputReportByteLength = ReportLength, InputReportByteLength = 64 });
            var device = new ScriptedTlLcd();
            device.Queue(Reply(0x3E, SerialPayload("", port: 0, index: i)));
            hid.DevicesByPath[path] = device;
        }
        hid.ByProductId[0x7393] = screens;
        var presence = new HardwarePresence(new FixedUsb(new UsbDeviceEntry { VendorId = 0x04FC, ProductId = 0x7393 }));
        var panels = new TlLcdPanels();
        var worker = new TlLcdConnectionWorker(hid, panels, gate, presence);

        worker.Sync();
        Assert.Equal(
            new[] { "p0i0", "p0i1", "p0i2" },
            panels.Attached.Select(s => s.Key).OrderBy(k => k).ToArray());

        screens.RemoveAt(1);
        worker.Sync();
        Assert.Equal(
            new[] { "p0i0", "p0i2" },
            panels.Attached.Select(s => s.Key).OrderBy(k => k).ToArray());
    }

    [Fact]
    public void A_screen_whose_handle_died_is_reopened_on_the_next_poll()
    {
        var gate = new DeviceControlGate(new InMemoryConfigStore());
        gate.SetEnabled(TlLcdPanels.GateHandlerId, true);
        var hid = new FakeWorkerHidEnumerator();
        hid.ByProductId[0x7393] = new List<HidDeviceInfo>
        {
            new() { VendorId = 0x04FC, ProductId = 0x7393, Path = "tl-lcd-0", OutputReportByteLength = ReportLength },
        };
        var device = new ScriptedTlLcd();
        hid.DevicesByPath["tl-lcd-0"] = device;
        var panels = new TlLcdPanels();
        var worker = new TlLcdConnectionWorker(
            hid, panels, gate,
            new HardwarePresence(new FixedUsb(new UsbDeviceEntry { VendorId = 0x04FC, ProductId = 0x7393 })));
        worker.Sync();
        var first = Assert.Single(panels.Attached);

        device.DeadHandle = true;
        Assert.False(first.Hub.SendFrame(new byte[16]));
        device.DeadHandle = false;
        worker.Sync();

        var second = Assert.Single(panels.Attached);
        Assert.NotSame(first, second);
        Assert.Equal(first.Key, second.Key);
    }

    [Fact]
    public void The_worker_claims_nothing_while_nexus_control_is_off()
    {
        var hid = new FakeWorkerHidEnumerator();
        hid.ByProductId[0x7393] = new List<HidDeviceInfo>
        {
            new() { VendorId = 0x04FC, ProductId = 0x7393, Path = "tl-lcd-0", OutputReportByteLength = ReportLength },
        };
        hid.DevicesByPath["tl-lcd-0"] = new ScriptedTlLcd();
        var panels = new TlLcdPanels();
        var worker = new TlLcdConnectionWorker(
            hid, panels, new DeviceControlGate(new InMemoryConfigStore()),
            new HardwarePresence(new FixedUsb(new UsbDeviceEntry { VendorId = 0x04FC, ProductId = 0x7393 })));

        worker.Sync();

        Assert.False(panels.AnyAttached);
    }

    private static (TlLcdScreens Screens, ScriptedTlLcd Device, TlLcdPanels Panels) AttachedScreen()
    {
        var device = new ScriptedTlLcd();
        var handshake = new TlLcdHandshake();
        var hub = new JpegPanelHub(JpegPanelModel.TlLcd with { Handshake = handshake });
        Assert.True(hub.Attach(device));
        var panels = new TlLcdPanels();
        panels.Add(new TlLcdScreen("s1", "S1", "path-1", hub, handshake));
        device.ClearWrites();
        return (new TlLcdScreens(panels), device, panels);
    }

    [Fact]
    public void The_first_nexus_frame_carries_the_rotation_and_later_frames_do_not()
    {
        var (screens, device, _) = AttachedScreen();
        using var disposeScreens = screens;
        screens.SetRotation("s1", 1);

        Assert.True(screens.PushImage("s1", new byte[600]));
        Assert.True(screens.PushImage("s1", new byte[600]));

        var commands = device.WriteCommands();
        Assert.Equal(TlLcdHandshake.CmdLcdControl, commands[0]);
        Assert.Equal(1, commands.Count(c => c == TlLcdHandshake.CmdLcdControl));
        Assert.All(commands.Skip(1), c => Assert.Equal(TlLcdHandshake.CmdSyncJpeg, c));
    }

    [Fact]
    public void A_rotation_alone_writes_nothing_so_a_stock_screen_keeps_its_own()
    {
        var (screens, device, _) = AttachedScreen();
        using var disposeScreens = screens;

        screens.SetRotation("s1", 3);

        Assert.Empty(device.WriteCommands());
    }

    [Fact]
    public void A_quiet_screen_gets_its_last_frame_again_until_released()
    {
        var (screens, device, _) = AttachedScreen();
        using var disposeScreens = screens;
        screens.PushImage("s1", new byte[600]);
        long sentAt = Environment.TickCount64;
        int afterFirst = device.WriteCommands().Count;
        System.Threading.SpinWait.SpinUntil(() => Environment.TickCount64 - sentAt > TlLcdScreens.KeepaliveMs + 20);

        // The background timer may already have re-sent; either way the frame went out again.
        screens.Keepalive();
        Assert.True(device.WriteCommands().Count > afterFirst);

        screens.Release("s1");
        int afterRelease = device.WriteCommands().Count;
        System.Threading.SpinWait.SpinUntil(() => Environment.TickCount64 - sentAt > (3 * TlLcdScreens.KeepaliveMs) + 20);
        screens.Keepalive();
        Assert.Equal(afterRelease, device.WriteCommands().Count);
    }

    [Fact]
    public void A_push_to_a_detached_screen_is_refused()
    {
        var (screens, _, _) = AttachedScreen();
        using var disposeScreens = screens;

        Assert.False(screens.PushImage("gone", new byte[600]));
    }

    private static byte[] SerialPayload(string serial, int port, int index)
    {
        var data = new byte[34];
        Encoding.ASCII.GetBytes(serial).AsSpan(0, Math.Min(serial.Length, 32)).CopyTo(data);
        data[32] = (byte)port;
        data[33] = (byte)index;
        return data;
    }

    /// <summary>Report 2, the echoed command, then the sequenced header's length fields.</summary>
    private static byte[] Reply(byte command, byte[] payload)
    {
        var reply = new byte[64];
        reply[0] = 0x02;
        reply[1] = command;
        reply[5] = (byte)payload.Length;
        reply[9] = (byte)(payload.Length >> 8);
        reply[10] = (byte)payload.Length;
        payload.CopyTo(reply, 11);
        return reply;
    }

    private sealed class FixedUsb : IUsbEnumerator
    {
        private readonly UsbDeviceEntry[] _entries;
        public FixedUsb(params UsbDeviceEntry[] entries) => _entries = entries;
        public List<UsbDeviceEntry> Enumerate() => _entries.ToList();
    }

    /// <summary>Answers each timed read from a queue; a zero-timeout drain read finds nothing queued.</summary>
    private sealed class ScriptedTlLcd : IHidDevice
    {
        private readonly Queue<byte[]> _replies = new();
        public List<byte[]> Writes { get; } = new();
        public int? FailWritesFrom { get; set; }
        public bool DeadHandle { get; set; }

        public void Queue(byte[] reply) => _replies.Enqueue(reply);

        public int VendorId => 0x04FC;
        public int ProductId => 0x7393;
        public string Path => "fake-tl-lcd";
        public string? Serial => "TL_LCDV0.1";
        public int UsagePage => 0xFF06;
        public int Usage => 0x01;

        public bool Write(ReadOnlySpan<byte> report)
        {
            lock (Writes)
            {
                bool ok = !(FailWritesFrom is { } from && Writes.Count >= from);
                Writes.Add(report.ToArray());
                return ok;
            }
        }

        /// <summary>The command byte of every write so far; safe while a keepalive timer writes.</summary>
        public List<byte> WriteCommands()
        {
            lock (Writes)
            {
                return Writes.Select(w => w[1]).ToList();
            }
        }

        public void ClearWrites()
        {
            lock (Writes)
            {
                Writes.Clear();
            }
        }

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            if (DeadHandle)
            {
                return -1;
            }
            if (timeoutMs == 0 || _replies.Count == 0)
            {
                return 0;
            }
            var reply = _replies.Dequeue();
            int copy = Math.Min(reply.Length, buffer.Length);
            reply.AsSpan(0, copy).CopyTo(buffer);
            return copy;
        }

        public bool SetFeature(ReadOnlySpan<byte> report) => true;
        public bool GetFeature(Span<byte> buffer) => false;
        public bool GetInputReport(Span<byte> buffer) => false;
        public bool SetOutputReport(ReadOnlySpan<byte> report) => false;
        public void Dispose() { }
    }
}
