using System.Linq;
using Nexus.Service.Peripherals.AsiahorseUmbra;
using Nexus.Service.Persistence;
using Nexus.Service.Routes;
using Xunit;

namespace Nexus.Service.Tests.AsiahorseUmbra;

public class UmbraRouteLogicTests
{
    // ── Validation ──

    [Fact]
    public void An_empty_lighting_body_is_valid()
    {
        Assert.Null(AsiahorseUmbraRouteLogic.ValidateLighting(new UmbraLightingRequest()));
    }

    [Theory]
    [InlineData("nexus")]
    [InlineData("firmware")]
    [InlineData("argbInput")]
    public void Known_sources_are_accepted(string source)
    {
        Assert.Null(AsiahorseUmbraRouteLogic.ValidateLighting(new UmbraLightingRequest { Source = source }));
    }

    [Theory]
    [InlineData("Nexus")]
    [InlineData("")]
    [InlineData("rgb")]
    public void Unknown_sources_are_rejected(string source)
    {
        Assert.NotNull(AsiahorseUmbraRouteLogic.ValidateLighting(new UmbraLightingRequest { Source = source }));
    }

    [Theory]
    [InlineData("sync", true)]
    [InlineData("async", true)]
    [InlineData("merge", true)]
    [InlineData("spiral", false)]
    public void Group_must_be_a_known_group(string group, bool valid)
    {
        var error = AsiahorseUmbraRouteLogic.ValidateLighting(new UmbraLightingRequest { Group = group });

        Assert.Equal(valid, error is null);
    }

    [Fact]
    public void An_unknown_effect_is_rejected()
    {
        var body = new UmbraLightingRequest { Sync = new UmbraEffectPatch { Effect = "sparkle" } };

        Assert.NotNull(AsiahorseUmbraRouteLogic.ValidateLighting(body));
    }

    [Theory]
    [InlineData("rainbow", true)]
    [InlineData("runway", true)]
    [InlineData("address", true)]
    [InlineData("static", false)]
    [InlineData("breath", false)]
    [InlineData("disco", false)]
    [InlineData("gradientRainbow", false)]
    public void Merge_only_accepts_the_merge_effects(string effect, bool valid)
    {
        var error = AsiahorseUmbraRouteLogic.ValidateLighting(new UmbraLightingRequest { Merge = new UmbraEffectPatch { Effect = effect } });

        Assert.Equal(valid, error is null);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(5, true)]
    [InlineData(6, false)]
    public void Speed_is_zero_to_five(int speed, bool valid)
    {
        var error = AsiahorseUmbraRouteLogic.ValidateLighting(new UmbraLightingRequest { Sync = new UmbraEffectPatch { Speed = speed } });

        Assert.Equal(valid, error is null);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(5, true)]
    [InlineData(6, false)]
    public void Brightness_is_one_to_five(int brightness, bool valid)
    {
        var error = AsiahorseUmbraRouteLogic.ValidateLighting(new UmbraLightingRequest { Sync = new UmbraEffectPatch { Brightness = brightness } });

        Assert.Equal(valid, error is null);
    }

    [Theory]
    [InlineData("forward", true)]
    [InlineData("reverse", true)]
    [InlineData("up", false)]
    public void Direction_is_forward_or_reverse(string direction, bool valid)
    {
        var error = AsiahorseUmbraRouteLogic.ValidateLighting(new UmbraLightingRequest { Sync = new UmbraEffectPatch { Direction = direction } });

        Assert.Equal(valid, error is null);
    }

    [Theory]
    [InlineData("#ff0000", true)]
    [InlineData("#FF00aa", true)]
    [InlineData("ff0000", false)]
    [InlineData("#ff00", false)]
    [InlineData("#gg0000", false)]
    public void Colors_must_be_six_digit_hex(string color, bool valid)
    {
        var error = AsiahorseUmbraRouteLogic.ValidateLighting(new UmbraLightingRequest { Sync = new UmbraEffectPatch { Colors = new[] { color } } });

        Assert.Equal(valid, error is null);
    }

    [Fact]
    public void More_than_four_colors_are_rejected()
    {
        var colors = Enumerable.Repeat("#ffffff", 5).ToArray();

        Assert.NotNull(AsiahorseUmbraRouteLogic.ValidateLighting(new UmbraLightingRequest { Sync = new UmbraEffectPatch { Colors = colors } }));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(9, true)]
    [InlineData(10, false)]
    public void Async_needs_a_port_from_zero_to_nine(int? port, bool valid)
    {
        var error = AsiahorseUmbraRouteLogic.ValidateLighting(new UmbraLightingRequest { Async = new UmbraAsyncPatch { Port = port } });

        Assert.Equal(valid, error is null);
    }

