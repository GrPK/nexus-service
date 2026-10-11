using System.Collections.Generic;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.CorsairLink;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Tests.CorsairLink;

public class CorsairLinkLightingTests
{
    // ── colour-stream order ───────────────────────────────────────────────────

    [Fact]
    public void WireOrder_streams_the_lcd_cap_right_after_the_aio()
    {
        // TITAN RX LCD chain: cap on ch13, pump on ch14, plus a fan on either side.
        var fan = new CorsairLinkDevice { Channel = 1, Type = 1, Class = CorsairLinkClass.Fan, LedCount = 34 };
        var cap = new CorsairLinkDevice { Channel = 13, Type = 6, Class = CorsairLinkClass.Pump, LedCount = 24 };
        var pump = new CorsairLinkDevice { Channel = 14, Type = 17, Class = CorsairLinkClass.Aio, LedCount = 20 };
        var fan2 = new CorsairLinkDevice { Channel = 15, Type = 1, Class = CorsairLinkClass.Fan, LedCount = 34 };

        var ordered = CorsairLinkLightingFrameWriter.WireOrder(new[] { fan, cap, pump, fan2 }, new List<CorsairLinkDevice>());

        Assert.Equal(new[] { fan, pump, cap, fan2 }, ordered);
    }

    [Fact]
    public void WireOrder_keeps_channel_order_without_a_cap_and_aio_pair()
    {
        var cap = new CorsairLinkDevice { Channel = 13, Type = 6, Class = CorsairLinkClass.Pump, LedCount = 24 };
        var fan = new CorsairLinkDevice { Channel = 14, Type = 1, Class = CorsairLinkClass.Fan, LedCount = 34 };
        var devices = new[] { cap, fan };

        Assert.Same(devices, CorsairLinkLightingFrameWriter.WireOrder(devices, new List<CorsairLinkDevice>()));
    }

    // ── brightness-cap staging (port protection) ──────────────────────────────

    private static IReadOnlyList<CorsairLinkDevice> Port0Devices(int ledCount) =>
        new[] { new CorsairLinkDevice { Channel = 1, PortId = 0, LedCount = ledCount, Class = CorsairLinkClass.Fan } };

    [Theory]
    [InlineData(0, 1.0f)]
    [InlineData(238, 1.0f)]   // at stage-1 boundary, not over
    [InlineData(239, 0.66f)]  // just over stage-1
    [InlineData(340, 0.66f)]  // at stage-2 boundary, not over
    [InlineData(341, 0.33f)]  // just over stage-2
    [InlineData(442, 0.33f)]  // at stage-3 boundary, not over
    [InlineData(443, 0.10f)]  // just over stage-3
    [InlineData(1000, 0.10f)] // well over stage-3
    public void ComputePortCapFactor_stage_boundaries(int ledCount, float expected)
    {
        var factor = CorsairLinkLightingFrameWriter.ComputePortCapFactor(Port0Devices(ledCount));
        Assert.Equal(expected, factor, 2);
    }

    [Fact]
    public void ComputePortCapFactor_uses_most_restrictive_port()
    {
        // Port 0 has 239 LEDs (stage-1 = 0.66); port 1 has 0 LEDs (uncapped = 1.0).
        // The most restrictive cap wins.
        var devices = new[]
        {
            new CorsairLinkDevice { Channel = 1, PortId = 0, LedCount = 239, Class = CorsairLinkClass.Fan },
            new CorsairLinkDevice { Channel = 13, PortId = 1, LedCount = 0, Class = CorsairLinkClass.Fan },
        };
        Assert.Equal(0.66f, CorsairLinkLightingFrameWriter.ComputePortCapFactor(devices), 2);
    }

    [Fact]
    public void ComputePortCapFactor_both_ports_constrained_picks_worse()
    {
        // Port 0: stage-1 (0.66); port 1: stage-3 (0.10). Result = 0.10.
        var devices = new[]
        {
            new CorsairLinkDevice { Channel = 1, PortId = 0, LedCount = 239, Class = CorsairLinkClass.Fan },
            new CorsairLinkDevice { Channel = 13, PortId = 1, LedCount = 443, Class = CorsairLinkClass.Fan },
        };
        Assert.Equal(0.10f, CorsairLinkLightingFrameWriter.ComputePortCapFactor(devices), 2);
    }

    [Fact]
    public void ComputePortCapFactor_ignores_zero_led_devices()
    {
        var devices = new[]
        {
            new CorsairLinkDevice { Channel = 1, PortId = 0, LedCount = 0 },
            new CorsairLinkDevice { Channel = 2, PortId = 0, LedCount = 0 },
        };
        Assert.Equal(1.0f, CorsairLinkLightingFrameWriter.ComputePortCapFactor(devices), 2);
    }

    // ── AIO inner-ring blank ──────────────────────────────────────────────────

    [Fact]
    public void ApplyAioInnerRingBlank_zeroes_led_indices_16_to_19()
    {
        var buf = new RgbColor[20];
        for (var i = 0; i < buf.Length; i++)
        {
            buf[i] = new RgbColor(255, 128, 64);
        }

        CorsairLinkLightingFrameWriter.ApplyAioInnerRingBlank(buf);

        for (var i = 0; i < 16; i++)
        {
            Assert.Equal(new RgbColor(255, 128, 64), buf[i]);
        }
        for (var i = 16; i < 20; i++)
        {
            Assert.Equal(default(RgbColor), buf[i]);
        }
    }

    [Fact]
    public void ApplyAioInnerRingBlank_shorter_segment_does_not_throw()
    {
        var buf = new RgbColor[10];
        for (var i = 0; i < buf.Length; i++)
        {
            buf[i] = new RgbColor(1, 2, 3);
        }
        CorsairLinkLightingFrameWriter.ApplyAioInnerRingBlank(buf);
        for (var i = 0; i < buf.Length; i++)
        {
            Assert.Equal(new RgbColor(1, 2, 3), buf[i]);
        }
    }

    [Fact]
    public void ApplyAioInnerRingBlank_exactly_20_leds_zeroes_last_four()
    {
        var buf = new RgbColor[20];
        for (var i = 0; i < buf.Length; i++)
        {
            buf[i] = new RgbColor(10, 20, 30);
        }
        CorsairLinkLightingFrameWriter.ApplyAioInnerRingBlank(buf);
        Assert.Equal(new RgbColor(10, 20, 30), buf[15]);
        Assert.Equal(default(RgbColor), buf[16]);
        Assert.Equal(default(RgbColor), buf[19]);
    }
}
