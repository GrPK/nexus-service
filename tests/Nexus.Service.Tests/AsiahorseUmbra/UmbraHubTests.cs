using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Peripherals.AsiahorseUmbra;
using Xunit;

namespace Nexus.Service.Tests.AsiahorseUmbra;

public class UmbraHubTests
{
    private static (UmbraHub hub, UmbraDeviceFake fake) Connected(bool leadingReportId = false)
    {
        var fake = new UmbraDeviceFake(leadingReportId);
        fake.Ports[9][0] = 6;
        var hub = new UmbraHub();
        hub.Attach(fake);
        Assert.True(hub.Connect());
        return (hub, fake);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Connect_reads_info_and_ports(bool leadingReportId)
    {
        var (hub, _) = Connected(leadingReportId);
        using (hub)
        {
            var snap = hub.Snapshot;
            Assert.True(hub.IsConnected);
            Assert.Equal("CDAB2F9648BC44FE", snap.DeviceId);
            Assert.Equal("1.0.6", snap.Firmware);
            Assert.Equal(6, snap.Ports[9].LedCount);
            Assert.Equal(6, snap.TotalLeds());
            Assert.Equal(1, hub.ConnectEpoch);
        }
    }

    [Fact]
    public void Connect_sends_only_info_and_port_reads()
    {
        var (hub, fake) = Connected();
        using (hub)
        {
            Assert.Equal(new byte[] { 0x00, 0x01 }, fake.Writes.Select(w => w[4]).ToArray());
        }
    }

    [Fact]
    public void ReleaseWithHandBack_writes_the_frames_then_drops_the_device()
    {
        var (hub, fake) = Connected();
        using (hub)
        {
            fake.Writes.Clear();
            var effect = UmbraProtocol.EncodeSyncEffect(new UmbraEffectBlock(1, 3, 1, 153, new byte[12]));

            hub.ReleaseWithHandBack(new[] { UmbraProtocol.EncodeAuthority(true), effect });

            Assert.Equal(new byte[] { 0xFD, 0x03 }, fake.Writes.Select(w => w[4]).ToArray());
            Assert.False(hub.IsConnected);
            Assert.False(hub.SendFrame(effect));
            Assert.Equal(2, fake.Writes.Count);
        }
    }

    [Fact]
    public void Connect_fails_when_the_device_does_not_answer()
    {
        var hub = new UmbraHub();
        hub.Attach(new SilentDevice());
        using (hub)
        {
            Assert.False(hub.Connect());
            Assert.False(hub.IsConnected);
        }
    }

    [Fact]
    public void WritePort_writes_then_rereads_and_publishes_the_device_values()
    {
        var (hub, fake) = Connected();
        using (hub)
        {
            fake.Writes.Clear();

            Assert.True(hub.WritePort(2, UmbraPortField.LedCount, 24));

            Assert.Equal(new byte[] { 0x02, 0x01 }, fake.Writes.Select(w => w[4]).ToArray());
            Assert.Equal(24, hub.Snapshot.Ports[2].LedCount);
        }
    }

    [Fact]
    public void WritePort_rejects_a_port_out_of_range()
    {
        var (hub, fake) = Connected();
        using (hub)
        {
            fake.Writes.Clear();
            Assert.False(hub.WritePort(10, UmbraPortField.LedCount, 1));
            Assert.Empty(fake.Writes);
        }
    }

    [Fact]
    public void SetFanDuty_clamps_and_records_the_duty()
    {
        var (hub, fake) = Connected();
        using (hub)
        {
            fake.Writes.Clear();

            Assert.True(hub.SetFanDuty(140));

            Assert.Equal(100, fake.Writes[0][5]);
            Assert.Equal(100, hub.Snapshot.FanDuty);
        }
    }

    [Fact]
    public void SetBootFixed_sends_both_frames_then_republishes_the_info()
    {
        var (hub, fake) = Connected();
        using (hub)
        {
            fake.Writes.Clear();

            Assert.True(hub.SetBootFixed());

            Assert.Equal(new byte[] { 0x0B, 0x03, 0x00 }, fake.Writes.Select(w => w[4]).ToArray());
            Assert.False(hub.Snapshot.BootMemory);
        }
    }

    [Fact]
    public void SetSelfCheck_republishes_the_info()
    {
        var (hub, _) = Connected();
        using (hub)
        {
            Assert.True(hub.SetSelfCheck(false));
            Assert.False(hub.Snapshot.SelfCheck);
        }
    }

    [Fact]
    public void PollInfo_is_false_only_when_the_device_is_gone()
    {
        var (hub, fake) = Connected();
        using (hub)
        {
            Assert.True(hub.PollInfo());
            fake.Gone = true;
            Assert.False(hub.PollInfo());
        }
    }

    [Fact]
    public async Task RestartAsync_sends_the_restart_and_completes_when_the_device_reconnects()
    {
        var (hub, fake) = Connected();
        using (hub)
        {
            var epoch = hub.ConnectEpoch;
            fake.Writes.Clear();

            var restart = hub.RestartAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

            Assert.Equal(0xFC, fake.Writes[0][4]);
            Assert.True(hub.IsRestarting);
            Assert.False(restart.IsCompleted);

            hub.Detach();
            var replacement = new UmbraDeviceFake();
            replacement.Ports[0][0] = 12;
            hub.Attach(replacement);
            Assert.True(hub.Connect());

            Assert.Equal(UmbraRestartResult.Reconnected, await restart);
            Assert.False(hub.IsRestarting);
            Assert.True(hub.ConnectEpoch > epoch);
            Assert.Equal(12, hub.Snapshot.Ports[0].LedCount);
        }
    }

    [Fact]
    public async Task RestartAsync_times_out_when_the_device_never_returns()
    {
        var (hub, _) = Connected();
        using (hub)
        {
            Assert.Equal(UmbraRestartResult.TimedOut, await hub.RestartAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None));
            Assert.False(hub.IsRestarting);
        }
    }

