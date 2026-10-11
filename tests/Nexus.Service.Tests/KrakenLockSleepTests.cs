using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.Nzxt;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>Lock sleep on the Kraken hub against a recording HID double; no hardware.</summary>
public class KrakenLockSleepTests
{
    private static readonly KrakenModel EliteNoRgb = KrakenModel.Find(0x300C)!;

    private sealed class Rig
    {
        public readonly LcdLockState Lock = new();
        public readonly KrakenHub Hub;
        public Recorder Hid = new();
        public bool SleepSetting = true;

        public Rig(int brightness = 60)
        {
            Hub = new KrakenHub(new Factory());
            Hub.BindLockSleep(Lock, () => SleepSetting);
            Connect();
            Assert.True(Hub.SetLcdBacklight(brightness, 0));
            Hid.Writes.Clear();
        }

        public void Connect()
        {
            Hub.Attach(Hid, EliteNoRgb, KrakenProtocol.ReportLength);
            Assert.True(Hub.Connect());
        }

        public void Reconnect()
        {
            Hub.Detach();
            Hid = new Recorder();
            Connect();
        }

        public bool Wrote(int brightness) =>
            Hid.Writes.Any(w => w.AsSpan(0, KrakenProtocol.ReportLength).SequenceEqual(KrakenProtocol.EncodeSetBacklight(brightness, 0)));

        public int BacklightWrites =>
            Hid.Writes.Count(w => w[0] == KrakenProtocol.EncodeSetBacklight(0, 0)[0] && w[1] == KrakenProtocol.EncodeSetBacklight(0, 0)[1]);
    }

    [Fact]
    public void Lock_darkens_the_backlight_and_unlock_restores_it_without_touching_the_snapshot()
    {
        var rig = new Rig(60);

        rig.Lock.Set(true);
        Assert.True(rig.Wrote(0));
        Assert.True(rig.Hub.LockDimmed);
        Assert.Equal(60, rig.Hub.Snapshot.LcdBrightness);

        rig.Hid.Writes.Clear();
        rig.Lock.Set(false);
        Assert.True(rig.Wrote(60));
        Assert.False(rig.Hub.LockDimmed);
        Assert.Equal(60, rig.Hub.Snapshot.LcdBrightness);
    }

    [Fact]
    public void Streamed_frames_are_not_pushed_while_dark()
    {
        var rig = new Rig();
        var frame = new byte[EliteNoRgb.LcdFrameBytes];
        rig.Lock.Set(true);
        rig.Hid.Writes.Clear();

        Assert.True(rig.Hub.PushStreamFrame(frame));

        Assert.Empty(rig.Hid.Writes);
        rig.Lock.Set(false);
        rig.Hid.Writes.Clear();
        Assert.True(rig.Hub.PushStreamFrame(frame));
        Assert.NotEmpty(rig.Hid.Writes);
    }

    [Fact]
    public void A_level_set_while_locked_is_remembered_and_applied_on_unlock()
    {
        var rig = new Rig(60);
        rig.Lock.Set(true);
        rig.Hid.Writes.Clear();

        Assert.True(rig.Hub.SetLcdBacklight(25, 0));

        Assert.True(rig.Wrote(0));
        Assert.False(rig.Wrote(25));
        Assert.Equal(25, rig.Hub.Snapshot.LcdBrightness);
        rig.Hid.Writes.Clear();
        rig.Lock.Set(false);
        Assert.True(rig.Wrote(25));
    }

    [Fact]
    public void A_setting_that_is_off_leaves_the_backlight_alone()
    {
        var rig = new Rig();
        rig.SleepSetting = false;

        rig.Lock.Set(true);

        Assert.Equal(0, rig.BacklightWrites);
        Assert.False(rig.Hub.LockDimmed);
    }

    [Fact]
    public void Unlock_restores_even_when_the_setting_was_turned_off_mid_lock()
    {
        var rig = new Rig(60);
        rig.Lock.Set(true);
        rig.SleepSetting = false;
        rig.Hid.Writes.Clear();

        rig.Lock.Set(false);

        Assert.True(rig.Wrote(60));
    }

    [Fact]
    public void A_reconnect_while_locked_stays_dark_and_keeps_the_users_level()
    {
        var rig = new Rig(60);
        rig.Lock.Set(true);

        rig.Reconnect();

        Assert.True(rig.Wrote(0));
        Assert.False(rig.Wrote(60));
        Assert.Equal(60, rig.Hub.Snapshot.LcdBrightness);
        rig.Hid.Writes.Clear();
        rig.Lock.Set(false);
        Assert.True(rig.Wrote(60));
    }

    [Fact]
    public void An_unlock_while_detached_restores_on_the_next_connect()
    {
        var rig = new Rig(60);
        rig.Lock.Set(true);
        rig.Hub.Detach();

        rig.Lock.Set(false);
        rig.Hid = new Recorder();
        rig.Connect();

        Assert.True(rig.Wrote(60));
        Assert.Equal(60, rig.Hub.Snapshot.LcdBrightness);
    }

    [Fact]
    public void A_cooler_attached_while_locked_joins_the_sleep()
    {
        var lockState = new LcdLockState();
        var hub = new KrakenHub(new Factory());
        hub.BindLockSleep(lockState, () => true);
        lockState.Set(true);
        var hid = new Recorder();

        hub.Attach(hid, EliteNoRgb, KrakenProtocol.ReportLength);
        Assert.True(hub.Connect());

        Assert.Contains(hid.Writes, w => w.AsSpan(0, KrakenProtocol.ReportLength).SequenceEqual(KrakenProtocol.EncodeSetBacklight(0, 0)));
        Assert.True(hub.LockDimmed);
    }

    private sealed class Recorder : IHidDevice
    {
        private readonly Queue<byte[]> _replies = new();
        public List<byte[]> Writes { get; } = new();
        public int VendorId => 0x1E71;
        public int ProductId => 0x300C;
        public string Path => "fake";
        public string? Serial => "TEST";
        public int UsagePage => 0xFF00;
        public int Usage => 1;

        public bool Write(ReadOnlySpan<byte> report)
        {
            var copy = report.ToArray();
            Writes.Add(copy);
            var reply = new byte[KrakenProtocol.ReportLength];
            reply[0] = (byte)(copy[0] + 1);
            reply[1] = copy[1];
            reply[14] = KrakenProtocol.AckOk;
            _replies.Enqueue(reply);
            return true;
        }

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            if (_replies.Count == 0) return 0;
            var r = _replies.Dequeue();
            r.CopyTo(buffer);
            return r.Length;
        }

        public bool SetFeature(ReadOnlySpan<byte> report) => true;
        public bool GetFeature(Span<byte> buffer) => false;
        public bool GetInputReport(Span<byte> buffer) => false;
        public bool SetOutputReport(ReadOnlySpan<byte> report) => true;
        public void Dispose() { }
    }

    private sealed class Lcd : IKrakenLcdTransport
    {
        public bool Write(ReadOnlySpan<byte> data) => true;
        public void Dispose() { }
    }

    private sealed class Factory : IKrakenLcdTransportFactory
    {
        public IKrakenLcdTransport? Open(string? serial) => new Lcd();
    }
}
