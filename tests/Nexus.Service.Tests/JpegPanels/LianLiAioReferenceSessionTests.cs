using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.JpegPanels;
using Xunit;

namespace Nexus.Service.Tests.JpegPanels;

/// <summary>
/// The session the HydroShift LCD follows from the reference driver (sgtaziz/lian-li-linux
/// hydroshift_lcd): settle, firmware, status handshake, pause, claim, then a status
/// keepalive and LCD availability recovery. Fake transport and recorded sleeps, so these pin
/// ordering and bytes, not what a panel does with them.
/// </summary>
public class LianLiAioReferenceSessionTests
{
    private const int ReportLength = 1024;

    private static LianLiAioHandshake Session(List<int>? sleeps = null, List<string>? order = null) =>
        new("lianli-hydroshift-lcd", 24, referenceSession: true, ms =>
        {
            sleeps?.Add(ms);
            order?.Add("sleep");
        }, () => 0);

    [Fact]
    public void Attach_orders_firmware_then_status_handshake_then_a_pause_then_the_claim()
    {
        var order = new List<string>();
        var sleeps = new List<int>();
        var device = new FakeDevice { OnWrite = w => order.Add($"{w[0]:X2}{w[1]:X2}") };

        Assert.True(Session(sleeps, order).OnAttach(device, ReportLength));

        Assert.Equal(new[] { "0186", "0181", "sleep", "020C" }, order);
        Assert.Equal(new[] { 2000 }, sleeps);
    }

    [Fact]
    public void The_reference_session_settles_before_attach_and_ticks_once_a_second()
    {
        var handshake = Session();

        Assert.True(handshake.SettleMs >= 10_000);
        Assert.Equal(1000, handshake.TickIntervalMs);
    }

    [Fact]
    public void The_plain_family_handshake_keeps_its_verified_attach_and_does_not_tick()
    {
        var handshake = new LianLiAioHandshake("lianli-galahad2-lcd", 24);
        var device = new FakeDevice();

        Assert.True(handshake.OnAttach(device, ReportLength));
        handshake.Tick(device, ReportLength, 60_000);

        Assert.Equal(0, handshake.SettleMs);
        Assert.Equal(0, handshake.TickIntervalMs);
        Assert.Equal(new[] { 0x86, 0x0C }, device.Writes.Select(w => (int)w[1]));
    }

    [Fact]
    public void Only_the_hydroshift_model_opts_into_the_reference_session()
    {
        Assert.True(JpegPanelModel.HydroShiftLcd.Handshake is IJpegPanelSession { SettleMs: > 0 });
        Assert.True(JpegPanelModel.GalahadIiLcd.Handshake is IJpegPanelSession { SettleMs: 0 });
    }

    [Fact]
    public void A_silent_firmware_is_retried_with_a_pause_between_attempts()
    {
        var sleeps = new List<int>();
        var device = new FakeDevice { ReadResult = 0 };

        Assert.True(Session(sleeps).OnAttach(device, ReportLength));

        Assert.Equal(3, device.Writes.Count(w => w[0] == 0x01 && w[1] == 0x86));
        Assert.Equal(new[] { 2000, 2000, 2000 }, sleeps);
    }

    [Fact]
    public void The_keepalive_polls_status_once_a_second_not_faster()
    {
        var handshake = Session();
        var device = new FakeDevice();
        handshake.OnAttach(device, ReportLength);
        device.Writes.Clear();

        handshake.Tick(device, ReportLength, 500);
        Assert.Empty(device.Writes);

        handshake.Tick(device, ReportLength, 1000);
        var poll = Assert.Single(device.Writes);
        Assert.Equal(new byte[] { 0x01, 0x81, 0, 0, 0, 0 }, poll[..6]);

        handshake.Tick(device, ReportLength, 1500);
        Assert.Single(device.Writes, w => w[1] == 0x81);

        handshake.Tick(device, ReportLength, 2000);
        Assert.Equal(2, device.Writes.Count(w => w[1] == 0x81));
    }

