using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Cooling;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.AsiahorseUmbra;
using Nexus.Service.Peripherals.Hid;
using Xunit;

namespace Nexus.Service.Tests.AsiahorseUmbra;

public class UmbraConnectionWorkerTests
{
    private readonly InMemoryConfigStore _store = new();
    private readonly UmbraHub _hub = new();
    private readonly UmbraDeviceFake _fake = new();
    private readonly DeviceControlGate _gate;
    private readonly UmbraConnectionWorker _worker;

    public UmbraConnectionWorkerTests()
    {
        _gate = new DeviceControlGate(_store);
        var lighting = new AsiahorseUmbraLightingDeviceProvider(_hub, _store);
        var cooling = new AsiahorseUmbraCoolingProvider(_hub, _store);
        _worker = new UmbraConnectionWorker(new FakeEnumerator(_fake), _hub, lighting, cooling, _gate,
            new HardwarePresence(new FakeUsb()), _store);
    }

    private void Connect()
    {
        _fake.Ports[9][0] = 6;
        _hub.Attach(_fake);
        Assert.True(_hub.Connect());
        _fake.Writes.Clear();
    }

    private static byte[] Cmds(IEnumerable<byte[]> frames) => frames.Select(f => f[4]).ToArray();

    [Fact]
    public void HandBack_replays_the_firmware_effect_then_returns_the_fans_to_the_pwm_header()
    {
        Connect();

        _worker.HandBack();

        Assert.Equal(new byte[] { 0xFD, 0x03, 0x08 }, Cmds(_fake.Writes));
        Assert.Equal(new byte[] { 0x08, 0x00 }, _fake.Writes[2][4..6]);
        Assert.False(_hub.IsConnected);
    }

    [Fact]
    public void HandBack_writes_nothing_while_both_pillars_are_off_and_never_were_on()
    {
        _store.Update(s => s.FeaturesOnboardingCompleted = false);
        var worker = new UmbraConnectionWorker(new FakeEnumerator(_fake), _hub,
            new AsiahorseUmbraLightingDeviceProvider(_hub, _store), new AsiahorseUmbraCoolingProvider(_hub, _store), _gate,
            new HardwarePresence(new FakeUsb()), _store, new Nexus.Service.Lifecycle.FeatureGates(_store));
        Connect();

        worker.HandBack();

        Assert.Empty(_fake.Writes);
        Assert.False(_hub.IsConnected);
    }

    private UmbraConnectionWorker GatedWorker(Nexus.Service.Lifecycle.FeatureGates gates) =>
        new(new FakeEnumerator(_fake), _hub, new AsiahorseUmbraLightingDeviceProvider(_hub, _store),
            new AsiahorseUmbraCoolingProvider(_hub, _store), _gate, new HardwarePresence(new FakeUsb()), _store, gates);

