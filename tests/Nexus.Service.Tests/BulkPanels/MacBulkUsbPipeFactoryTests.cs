#if MACOS
using Nexus.Service.Peripherals.BulkPanels;
using Xunit;

namespace Nexus.Service.Tests.BulkPanels;

public class MacBulkUsbPipeFactoryTests
{
    [MacOnlyFact]
    public void Framework_loads_and_registers_its_classes()
    {
        Assert.True(MacUsbHost.EnsureLoaded());
        Assert.NotEqual(System.IntPtr.Zero, MacUsbHost.DeviceClass);
        Assert.NotEqual(System.IntPtr.Zero, MacUsbHost.InterfaceClass);
    }

    // No vendor is assigned 0xFFFF, so the IOKit match runs end to end and finds nothing.
    [MacOnlyFact]
    public void Open_returns_null_when_no_device_matches()
    {
        Assert.Null(new MacBulkUsbPipeFactory().Open(0xFFFF, 0xFFFF, 0x01, 0x81));
        Assert.Empty(MacUsbHost.FindDevices(0xFFFF, 0xFFFF));
    }
}
#endif
