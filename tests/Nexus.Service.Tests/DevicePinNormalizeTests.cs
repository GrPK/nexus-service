using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests;

public class DevicePinNormalizeTests
{
    [Fact]
    public void An_animate_pin_keeps_only_its_effect()
    {
        var pin = new DevicePin
        {
            Kind = DevicePinKinds.Animate, Effect = "plasma", Color = "#112233", Hue = 0.4f, Slot = 2,
            Params = new() { ["u_angle"] = 1f },
        };

        var n = DevicePinTracker.Normalize(pin);

        Assert.Equal("plasma", n.Effect);
        Assert.Null(n.Color);
        Assert.Null(n.Hue);
        Assert.Null(n.Slot);
        Assert.Null(n.Params);
    }

    [Theory]
    [InlineData(DevicePinKinds.Mirror)]
    [InlineData(DevicePinKinds.Media)]
    public void Mirror_and_media_pins_keep_no_fields(string kind)
    {
        var n = DevicePinTracker.Normalize(new DevicePin { Kind = kind, Effect = "x", Hue = 1f });
        Assert.Equal(kind, n.Kind);
        Assert.Null(n.Effect);
        Assert.Null(n.Hue);
    }

    [Fact]
    public void A_static_pin_has_its_numeric_fields_clamped_to_the_shader_ranges()
    {
        var specs = ShaderLibrary.Params("gradientlinear");
        var pin = new DevicePin
        {
            Kind = DevicePinKinds.Static, Effect = "gradientlinear",
            Hue = 1000f, Saturation = -1000f, Colorize = 1000f, Contrast = 1000f, Intensity = 1000f, Slot = 3,
        };

        var n = DevicePinTracker.Normalize(pin);

        Assert.Equal(specs["u_hue"].Max, n.Hue);
        Assert.Equal(specs["u_saturation"].Min, n.Saturation);
        Assert.Equal(specs["u_colorize"].Max, n.Colorize);
        Assert.Equal(specs["u_contrast"].Max, n.Contrast);
        // A field the shader declares no range for passes through.
        Assert.Equal(specs.TryGetValue("u_intensity", out var intensity) ? intensity.Max : 1000f, n.Intensity);
        Assert.Equal(3, n.Slot);
    }

    [Fact]
    public void SetMany_stores_the_normalized_pin()
    {
        var pins = new DevicePinTracker();
        pins.Set("a", new DevicePin { Kind = DevicePinKinds.Animate, Effect = "plasma", Hue = 0.5f });
        Assert.Null(pins.Snapshot()["a"].Hue);
    }
}