    private static void SetTouched(UmbraConnectionWorker worker, bool lighting, bool cooling)
    {
        var t = typeof(UmbraConnectionWorker);
        t.GetField("_lightingTouched", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(worker, lighting);
        t.GetField("_coolingTouched", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(worker, cooling);
    }

    [Fact]
    public void A_pillar_that_went_off_while_the_device_was_away_is_handed_back_on_reconnect()
    {
        var worker = GatedWorker(new Nexus.Service.Lifecycle.FeatureGates(_store));
        SetTouched(worker, lighting: false, cooling: true);
        _store.Update(s => s.Features.Cooling = false);
        Connect();

        worker.HandBackStalePillars();

        Assert.Equal(new byte[] { 0x08 }, Cmds(_fake.Writes));
        Assert.True(_hub.IsConnected);
    }

    [Fact]
    public void A_failed_stale_pillar_write_keeps_the_flags_so_the_next_connect_tries_again()
    {
        var worker = GatedWorker(new Nexus.Service.Lifecycle.FeatureGates(_store));
        SetTouched(worker, lighting: false, cooling: true);
        _store.Update(s => s.Features.Cooling = false);
        Connect();
        _fake.FailWrites = true;

        worker.HandBackStalePillars();
        _fake.FailWrites = false;
        worker.HandBackStalePillars();

        Assert.Equal(new byte[] { 0x08 }, Cmds(_fake.Writes));
    }

    [Fact]
    public void A_failed_hand_back_write_keeps_the_flags_so_the_next_reconnect_retries()
    {
        var worker = GatedWorker(new Nexus.Service.Lifecycle.FeatureGates(_store));
        SetTouched(worker, lighting: false, cooling: true);
        _store.Update(s => s.Features.Cooling = false);
        Connect();
        _fake.FailWrites = true;

        Assert.False(worker.HandBack());

        _fake.FailWrites = false;
        Connect();
        worker.HandBackStalePillars();
        Assert.Equal(new byte[] { 0x08 }, Cmds(_fake.Writes));
    }

    [Fact]
    public void A_normal_hand_back_clears_the_flags_so_the_next_reconnect_writes_nothing()
    {
        var worker = GatedWorker(new Nexus.Service.Lifecycle.FeatureGates(_store));
        SetTouched(worker, lighting: false, cooling: true);
        _store.Update(s => s.Features.Cooling = false);
        Connect();
        worker.HandBack();

        Connect();
        worker.HandBackStalePillars();

        Assert.Empty(_fake.Writes);
    }

    [Fact]
    public void Nothing_is_handed_back_on_reconnect_for_a_pillar_that_is_still_on_or_was_never_driven()
    {
        var worker = GatedWorker(new Nexus.Service.Lifecycle.FeatureGates(_store));
        SetTouched(worker, lighting: false, cooling: true);
        Connect();
        worker.HandBackStalePillars();
        Assert.Empty(_fake.Writes);

        _store.Update(s => s.Features.Cooling = false);
        SetTouched(worker, lighting: false, cooling: false);
        worker.HandBackStalePillars();
        Assert.Empty(_fake.Writes);
    }

    [Fact]
    public void HandBack_skips_the_fan_frame_when_fans_already_follow_the_motherboard()
    {
        _store.Update(s => s.Devices.AsiahorseUmbra.FanSyncMb = true);
        Connect();

        _worker.HandBack();

        Assert.Equal(new byte[] { 0xFD, 0x03 }, Cmds(_fake.Writes));
    }

    [Fact]
    public void HandBack_from_the_argb_input_source_hands_over_authority_and_the_fans()
    {
        _store.Update(s => s.Devices.AsiahorseUmbra.Source = "argbInput");
        Connect();

        _worker.HandBack();

        Assert.Equal(new byte[] { 0xFD, 0x08 }, Cmds(_fake.Writes));
        Assert.Equal(0x00, _fake.Writes[0][5]);
    }

    [Fact]
    public void Nothing_is_written_after_the_release()
    {
        Connect();
        _worker.HandBack();
        var count = _fake.Writes.Count;

        Assert.False(_hub.SendFrame(UmbraProtocol.EncodeInfoRequest()));
        Assert.False(_hub.SendFrames(UmbraProtocol.EncodeStream(new byte[18])));

        Assert.Equal(count, _fake.Writes.Count);
    }

    [Fact]
    public async Task Turning_nexus_control_on_connects_without_waiting_for_the_retry_interval()
    {
        _gate.SetEnabled(UmbraConnectionWorker.HandlerId, false);
        Assert.False(_gate.IsEnabled(UmbraConnectionWorker.HandlerId));
        await _worker.StartAsync(CancellationToken.None);
        try
        {
            // The worker is parked in its retry wait; the control change must wake it.
            _gate.SetEnabled(UmbraConnectionWorker.HandlerId, true);

            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (!_hub.IsConnected && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }

            Assert.True(_hub.IsConnected);
        }
        finally
        {
            await _worker.StopAsync(CancellationToken.None);
            _hub.Dispose();
        }
    }

    private sealed class FakeEnumerator : IHidEnumerator
    {
        private readonly UmbraDeviceFake _device;

        public FakeEnumerator(UmbraDeviceFake device) { _device = device; }

        public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId) => new[]
        {
            new HidDeviceInfo { VendorId = vendorId, ProductId = productId, Path = "fake", InputReportByteLength = 65, OutputReportByteLength = 65 },
        };

        public IReadOnlyList<HidDeviceInfo> FindAll() => Find(UmbraProtocol.VendorId, UmbraProtocol.ProductId);

        public IHidDevice? Open(string path, bool forInput = false) => _device;
    }

    private sealed class FakeUsb : IUsbEnumerator
    {
        public List<UsbDeviceEntry> Enumerate() => new()
        {
            new UsbDeviceEntry { VendorId = UmbraProtocol.VendorId, ProductId = UmbraProtocol.ProductId },
        };
    }
}