    [Fact]
    public void Async_accepts_the_sync_effects_that_merge_does_not()
    {
        var body = new UmbraLightingRequest { Async = new UmbraAsyncPatch { Port = 3, Effect = "breath" } };

        Assert.Null(AsiahorseUmbraRouteLogic.ValidateLighting(body));
    }

    [Theory]
    [InlineData(-1, null, null, null, false)]
    [InlineData(10, null, null, null, false)]
    [InlineData(0, 0, "forward", 1, true)]
    [InlineData(9, 120, "reverse", 10, true)]
    [InlineData(2, 121, null, null, false)]
    [InlineData(2, -1, null, null, false)]
    [InlineData(2, null, "sideways", null, false)]
    [InlineData(2, null, null, 0, false)]
    [InlineData(2, null, null, 11, false)]
    public void Port_requests_are_range_checked(int index, int? ledCount, string? direction, int? order, bool valid)
    {
        var error = AsiahorseUmbraRouteLogic.ValidatePort(index, new UmbraPortRequest { LedCount = ledCount, Direction = direction, Order = order });

        Assert.Equal(valid, error is null);
    }

    [Fact]
    public void Port_writes_use_zero_based_order_and_numeric_direction()
    {
        var writes = AsiahorseUmbraRouteLogic.PortWrites(new UmbraPortRequest { LedCount = 24, Direction = "reverse", Order = 4 }).ToArray();

        Assert.Equal(new[] { (UmbraPortField.LedCount, 24), (UmbraPortField.Direction, 0), (UmbraPortField.Order, 3) }, writes);
    }

    // ── Applying a patch ──

    [Fact]
    public void Selecting_an_effect_restores_its_stored_values()
    {
        var current = new AsiahorseUmbraSettings();
        current.Sync.Effect = "rainbow";
        current.Sync.Values["runway"] = new UmbraEffectValues { Speed = 5, Brightness = 2, Direction = "reverse", Colors = { "#010203", "#000000", "#000000", "#000000" } };

        var next = AsiahorseUmbraRouteLogic.ApplyLighting(current, new UmbraLightingRequest { Sync = new UmbraEffectPatch { Effect = "runway" } });

        Assert.Equal("runway", next.Sync.Effect);
        Assert.Equal(5, next.Sync.Values["runway"].Speed);
        Assert.Equal("reverse", next.Sync.Values["runway"].Direction);
        Assert.Equal("#010203", next.Sync.Values["runway"].Colors[0]);
    }

    [Fact]
    public void Editing_values_keeps_the_other_effects_stored_values()
    {
        var current = new AsiahorseUmbraSettings();
        current.Sync.Values["runway"] = new UmbraEffectValues { Speed = 5, Colors = { "#010203", "#000000", "#000000", "#000000" } };

        var next = AsiahorseUmbraRouteLogic.ApplyLighting(current, new UmbraLightingRequest { Sync = new UmbraEffectPatch { Speed = 1 } });

        Assert.Equal(1, next.Sync.Values["rainbow"].Speed);
        Assert.Equal(5, next.Sync.Values["runway"].Speed);
    }

    [Fact]
    public void The_applied_patch_never_mutates_the_live_settings()
    {
        var current = new AsiahorseUmbraSettings();

        var next = AsiahorseUmbraRouteLogic.ApplyLighting(current, new UmbraLightingRequest
        {
            Source = "firmware",
            Sync = new UmbraEffectPatch { Speed = 1 },
            Async = new UmbraAsyncPatch { Port = 2, Speed = 4 },
        });

        Assert.Equal("nexus", current.Source);
        Assert.Empty(current.Sync.Values);
        Assert.Empty(current.Async);
        Assert.Equal("firmware", next.Source);
        Assert.Equal(4, next.Async["2"].Values["rainbow"].Speed);
    }

