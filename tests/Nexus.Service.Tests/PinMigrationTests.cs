using Nexus.Service.Lighting;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests;

public class PinMigrationTests
{
    private static StaticDeviceLook Look(string effect, string color = "", int slot = 0) =>
        new() { Effect = effect, Color = color, Slot = slot, Intensity = 0.7f, Hue = 0.2f, Params = new() { ["u_angle"] = 45f } };

    [Fact]
    public void Every_static_look_becomes_a_static_pin_whether_or_not_it_was_locked()
    {
        var lighting = new LightingSettings
        {
            StaticDeviceLooks = new()
            {
                ["a"] = Look("gradientlinear", slot: 2),
                ["b"] = Look("flat", "#ff0000"),
                ["empty"] = Look(""),
            },
        };

        Assert.True(PinMigration.Apply(lighting));

        Assert.Equal(2, lighting.DevicePins.Count);
        var a = lighting.DevicePins["a"];
        Assert.Equal(DevicePinKinds.Static, a.Kind);
        Assert.Equal("gradientlinear", a.Effect);
        Assert.Equal(2, a.Slot);
        Assert.Equal(0.7f, a.Intensity!.Value, 3);
        Assert.Equal(45f, a.Params!["u_angle"], 3);
        Assert.Null(a.Color);
        Assert.Equal("#ff0000", lighting.DevicePins["b"].Color);
        Assert.Null(lighting.StaticDeviceLooks);
    }

    [Fact]
    public void An_animation_lock_becomes_an_animate_pin_and_wins_over_a_static_look()
    {
        var lighting = new LightingSettings
        {
            StaticDeviceLooks = new() { ["both"] = Look("flat", "#00ff00"), ["only-static"] = Look("flat", "#0000ff") },
            AnimateDeviceLocks = new() { ["both"] = "plasma", ["only-animate"] = "fire" },
        };

        PinMigration.Apply(lighting);

        Assert.Equal(DevicePinKinds.Animate, lighting.DevicePins["both"].Kind);
        Assert.Equal("plasma", lighting.DevicePins["both"].Effect);
        Assert.Equal(DevicePinKinds.Static, lighting.DevicePins["only-static"].Kind);
        Assert.Equal("fire", lighting.DevicePins["only-animate"].Effect);
        Assert.Null(lighting.AnimateDeviceLocks);
        Assert.Null(lighting.StaticDeviceLooks);
    }

    [Fact]
    public void An_existing_pin_is_never_overwritten_and_a_second_pass_changes_nothing()
    {
        var lighting = new LightingSettings
        {
            StaticDeviceLooks = new() { ["a"] = Look("flat", "#00ff00") },
        };
        lighting.DevicePins["a"] = new DevicePin { Kind = DevicePinKinds.Mirror };

        PinMigration.Apply(lighting);
        Assert.Equal(DevicePinKinds.Mirror, lighting.DevicePins["a"].Kind);
        Assert.False(PinMigration.Apply(lighting));
    }

    [Fact]
    public void A_preset_is_migrated_the_same_way_and_a_preset_without_either_field_stays_untouched()
    {
        var lighting = new LightingSettings();
        lighting.LayoutPresets.Add(new LayoutPreset
        {
            Id = "old",
            StaticDeviceLooks = new() { ["a"] = Look("flat", "#123456") },
            AnimateDeviceLocks = new() { ["b"] = "plasma" },
        });
        lighting.LayoutPresets.Add(new LayoutPreset { Id = "ancient" });

        PinMigration.Apply(lighting);

        var old = lighting.LayoutPresets[0];
        Assert.Equal("#123456", old.DevicePins!["a"].Color);
        Assert.Equal(DevicePinKinds.Animate, old.DevicePins["b"].Kind);
        Assert.Null(old.StaticDeviceLooks);
        Assert.Null(old.AnimateDeviceLocks);
        // Null still means "leave the live pins alone" on activate.
        Assert.Null(lighting.LayoutPresets[1].DevicePins);
    }

    [Fact]
    public void Fan_lock_overrides_move_to_fan_pin_overrides()
    {
        var cooling = new CoolingSettings
        {
            FanLockOverrides = new() { ["fan1"] = true, ["fan2"] = false, ["fan3"] = true },
        };
        cooling.FanPinOverrides["fan3"] = false;

        Assert.True(PinMigration.Apply(cooling));

        Assert.True(cooling.FanPinOverrides["fan1"]);
        Assert.False(cooling.FanPinOverrides["fan2"]);
        // An entry already under the new name wins.
        Assert.False(cooling.FanPinOverrides["fan3"]);
        Assert.Null(cooling.FanLockOverrides);
        Assert.False(PinMigration.Apply(cooling));
    }

    [Fact]
    public void A_deserialized_pre_pin_document_migrates_lighting_and_cooling_together()
    {
        var json = """
            {"lighting":{"staticDeviceLooks":{"a":{"effect":"flat","color":"#010203","locked":false}},"animateDeviceLocks":{"b":"plasma"}},
             "cooling":{"fanLockOverrides":{"fan1":true}}}
            """;
        var doc = System.Text.Json.JsonSerializer.Deserialize(json, Nexus.Service.Serialization.PersistenceJsonContext.Default.NexusSettings)!;
        PinMigration.Apply(doc);
        Assert.Equal("#010203", doc.Lighting.DevicePins["a"].Color);
        Assert.Equal("plasma", doc.Lighting.DevicePins["b"].Effect);
        Assert.True(doc.Cooling.FanPinOverrides["fan1"]);
    }
}
