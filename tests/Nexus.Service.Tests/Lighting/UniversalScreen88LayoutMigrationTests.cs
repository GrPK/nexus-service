using System.Collections.Generic;
using Nexus.Service.Lighting;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Lighting;

/// <summary>The OpenRGB "Lian Li Universal Screen" card's Lighting page state moves to the native 8.8 ring once.</summary>
public class UniversalScreen88LayoutMigrationTests
{
    private const string Old = UniversalScreen88LayoutMigration.OpenRgbId;
    private const string Ring = UniversalScreen88LedLightingDeviceProvider.RingZoneId;
    private const string Device = UniversalScreen88LedLightingDeviceProvider.HandlerId;

    private static NexusSettings WithOpenRgbCard()
    {
        var s = new NexusSettings();
        s.Lighting.DeviceLayouts[Old] = new DeviceLayout { X = 10, Y = 20, W = 300, H = 75, Rotation = 90 };
        s.Lighting.LayoutPresets.Add(new LayoutPreset
        {
            Id = "desk",
            Layouts = { [Old] = new DeviceLayout { X = 1, Y = 2, W = 3, H = 4 } },
            DisabledDevices = new List<string> { Old },
        });
        s.Devices.LightingDevicePrefs[Old] = new LightingDevicePreference { Brightness = 40 };
        s.Devices.DisabledLightingDevices = new List<string> { "other", Old };
        s.Devices.DeviceAspectRatios[Old] = 4f;
        return s;
    }

    [Fact]
    public void The_OpenRGB_card_id_is_its_location_stable_id()
    {
        // OpenRGB's LianLiUniversalScreenController location is "USB: 0416:8050"; the board has no USB serial.
        Assert.Equal("openrgb-l-USB__0416_8050", Old);
    }

    [Fact]
    public void Placement_presets_preferences_power_and_aspect_move_to_the_native_ring()
    {
        var s = WithOpenRgbCard();
        Assert.True(UniversalScreen88LayoutMigration.NeedsApply(s));
        Assert.True(UniversalScreen88LayoutMigration.Apply(s));

        Assert.False(s.Lighting.DeviceLayouts.ContainsKey(Old));
        Assert.Equal(300, s.Lighting.DeviceLayouts[Ring].W);
        Assert.Equal(90, s.Lighting.DeviceLayouts[Ring].Rotation);
        Assert.Equal(3, s.Lighting.LayoutPresets[0].Layouts[Ring].W);
        Assert.Equal(new[] { Ring }, s.Lighting.LayoutPresets[0].DisabledDevices);
        Assert.Equal(40, s.Devices.LightingDevicePrefs[Ring].Brightness);
        Assert.Equal(new[] { "other", Ring }, s.Devices.DisabledLightingDevices);
        Assert.Equal(4f, s.Devices.DeviceAspectRatios[Device]);
        Assert.False(UniversalScreen88LayoutMigration.NeedsApply(s));
    }

    [Fact]
    public void A_native_placement_already_set_wins_and_the_move_runs_once()
    {
        var s = WithOpenRgbCard();
        s.Lighting.DeviceLayouts[Ring] = new DeviceLayout { X = 500, Y = 600, W = 200, H = 50 };
        UniversalScreen88LayoutMigration.Apply(s);
        Assert.Equal(500, s.Lighting.DeviceLayouts[Ring].X);

        s.Lighting.DeviceLayouts[Old] = new DeviceLayout { X = 7 };
        Assert.False(UniversalScreen88LayoutMigration.Apply(s));
        Assert.True(s.Lighting.DeviceLayouts.ContainsKey(Old));
    }
}