    [Fact]
    public void Colors_replace_only_the_leading_slots()
    {
        var next = AsiahorseUmbraRouteLogic.ApplyLighting(new AsiahorseUmbraSettings(), new UmbraLightingRequest
        {
            Sync = new UmbraEffectPatch { Effect = "breath", Colors = new[] { "#ABCDEF", "#000001" } },
        });

        Assert.Equal(new[] { "#abcdef", "#000001", "#0000ff", "#ffff00" }, next.Sync.Values["breath"].Colors);
    }

    [Fact]
    public void Merge_defaults_to_speed_four_until_edited()
    {
        var next = AsiahorseUmbraRouteLogic.ApplyLighting(new AsiahorseUmbraSettings(), new UmbraLightingRequest
        {
            Merge = new UmbraEffectPatch { Effect = "tile" },
        });

        Assert.Equal(4, next.Merge.Values["tile"].Speed);
    }

    // ── State ──

    private static UmbraSnapshot Snapshot(params (int port, int leds)[] ports)
    {
        var arr = new UmbraPort[10];
        for (var i = 0; i < arr.Length; i++)
        {
            arr[i] = new UmbraPort(0, 0, true, i, 0);
        }
        foreach (var (port, leds) in ports)
        {
            arr[port] = new UmbraPort(leds, 0, false, port, 0);
        }
        return UmbraSnapshot.Empty.With(deviceId: "CDAB2F9648BC44FE", firmware: "1.0.6", ports: arr);
    }

    [Fact]
    public void State_matches_the_contract_shape()
    {
        var settings = new NexusSettings();

        var state = AsiahorseUmbraRouteLogic.BuildState(true, Snapshot((9, 6)), settings, 20);

        Assert.True(state.IsConnected);
        Assert.Equal("CDAB2F9648BC44FE", state.DeviceId);
        Assert.Equal("1.0.6", state.Firmware);
        Assert.Equal("nexus", state.Lighting.Source);
        Assert.True(state.Lighting.LightOn);
        Assert.Equal("sync", state.Lighting.Group);
        Assert.Equal("rainbow", state.Lighting.Sync.Effect);
        Assert.Equal(3, state.Lighting.Sync.Speed);
        Assert.Equal(3, state.Lighting.Sync.Brightness);
        Assert.Equal("forward", state.Lighting.Sync.Direction);
        Assert.Equal(new[] { "#ff0000", "#00ff00", "#0000ff", "#ffff00" }, state.Lighting.Sync.Colors);
        Assert.Equal(4, state.Lighting.Merge.Speed);
        var async = Assert.Single(state.Lighting.Async);
        Assert.Equal(9, async.Port);
        Assert.False(state.Fan.SyncMb);
        Assert.Equal(20, state.Fan.Duty);
        Assert.Equal("memory", state.Boot.Mode);
        Assert.Equal(10, state.Ports.Length);
        Assert.Equal(15, state.Effects.Length);
        Assert.Equal(10, state.Effects.Count(e => e.Merge));
    }

    [Fact]
    public void State_ports_show_one_based_order_and_the_flow_direction()
    {
        var state = AsiahorseUmbraRouteLogic.BuildState(true, Snapshot((9, 6)), new NexusSettings(), 0);

        Assert.Equal(1, state.Ports[0].Order);
        Assert.Equal("forward", state.Ports[0].Direction);
        Assert.Equal(10, state.Ports[9].Order);
        Assert.Equal("reverse", state.Ports[9].Direction);
        Assert.Equal(6, state.Ports[9].LedCount);
        Assert.Equal(9, state.Ports[9].Index);
    }

    [Fact]
    public void State_lists_async_only_for_ports_with_leds()
    {
        var state = AsiahorseUmbraRouteLogic.BuildState(true, Snapshot((1, 4), (6, 9)), new NexusSettings(), 0);

        Assert.Equal(new[] { 1, 6 }, state.Lighting.Async.Select(a => a.Port));
    }

    [Fact]
    public void State_reports_the_effect_controls_from_the_catalog()
    {
        var state = AsiahorseUmbraRouteLogic.BuildState(true, Snapshot(), new NexusSettings(), 0);

        var staticFx = state.Effects.Single(e => e.Key == "static");
        Assert.Equal(1, staticFx.Colors);
        Assert.False(staticFx.Speed);
        Assert.False(staticFx.Direction);
        Assert.False(staticFx.Merge);
        var tile = state.Effects.Single(e => e.Key == "tile");
        Assert.Equal(4, tile.Colors);
        Assert.True(tile.Speed);
        Assert.True(tile.Direction);
        Assert.True(tile.Merge);
    }
}