    [Fact]
    public void The_availability_request_is_a_bare_report_b_with_command_0x17()
    {
        var handshake = Session();
        var device = new FakeDevice();
        handshake.OnAttach(device, ReportLength);
        device.Writes.Clear();

        handshake.Tick(device, ReportLength, 2000);

        var request = Assert.Single(device.Writes, w => w[0] == 0x02);
        Assert.Equal(0x17, request[1]);
        Assert.All(request[2..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void An_available_lcd_is_never_reset()
    {
        var handshake = Session();
        var device = new FakeDevice();
        handshake.OnAttach(device, ReportLength);
        device.Writes.Clear();

        for (long t = 2000; t <= 20_000; t += 1000)
        {
            handshake.Tick(device, ReportLength, t);
        }

        Assert.Contains(device.Writes, w => w[0] == 0x02 && w[1] == 0x17);
        Assert.DoesNotContain(device.Writes, w => w[0] == 0x01 && w[1] == 0x8E);
    }

    [Fact]
    public void A_silent_panel_counts_as_available()
    {
        var handshake = Session();
        var device = new FakeDevice();
        handshake.OnAttach(device, ReportLength);
        device.ReadResult = 0;
        device.Writes.Clear();

        for (long t = 2000; t <= 20_000; t += 2000)
        {
            handshake.Tick(device, ReportLength, t);
        }

        Assert.DoesNotContain(device.Writes, w => w[0] == 0x01 && w[1] == 0x8E);
    }

    [Fact]
    public void Three_unavailable_reports_reset_the_panel_then_reclaim_the_glass()
    {
        var sleeps = new List<int>();
        var handshake = Session(sleeps);
        var device = new FakeDevice { LcdUnavailable = true };
        handshake.OnAttach(device, ReportLength);
        device.Writes.Clear();
        sleeps.Clear();

        handshake.Tick(device, ReportLength, 2000);
        handshake.Tick(device, ReportLength, 4000);
        Assert.DoesNotContain(device.Writes, w => w[0] == 0x01 && w[1] == 0x8E);

        handshake.Tick(device, ReportLength, 6000);

        var tail = device.Writes
            .Where(w => (w[0] == 0x01 && w[1] == 0x8E) || (w[0] == 0x02 && w[1] == 0x0C))
            .ToList();
        Assert.Equal(2, tail.Count);
        Assert.Equal(0x8E, tail[0][1]);
        Assert.Equal(0x0C, tail[1][1]);
        Assert.Equal(0x01, tail[1][11]);
        Assert.Equal(100, tail[1][12]);
        Assert.Equal(new[] { 500 }, sleeps);
    }

    [Fact]
    public void A_failed_reset_still_reclaims_the_glass()
    {
        var sleeps = new List<int>();
        var handshake = Session(sleeps);
        var device = new FakeDevice { LcdUnavailable = true, ResetFails = true };
        handshake.OnAttach(device, ReportLength);
        device.Writes.Clear();
        sleeps.Clear();

        handshake.Tick(device, ReportLength, 2000);
        handshake.Tick(device, ReportLength, 4000);
        handshake.Tick(device, ReportLength, 6000);

        Assert.Contains(device.Writes, w => w[0] == 0x01 && w[1] == 0x8E);
        Assert.Contains(device.Writes, w => w[0] == 0x02 && w[1] == 0x0C && w[11] == 0x01);
        Assert.Empty(sleeps);
    }

    [Fact]
    public void Recovery_waits_out_the_cooldown_before_checking_again()
    {
        var handshake = Session();
        var device = new FakeDevice { LcdUnavailable = true };
        handshake.OnAttach(device, ReportLength);
        handshake.Tick(device, ReportLength, 2000);
        handshake.Tick(device, ReportLength, 4000);
        handshake.Tick(device, ReportLength, 6000);
        device.Writes.Clear();

        handshake.Tick(device, ReportLength, 7000);

        Assert.DoesNotContain(device.Writes, w => w[0] == 0x02 && w[1] == 0x17);
    }

    [Fact]
    public void No_availability_check_runs_while_frames_are_flowing()
    {
        var handshake = Session();
        var device = new FakeDevice { LcdUnavailable = true };
        handshake.OnAttach(device, ReportLength);
        device.Writes.Clear();

        for (long t = 2000; t <= 12_000; t += 1000)
        {
            handshake.BeforeFrame(device, ReportLength, t);
            handshake.Tick(device, ReportLength, t);
        }

        Assert.DoesNotContain(device.Writes, w => w[0] == 0x02 && w[1] == 0x17);
    }

    [Fact]
    public void The_hub_ticks_an_attached_session_and_counts_a_rejected_frame()
    {
        var model = JpegPanelModel.HydroShiftLcd with { Handshake = Session() };
        using var hub = new JpegPanelHub(model);
        var device = new FakeDevice();
        Assert.True(hub.Attach(device));
        device.Writes.Clear();

        Assert.Equal(1000, hub.TickIntervalMs);
        Assert.True(hub.SettleMs > 0);

        device.FailWrites = true;
        Assert.False(hub.SendFrame(new byte[] { 1, 2, 3 }));
        Assert.False(hub.SendFrame(new byte[] { 1, 2, 3 }));

        Assert.Equal(2, hub.FrameFailures);
    }

    private static byte[] AResponse(byte cmd, string text)
    {
        var frame = new byte[64];
        frame[0] = 0x01;
        frame[1] = cmd;
        var payload = Encoding.ASCII.GetBytes(text);
        frame[5] = (byte)payload.Length;
        payload.CopyTo(frame, 6);
        return frame;
    }

    /// <summary>Answers each write the way the panel does: echoing A-commands, reporting LCD availability, acking resets.</summary>
    private sealed class FakeDevice : IHidDevice
    {
        public List<byte[]> Writes { get; } = new();
        public Action<byte[]>? OnWrite { get; set; }
        public int ReadResult { get; set; } = 64;
        public bool LcdUnavailable { get; set; }
        public bool ResetFails { get; set; }
        public bool FailWrites { get; set; }

        public int VendorId => 0x0416;
        public int ProductId => 0x7399;
        public string Path => "/dev/fake-hydroshift-v1";
        public string? Serial => "FAKE";
        public int UsagePage => 0xFF1A;
        public int Usage => 0x93;

        public bool Write(ReadOnlySpan<byte> report)
        {
            var copy = report.ToArray();
            Writes.Add(copy);
            OnWrite?.Invoke(copy);
            return !FailWrites;
        }

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            if (ReadResult <= 0)
            {
                return ReadResult;
            }
            var reply = Reply();
            int copy = Math.Min(reply.Length, buffer.Length);
            reply.AsSpan(0, copy).CopyTo(buffer);
            return copy;
        }

        private byte[] Reply()
        {
            var last = Writes.Count > 0 ? Writes[^1] : new byte[ReportLength];
            if (last[0] == 0x01 && last[1] == 0x8E)
            {
                var reset = AResponse(0x8E, "x");
                reset[6] = (byte)(ResetFails ? 0 : 1);
                return reset;
            }
            if (last[0] == 0x01)
            {
                return AResponse(last[1], "1.3");
            }
            if (last[0] == 0x02 && last[1] == 0x17)
            {
                var b = new byte[ReportLength];
                b[0] = 0x02;
                b[1] = 0x17;
                b[10] = 1;
                b[11] = (byte)(LcdUnavailable ? 1 : 0);
                return b;
            }
            return AResponse(0x0C, "");
        }

        public bool SetFeature(ReadOnlySpan<byte> report) => true;
        public bool GetFeature(Span<byte> buffer) => false;
        public bool GetInputReport(Span<byte> buffer) => false;
        public bool SetOutputReport(ReadOnlySpan<byte> report) => false;
        public void Dispose() { }
    }
}