    [Fact]
    public async Task A_second_restart_while_one_is_pending_is_busy()
    {
        var (hub, fake) = Connected();
        using (hub)
        {
            var first = hub.RestartAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            fake.Writes.Clear();

            Assert.Equal(UmbraRestartResult.Busy, await hub.RestartAsync(TimeSpan.FromSeconds(5), CancellationToken.None));

            Assert.Empty(fake.Writes);
            Assert.True(hub.IsRestarting);
            hub.Detach();
            hub.Attach(new UmbraDeviceFake());
            Assert.True(hub.Connect());
            Assert.Equal(UmbraRestartResult.Reconnected, await first);
        }
    }

    [Fact]
    public async Task A_cancelled_restart_clears_the_restarting_state()
    {
        var (hub, _) = Connected();
        using (hub)
        {
            using var cts = new CancellationTokenSource();
            var restart = hub.RestartAsync(TimeSpan.FromSeconds(5), cts.Token);
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restart);

            Assert.False(hub.IsRestarting);
        }
    }

    [Fact]
    public void Connect_refuses_a_model_code_other_than_the_umbra_v2()
    {
        var fake = new UmbraDeviceFake { ModelCode = 10 };
        using var hub = new UmbraHub();
        hub.Attach(fake);

        Assert.False(hub.Connect());
        Assert.False(hub.IsConnected);
        Assert.Equal(0, hub.ConnectEpoch);
    }

    [Fact]
    public async Task RestartAsync_fails_without_a_connection()
    {
        using var hub = new UmbraHub();
        Assert.Equal(UmbraRestartResult.NoDevice, await hub.RestartAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None));
    }

    [Fact]
    public void Detach_clears_the_snapshot()
    {
        var (hub, _) = Connected();
        using (hub)
        {
            hub.Detach();

            Assert.False(hub.IsConnected);
            Assert.Equal(0, hub.Snapshot.TotalLeds());
        }
    }

    private sealed class SilentDevice : Nexus.Service.Peripherals.Hid.IHidDevice
    {
        public int VendorId => 0;
        public int ProductId => 0;
        public string Path => "silent";
        public string? Serial => null;
        public int UsagePage => 0;
        public int Usage => 0;
        public bool SetFeature(ReadOnlySpan<byte> report) => false;
        public bool GetFeature(Span<byte> buffer) => false;
        public bool GetInputReport(Span<byte> buffer) => false;
        public bool Write(ReadOnlySpan<byte> report) => true;
        public bool SetOutputReport(ReadOnlySpan<byte> report) => false;
        public int Read(Span<byte> buffer, int timeoutMs) => 0;
        public void Dispose() { }
    }
}
