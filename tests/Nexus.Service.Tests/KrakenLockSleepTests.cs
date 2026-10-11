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
        public int? PersistedRestore;

        public Rig(int brightness = 60)
        {
            Hub = new KrakenHub(new Factory());
            Hub.BindLockSleep(Lock, () => SleepSetting, () => PersistedRestore, level => PersistedRestore = level);
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

    [Fact]
    public async System.Threading.Tasks.Task A_lock_handler_that_decided_before_the_unlock_does_not_dim_after_it()
    {
        var rig = new Rig(60);
        using var entered = new System.Threading.ManualResetEventSlim();
        using var release = new System.Threading.ManualResetEventSlim();
        // The handler has been told "locked" and waits for the device locks (a bucket upload
        // holds them for ~450 ms) while the unlock arrives.
        rig.Hub.TestHookBeforeLockEnter = () => { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); };
        var handler = System.Threading.Tasks.Task.Run(() => rig.Lock.Set(true));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));

        rig.Hub.TestHookBeforeLockEnter = null;
        rig.Lock.Set(false);
        release.Set();
        await handler.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(rig.Hub.LockDimmed);
        Assert.Equal(0, rig.BacklightWrites);
        Assert.True(rig.Hub.PushStreamFrame(new byte[EliteNoRgb.LcdFrameBytes]));
        Assert.NotEmpty(rig.Hid.Writes);
    }

    [Fact]
    public void A_failed_restoring_write_keeps_the_marker_and_the_next_poll_retries_it()
    {
        var rig = new Rig(60);
        rig.Lock.Set(true);
        rig.Hid.Writes.Clear();
        rig.Hid.FailWrites = true;

        rig.Lock.Set(false);

        Assert.Equal(60, rig.PersistedRestore);
        rig.Hid.FailWrites = false;
        rig.Hub.Poll();
        Assert.True(rig.Wrote(60));
        Assert.Null(rig.PersistedRestore);
    }

    [Fact]
    public void A_change_held_while_dark_is_applied_once_on_a_reconnect_after_the_unlock_and_never_replayed()
    {
        var rig = new Rig(60);
        rig.Lock.Set(true);
        Assert.True(rig.Hub.SetDisplayMode(KrakenDisplayMode.Liquid));
        rig.Hub.Detach();
        rig.Lock.Set(false);
        rig.Hid = new Recorder();

        rig.Connect();

        var modeWrite = KrakenProtocol.EncodeSetDisplayMode(KrakenDisplayMode.Liquid, 0);
        Assert.Contains(rig.Hid.Writes, w => w[0] == modeWrite[0] && w[1] == modeWrite[1] && w[2] == modeWrite[2]);

        rig.Hid.Writes.Clear();
        rig.Lock.Set(true);
        rig.Lock.Set(false);
        Assert.DoesNotContain(rig.Hid.Writes, w => w[0] == modeWrite[0] && w[1] == modeWrite[1] && w[2] == modeWrite[2]);
    }

    [Fact]
    public void A_level_set_while_dark_is_the_persisted_restore_level()
    {
        var rig = new Rig(60);
        rig.Lock.Set(true);

        Assert.True(rig.Hub.SetLcdBacklight(25, 0));

        Assert.Equal(25, rig.PersistedRestore);
    }

    [Fact]
    public void The_pre_lock_level_is_persisted_while_dark_and_cleared_on_unlock()
    {
        var rig = new Rig(60);

        rig.Lock.Set(true);
        Assert.Equal(60, rig.PersistedRestore);
        Assert.Equal(60, rig.Hub.Snapshot.LcdBrightness);

        rig.Lock.Set(false);
        Assert.Null(rig.PersistedRestore);
    }

    [Fact]
    public void A_restart_while_dark_restores_the_persisted_level_on_connect()
    {
        // A previous run left the cooler dark with its level persisted; this run starts unlocked
        // and the cooler reads back zero.
        var lockState = new LcdLockState();
        int? persisted = 60;
        var hub = new KrakenHub(new Factory());
        hub.BindLockSleep(lockState, () => true, () => persisted, level => persisted = level);
        var hid = new Recorder();

        hub.Attach(hid, EliteNoRgb, KrakenProtocol.ReportLength);
        Assert.True(hub.Connect());

        Assert.Equal(60, hub.Snapshot.LcdBrightness);
        Assert.Contains(hid.Writes, w => w.AsSpan(0, KrakenProtocol.ReportLength).SequenceEqual(KrakenProtocol.EncodeSetBacklight(60, 0)));
        Assert.Null(persisted);
    }

    [Fact]
    public void Display_mode_and_image_changes_while_dark_are_held_until_unlock()
    {
        var rig = new Rig(60);
        Assert.True(rig.Hub.UploadLcdImage(new byte[EliteNoRgb.LcdFrameBytes]));
        rig.Lock.Set(true);
        rig.Hid.Writes.Clear();

        Assert.True(rig.Hub.SetDisplayMode(KrakenDisplayMode.Liquid));
        Assert.True(rig.Hub.UploadLcdImage(new byte[EliteNoRgb.LcdFrameBytes]));
        Assert.True(rig.Hub.SetLcdBacklight(60, 1));

        // Nothing but the dark backlight reached the cooler: no mode switch, no bucket traffic.
        Assert.All(rig.Hid.Writes, w => Assert.Equal(KrakenProtocol.EncodeSetBacklight(0, 0)[0], w[0]));
        Assert.False(rig.Wrote(60));

        rig.Hid.Writes.Clear();
        rig.Lock.Set(false);
        Assert.Contains(rig.Hid.Writes, w => w[0] != KrakenProtocol.EncodeSetBacklight(0, 0)[0]);
        Assert.Equal(KrakenProtocol.EncodeSetBacklight(60, 1)[3], rig.Hid.Writes[^1][3]);
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

        public bool FailWrites { get; set; }

        public bool Write(ReadOnlySpan<byte> report)
        {
            if (FailWrites)
            {
                return false;
            }
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
