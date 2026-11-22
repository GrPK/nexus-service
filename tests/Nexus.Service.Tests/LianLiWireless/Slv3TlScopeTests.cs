using System.Collections.Generic;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Persistence;
using Nexus.Service.Routes;
using Xunit;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Tests.LianLiWireless;

/// <summary>L-Connect's Up / Down scope and Up/Down swap on a wireless TL chain.</summary>
public class Slv3TlScopeTests
{
    private static readonly IReadOnlyList<Slv3StrimerEffectInfo> TlModes = Slv3FanEffects.CatalogFor(Slv3FanFamily.Tlv2Lcd);

    private static string? ValidateTl(Slv3ChainLightingRequest body) =>
        Slv3Routes.ValidateLightingRequest(body, TlModes, perLane: false, tl: true);

    private static LianLiWirelessChainLighting Chain() => new()
    {
        Mode = "rainbow",
        Speed = 2,
        Direction = 1,
        Brightness = 3,
        Colors = new List<string> { "#FF0000" },
    };

    [Theory]
    [InlineData("all")]
    [InlineData("up")]
    [InlineData("down")]
    public void A_TL_chain_takes_every_scope(string scope) =>
        Assert.Null(ValidateTl(new Slv3ChainLightingRequest { Scope = scope, Mode = "meteor", UpDownSwap = true }));

    [Fact]
    public void Scope_and_swap_are_refused_off_TL_chains()
    {
        var sl = Slv3FanEffects.CatalogFor(Slv3FanFamily.Slv3Lcd);
        Assert.Equal("scope not supported", Slv3Routes.ValidateLightingRequest(new Slv3ChainLightingRequest { Scope = "up" }, sl, perLane: false));
        Assert.Equal("up/down swap not supported", Slv3Routes.ValidateLightingRequest(new Slv3ChainLightingRequest { UpDownSwap = true }, sl, perLane: false));
    }

    [Fact]
    public void An_unknown_scope_or_a_live_mode_on_one_half_is_refused()
    {
        Assert.Equal("invalid scope", ValidateTl(new Slv3ChainLightingRequest { Scope = "top" }));
        Assert.Equal("unknown mode", ValidateTl(new Slv3ChainLightingRequest { Scope = "down", Mode = LianLiWirelessChainLighting.ModeCustom }));
    }

    [Fact]
    public void A_half_look_starts_from_the_chain_look_and_leaves_the_chain_look_alone()
    {
        var next = Slv3Routes.PatchChainLighting(Chain(), new Slv3ChainLightingRequest { Scope = "up", Mode = "stack", Speed = 4 });
        Assert.Equal("rainbow", next.Mode);
        Assert.Equal(2, next.Speed);
        Assert.NotNull(next.Up);
        Assert.Equal("stack", next.Up!.Mode);
        Assert.Equal(4, next.Up.Speed);
        Assert.Equal(1, next.Up.Direction);
        Assert.Equal(3, next.Up.Brightness);
        Assert.Equal(new[] { "#FF0000" }, next.Up.Colors);
        Assert.Null(next.Down);
        Assert.Equal("up", next.LastScope);
    }

    [Fact]
    public void A_whole_fan_look_drops_both_halves_but_a_live_mode_keeps_them()
    {
        var halves = Slv3Routes.PatchChainLighting(Chain(), new Slv3ChainLightingRequest { Scope = "down", Mode = "wave" });
        var live = Slv3Routes.PatchChainLighting(halves, new Slv3ChainLightingRequest { Mode = LianLiWirelessChainLighting.ModeCustom });
        Assert.NotNull(live.Down);
        Assert.Equal("down", live.LastScope);
        var whole = Slv3Routes.PatchChainLighting(halves, new Slv3ChainLightingRequest { Speed = 1 });
        Assert.Null(whole.Up);
        Assert.Null(whole.Down);
        Assert.Null(whole.LastScope);
        Assert.Equal(1, whole.Speed);
    }

    [Fact]
    public void The_swap_is_stored_with_the_chain_unless_it_is_a_TL_Flex_chain()
    {
        Assert.True(Slv3Routes.PatchChainLighting(Chain(), new Slv3ChainLightingRequest { UpDownSwap = true }).UpDownSwap);
        Assert.False(Slv3Routes.PatchChainLighting(Chain(), new Slv3ChainLightingRequest { UpDownSwap = true }, tlFlex: true).UpDownSwap);
    }

    [Fact]
    public void The_preset_plays_each_half_look_and_the_speed_of_the_half_set_last()
    {
        var chain = Slv3Routes.PatchChainLighting(Chain(), new Slv3ChainLightingRequest { Scope = "down", Mode = "collide", Speed = 4, Colors = new[] { "#00FF00" } });
        var preset = Slv3LightingFrameWriter.RenderTlPreset(chain, Slv3FanFamily.Tlv2Led, 2, swap: true);
        var red = new[] { new RgbColor(255, 0, 0) };
        var up = new Slv3TlLook("rainbow", 2, 1, 3, red);
        var down = new Slv3TlLook("collide", 4, 1, 3, new[] { new RgbColor(0, 255, 0) });
        var expected = Slv3FanEffects.RenderTl(Slv3FanFamily.Tlv2Led, 2, up, down, downSetLast: true, upDownSwap: true);
        Assert.Equal(expected.Frames, preset.Frames);
        Assert.Equal(expected.IntervalMs, preset.IntervalMs);
    }
}
