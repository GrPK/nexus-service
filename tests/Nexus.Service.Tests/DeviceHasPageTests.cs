using System.Linq;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Devices.Handlers;
using Nexus.Service.Persistence;
using Nexus.Service.Plugins;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>Pins the handlers that opt out of having a page, the panel-backed ones that keep it while disconnected, the default the rest inherit, and the hop onto the wire.</summary>
public class DeviceHasPageTests
{
    [Fact]
    public void MiniHub_HasNoPage()
    {
        Assert.False(((IDeviceHandler)TestHandlers.FanHub()).HasPage);
    }

    [Fact]
    public void Aw5_HasNoPage()
    {
        Assert.False(((IDeviceHandler)new Aw5Handler()).HasPage);
    }

    [Fact]
    public void BulkPanelCooler_HasAPage_EvenBeforeItsPanelConnects()
    {
        using var hub = new Nexus.Service.Peripherals.BulkPanels.BulkPanelHub(
            new Nexus.Service.Peripherals.BulkPanels.HydroShift2LcdDriver());
        Assert.True(((IDeviceHandler)new BulkPanelHandler(hub)).HasPage);
    }

    [Fact]
    public void JpegPanelCooler_HasAPage_EvenBeforeItsPanelConnects()
    {
        using var hub = new Nexus.Service.Peripherals.JpegPanels.JpegPanelHub(
            Nexus.Service.Peripherals.JpegPanels.JpegPanelModel.HydroShiftLcd);
        Assert.True(((IDeviceHandler)new JpegPanelHandler(hub)).HasPage);
    }

    [Fact]
    public void AHandlerThatStaysSilent_InheritsTheDefault()
    {
        Assert.True(((IDeviceHandler)TestHandlers.Cnvs()).HasPage);
    }

    [Fact]
    public void GetAll_CarriesEachHandlersAnswerOntoTheWire()
    {
        var manager = new DeviceManager(
            new IDeviceHandler[] { TestHandlers.Cnvs(), TestHandlers.FanHub(), new Aw5Handler() },
            new StubUsbEnumerator(),
            new PluginProviderRegistry(),
            new DeviceControlGate(new InMemoryConfigStore()));

        var items = manager.GetAll();

        Assert.True(items.Single(i => i.Id == "cnvs").HasPage);
        Assert.False(items.Single(i => i.Id == "fan-hub").HasPage);
        Assert.False(items.Single(i => i.Id == "aw5").HasPage);
    }
}
