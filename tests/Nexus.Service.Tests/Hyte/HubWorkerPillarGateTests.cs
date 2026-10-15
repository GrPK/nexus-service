using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Cooling;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Lifecycle;
using Nexus.Service.Peripherals.Hyte.MiniHub;
using Nexus.Service.Peripherals.Hyte.Np50;
using Nexus.Service.Peripherals.Hyte.SmartHub;
using Xunit;

namespace Nexus.Service.Tests.Hyte;

public class HubWorkerPillarGateTests
{
    private const byte SmartHubControl = 0xCC;
    private const byte MiniHubControl = 0xDD;

    private sealed class RecordingTransport : INp50Transport
    {
        public List<byte[]> Writes { get; } = new();
        public bool IsOpen => true;
        public string Serial => "GATETEST";
        public void DiscardInput() { }
        public void Write(ReadOnlySpan<byte> data) => Writes.Add(data.ToArray());
        public int Read(Span<byte> buffer, int timeoutMs) => 0;
        public void Dispose() { }
    }

    private sealed class OnePort : INp50PortDiscovery
    {
        public IReadOnlyList<Np50PortInfo> Discover() => new[] { new Np50PortInfo { PortName = "COM_TEST", Serial = "GATETEST" } };
    }

    // Present, so a worker whose polls fail (the fake never answers) and drops the hub reconnects on the next tick.
    private sealed class HubsOnTheBus : IUsbEnumerator
    {
        public List<UsbDeviceEntry> Enumerate() => new()
        {
            new UsbDeviceEntry { VendorId = SmartHubProtocol.VendorId, ProductId = SmartHubProtocol.ProductId },
            new UsbDeviceEntry { VendorId = MiniHubProtocol.VendorId, ProductId = MiniHubProtocol.ProductId },
        };
    }

    private static InMemoryConfigStore Store(bool decided, bool lighting = true, bool cooling = true)
    {
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.FeaturesOnboardingCompleted = decided;
            s.Features.Lighting = lighting;
            s.Features.Cooling = cooling;
        });
        return store;
    }

    private static (MiniHubHeartbeatWorker Worker, RecordingTransport Transport) MiniHub(InMemoryConfigStore store)
    {
        var transport = new RecordingTransport();
        var hub = new MiniHubHub(new OnePort(), _ => transport);
        Assert.True(hub.EnsureConnected());
        transport.Writes.Clear();
        var worker = new MiniHubHeartbeatWorker(hub, new HardwarePresence(new HubsOnTheBus()), new DeviceControlGate(store), new FeatureGates(store));
        return (worker, transport);
    }

    // Set RGB control mode is sub 0x03, set fan control mode sub 0x05.
    private static bool Sent(RecordingTransport t, byte sub) =>
        t.Writes.Any(w => w.Length >= 3 && w[1] == MiniHubControl && w[2] == sub);

    [Fact]
    public void MiniHub_Undecided_AssertsNeitherControlMode()
    {
        var (worker, transport) = MiniHub(Store(decided: false));

        worker.Tick();

        Assert.NotEmpty(transport.Writes);
        Assert.False(Sent(transport, 0x03));
        Assert.False(Sent(transport, 0x05));
    }

    [Fact]
    public void MiniHub_Decided_AssertsBothControlModes()
    {
        var (worker, transport) = MiniHub(Store(decided: true));

        worker.Tick();

        Assert.True(Sent(transport, 0x03));
        Assert.True(Sent(transport, 0x05));
    }

    [Fact]
    public void MiniHub_OnlyCoolingOff_StillAssertsTheRgbModeOnly()
    {
        var (worker, transport) = MiniHub(Store(decided: true, cooling: false));

        worker.Tick();

        Assert.True(Sent(transport, 0x03));
        Assert.False(Sent(transport, 0x05));
    }

    [Fact]
    public void MiniHub_CoolingSwitchedOnLater_AssertsTheFanModeOnTheNextTick()
    {
        var store = Store(decided: true, cooling: false);
        var (worker, transport) = MiniHub(store);
        worker.Tick();
        Assert.False(Sent(transport, 0x05));

        store.Update(s => s.Features.Cooling = true);
        worker.Tick();

        Assert.True(Sent(transport, 0x05));
    }

    private static (SmartHubHeartbeatWorker Worker, RecordingTransport Transport) SmartHub(InMemoryConfigStore store)
    {
        var transport = new RecordingTransport();
        var hub = new SmartHubHub(new OnePort(), _ => transport);
        Assert.True(hub.EnsureConnected());
        var worker = new SmartHubHeartbeatWorker(
            hub, new HardwarePresence(new HubsOnTheBus()), store, new SmartHubCoolingProvider(hub), new DeviceControlGate(store), new FeatureGates(store));
        return (worker, transport);
    }

    // Set fan speed is sub 0x02.
    private static bool FanWrite(RecordingTransport t) =>
        t.Writes.Any(w => w.Length >= 3 && w[1] == SmartHubControl && w[2] == 0x02);

    [Fact]
    public void SmartHub_Undecided_WritesNoFanDuty()
    {
        var (worker, transport) = SmartHub(Store(decided: false));

        worker.Tick();
        worker.Tick();

        Assert.NotEmpty(transport.Writes);
        Assert.False(FanWrite(transport));
    }

    [Fact]
    public void SmartHub_CoolingOff_WritesNoFanDuty()
    {
        var (worker, transport) = SmartHub(Store(decided: true, cooling: false));

        worker.Tick();
        worker.Tick();

        Assert.False(FanWrite(transport));
    }

    [Fact]
    public void SmartHub_Decided_AssertsInitialDuty_AndCoolingOffThenOnReassertsIt()
    {
        var store = Store(decided: true);
        var (worker, transport) = SmartHub(store);
        worker.Tick();
        Assert.True(FanWrite(transport));

        store.Update(s => s.Features.Cooling = false);
        transport.Writes.Clear();
        worker.Tick();
        Assert.False(FanWrite(transport));

        store.Update(s => s.Features.Cooling = true);
        worker.Tick();
        Assert.True(FanWrite(transport));
    }
}
