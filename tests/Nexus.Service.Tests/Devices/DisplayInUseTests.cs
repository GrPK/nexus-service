using System.Collections.Generic;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Devices.Handlers;
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Peripherals.Nzxt;
using Nexus.Service.Plugins;
using Xunit;

namespace Nexus.Service.Tests.Devices;

public class DisplayInUseTests
{
    private sealed class NoLcdFactory : IKrakenLcdTransportFactory
    {
        public IKrakenLcdTransport? Open(string? serial) => null;
    }

    private sealed class Bus : IUsbEnumerator
    {
        public List<UsbDeviceEntry> Entries { get; } = new();
        public List<UsbDeviceEntry> Enumerate() => new(Entries);
    }

    private static UsbDeviceEntry Kraken(int pid) => new() { VendorId = KrakenProtocol.VendorId, ProductId = pid };

    [Fact]
    public void KrakenWithAnLcd_DrivesADisplayFromUsbPresenceAlone()
    {
        var handler = new KrakenHandler(new KrakenHub(new NoLcdFactory()));

        Assert.True(handler.DrivesDisplay(new[] { Kraken(0x3008) }));
    }

    [Fact]
    public void KrakenWithoutAnLcd_DrivesNoDisplay()
    {
        var handler = new KrakenHandler(new KrakenHub(new NoLcdFactory()));

        Assert.False(handler.DrivesDisplay(new[] { Kraken(0x2007) }));
        Assert.False(handler.DrivesDisplay(new List<UsbDeviceEntry>()));
    }

    [Fact]
    public void IsDisplayInUse_NeedsNexusControlOn_APresentDevice_AndADisplay()
    {
        var store = new InMemoryConfigStore();
        var gate = new DeviceControlGate(store);
        var bus = new Bus();
        var manager = new DeviceManager(
            new IDeviceHandler[] { new KrakenHandler(new KrakenHub(new NoLcdFactory())) }, bus, new PluginProviderRegistry(), gate);
        gate.SetEnabled("nzxt-kraken", true);

        Assert.False(manager.IsDisplayInUse("nzxt-kraken"));
        Assert.False(manager.IsHandlerInUse("nzxt-kraken"));

        bus.Entries.Add(Kraken(0x2007));
        Assert.True(manager.IsHandlerInUse("nzxt-kraken"));
        Assert.False(manager.IsDisplayInUse("nzxt-kraken"));

        bus.Entries.Clear();
        bus.Entries.Add(Kraken(0x3008));
        Assert.True(manager.IsDisplayInUse("nzxt-kraken"));

        gate.SetEnabled("nzxt-kraken", false);
        Assert.False(manager.IsDisplayInUse("nzxt-kraken"));
        Assert.False(manager.IsHandlerInUse("nzxt-kraken"));
    }

    [Fact]
    public void AnUnknownHandler_IsNeverInUse()
    {
        var manager = new DeviceManager(new IDeviceHandler[0], new Bus(), new PluginProviderRegistry(), new DeviceControlGate(new InMemoryConfigStore()));

        Assert.False(manager.IsDisplayInUse("nope"));
    }

    [Fact]
    public void WirelessScreens_AreADisplayWithoutTheRfDongle_AndOnlyWhenScreensAreBound()
    {
        var hub = new Slv3LcdHub(new NoDiscovery(), _ => throw new System.InvalidOperationException("no transport"));
        var handler = new LianLiWirelessHandler(new Slv3Hub(new Nexus.Service.Tests.LianLiWireless.Slv3TestHub.FakeDiscovery(), _ => throw new System.InvalidOperationException()), hub);
        var noUsb = new List<UsbDeviceEntry>();

        Assert.False(handler.DrivesDisplay(noUsb));

        hub.UpdateScreens(new[] { new Slv3LcdScreenInfo { Serial = "S1", Position = 1 } });

        Assert.True(handler.DrivesDisplay(noUsb));
    }

    private sealed class NoDiscovery : ISlv3LcdDiscovery
    {
        public IReadOnlyList<Slv3LcdPortInfo> Discover() => new List<Slv3LcdPortInfo>();
    }
}
