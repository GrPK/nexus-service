using System.Collections.Generic;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.LianLiTl;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.LianLiTl;

public class TlLightingCardTests
{
    private static TlHubSnapshot Fans(params int[] fanCountsPerPort)
    {
        var ports = new List<int>();
        var indexes = new List<int>();
        var byAddress = new Dictionary<int, int>();
        for (var p = 0; p < fanCountsPerPort.Length; p++)
        {
            for (var f = 0; f < fanCountsPerPort[p]; f++)
            {
                byAddress[TlFanProtocol.Address(p, f)] = ports.Count;
                ports.Add(p);
                indexes.Add(f);
            }
        }
        return new TlHubSnapshot(ports.ToArray(), indexes.ToArray(), byAddress);
    }

    [Fact]
    public void Connected_hub_gets_a_firmware_card_sized_by_fan_count()
    {
        var card = CompositeLightingDeviceProvider.BuildTlCard(true, Fans(3, 2), new NexusSettings());

        Assert.NotNull(card);
        Assert.Equal("lianli-tl", card!.Id);
        Assert.Equal("lianli-tl", card.DeviceId);
        Assert.Equal("lianli-tl", card.ParentDeviceId);
        Assert.Equal("lianli-tl", card.ControlHandlerId);
        Assert.Equal("fan", card.IconType);
        Assert.Equal(5, card.LedCount);
        Assert.Equal("firmware", card.DevicePageReason);
    }

    [Fact]
    public void Argb_sync_marks_the_card_as_argb_input()
    {
        var settings = new NexusSettings();
        settings.Devices.TlLighting.ArgbSync = true;

        var card = CompositeLightingDeviceProvider.BuildTlCard(true, Fans(1), settings);

        Assert.Equal("argbInput", card!.DevicePageReason);
    }

    [Fact]
    public void An_empty_snapshot_still_reports_a_positive_led_count()
    {
        var card = CompositeLightingDeviceProvider.BuildTlCard(true, TlHubSnapshot.Empty, new NexusSettings());

        Assert.Equal(1, card!.LedCount);
    }

    [Fact]
    public void Saved_layout_positions_the_card()
    {
        var settings = new NexusSettings();
        settings.Lighting.DeviceLayouts["lianli-tl"] = new DeviceLayout { X = 10, Y = 20, W = 30, H = 40, Rotation = 90 };

        var card = CompositeLightingDeviceProvider.BuildTlCard(true, Fans(1), settings)!;

        Assert.Equal((10f, 20f, 30f, 40f, 90), (card.CanvasX, card.CanvasY, card.CanvasW, card.CanvasH, card.CanvasRotation));
    }

    [Fact]
    public void Disconnected_hub_has_no_card()
    {
        Assert.Null(CompositeLightingDeviceProvider.BuildTlCard(false, Fans(2), new NexusSettings()));
    }
}
