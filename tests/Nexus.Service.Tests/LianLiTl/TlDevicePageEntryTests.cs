using Nexus.Service.Lighting;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.LianLiTl;

public class TlDevicePageEntryTests
{
    [Fact]
    public void A_connected_controller_is_listed_as_firmware_controlled()
    {
        var entry = CompositeLightingDeviceProvider.TlDevicePageEntry(true, new TlLightingSettings());

        Assert.NotNull(entry);
        Assert.Equal("lianli-tl", entry.HandlerId);
        Assert.Equal("firmware", entry.Reason);
    }

    [Fact]
    public void Argb_sync_lists_it_as_following_the_motherboard()
    {
        var entry = CompositeLightingDeviceProvider.TlDevicePageEntry(true, new TlLightingSettings { ArgbSync = true });

        Assert.Equal("argbInput", entry?.Reason);
    }

    [Fact]
    public void A_disconnected_controller_is_not_listed()
    {
        Assert.Null(CompositeLightingDeviceProvider.TlDevicePageEntry(false, new TlLightingSettings()));
    }
}
