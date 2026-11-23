using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Persistence;
using Nexus.Service.Routes;
using Xunit;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Tests.Lighting;

/// <summary>L-Connect 2.1.29's Quick Sync: the six effects and how each device gets them.</summary>
public class LianLiSyncTests
{
    private static readonly List<string> Four = new() { "#112233", "#445566", "#778899", "#AABBCC" };

    private static NexusSettings Synced(string effect, LianLiSyncLook? look = null)
    {
        var settings = new NexusSettings();
        settings.Devices.LianLiSync = new LianLiSyncSettings { Enabled = true, Effect = effect };
        if (look is not null)
        {
            settings.Devices.LianLiSync.Looks[effect] = look;
        }
        return settings;
    }

    [Fact]
    public void The_effects_and_defaults_are_Quick_Sync_ones()
    {
        // QuickSyncLightingProfile.defaultLightingSettings: Rainbow..Runway, speed 75 (50 for Runway).
        Assert.Equal(new[] { "rainbow", "rainbowMorph", "static", "breathing", "meteor", "runway" }, LianLiSync.Effects.Select(e => e.Key));
        Assert.Equal(new[] { true, true, false, true, true, true }, LianLiSync.Effects.Select(e => e.HasSpeed));
        Assert.Equal(new[] { true, false, false, false, true, false }, LianLiSync.Effects.Select(e => e.HasDirection));
        Assert.Equal(new[] { 3, 3, 3, 3, 3, 2 }, LianLiSync.Effects.Select(e => e.DefaultSpeed));
        Assert.Equal(new[] { "#00FFAA", "#FF0000", "#96FF00", "#FFD700" }, LianLiSync.Find("static")!.DefaultColors);
        Assert.Equal(new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }, LianLiSync.Find("meteor")!.DefaultColors);
        Assert.Equal(new[] { "#FF0000", "#00FFAA" }, LianLiSync.Find("runway")!.DefaultColors);
        var look = LianLiSync.LookOf(new LianLiSyncSettings(), "runway");
        Assert.Equal(2, look.Speed);
        Assert.Equal(4, look.Brightness);
    }

    [Theory]
    [InlineData("rainbow", 3, 4)]
    [InlineData("runway", 2, 5)]
    [InlineData("breathing", 4, 3)]
    [InlineData("static", 1, 5)]
    public void A_TL_chain_gets_the_same_effect_on_the_whole_fan_at_L_Connect_speed_step(string effect, int speed, int step)
    {
        // applyToTLV2Fan: ValueToRFLampEffectSpeedType of the slider; Static has none, read as L3.
        var own = new LianLiWirelessChainLighting { Mode = "wave", UpDownSwap = true, Up = new LianLiFlexZoneSettings() };
        var look = new LianLiSyncLook { Speed = speed, Brightness = 2, Direction = 1, Colors = LianLiSync.Find(effect)!.DefaultColors.ToList() };
        var (lighting, speedStep) = LianLiSync.TlLighting(Synced(effect, look).Devices.LianLiSync, own)!.Value;
        Assert.Equal(effect, lighting.Mode);
        Assert.Equal(step, speedStep);
        Assert.Equal(2, lighting.Brightness);
        Assert.Equal(effect == "rainbow" ? 1 : 0, lighting.Direction);
        Assert.Equal(look.Colors, lighting.Colors);
        Assert.Null(lighting.Up);
        Assert.True(lighting.UpDownSwap);
    }

    [Fact]
    public void The_TL_loop_plays_at_the_sync_step()
    {
        var look = new Slv3TlLook("static", 0, 0, 4, new[] { new RgbColor(255, 0, 0) });
        var page = Slv3FanEffects.RenderTl(Slv3FanFamily.Tlv2Lcd, 2, look, look, downSetLast: false, upDownSwap: false);
        var synced = Slv3FanEffects.RenderTl(Slv3FanFamily.Tlv2Lcd, 2, look, look, downSetLast: false, upDownSwap: false, speedStep: 5);
        Assert.Equal(77.0, page.IntervalMs, 9);
        Assert.Equal(55.0, synced.IntervalMs, 9);
        Assert.Equal(page.Frames, synced.Frames);
    }

    [Fact]
    public void The_OLED_Curved_takes_its_colours_round_the_list()
    {
        // applyToCurve: Static/Breathing one colour, Runway two, Meteor six as colors[i % count].
        var settings = Synced("meteor", new LianLiSyncLook { Speed = 1, Brightness = 3, Direction = 1, Colors = Four });
        var curve = LianLiSync.CurveLighting(settings);
        Assert.Equal("meteor", curve.Mode);
        Assert.Equal(new[] { "#112233", "#445566", "#778899", "#AABBCC", "#112233", "#445566" }, curve.Colors);
        Assert.Equal(1, curve.Speed);
        Assert.Equal(3, curve.Brightness);
        Assert.Equal(1, curve.Direction);
        Assert.Equal(new[] { "#112233" }, LianLiSync.CurveLighting(Synced("breathing", new LianLiSyncLook { Colors = Four })).Colors);
        Assert.Equal(new[] { "#112233", "#445566" }, LianLiSync.CurveLighting(Synced("runway", new LianLiSyncLook { Colors = Four.Take(2).ToList() })).Colors);
        Assert.Empty(LianLiSync.CurveLighting(Synced("rainbow")).Colors);
        Assert.Equal(0, LianLiSync.CurveLighting(Synced("runway", new LianLiSyncLook { Direction = 1, Colors = Four.Take(2).ToList() })).Direction);
    }

    [Fact]
    public void The_8_8_bezel_doubles_Meteor_colours_and_plays_Static_at_its_slowest_step()
    {
        // applyToUniversalScreen8p8Inch: Meteor = c0,c0,c1,c1,c2,c2; Static goes with no speed, ConstrainMap reads it as step 7.
        var meteor = LianLiSync.Screen88Lighting(Synced("meteor", new LianLiSyncLook { Speed = 4, Colors = Four }));
        Assert.Equal(new[] { "#112233", "#112233", "#445566", "#445566", "#778899", "#778899" }, meteor.Colors);
        Assert.Equal(4, meteor.Speed);
        var still = LianLiSync.Screen88Lighting(Synced("static", new LianLiSyncLook { Speed = 4, Colors = Four }));
        Assert.Equal(new[] { "#112233" }, still.Colors);
        Assert.Equal(0, still.Speed);
    }

    [Fact]
    public void Sync_off_leaves_every_device_its_own_look()
    {
        var settings = Synced("meteor");
        settings.Devices.LianLiSync.Enabled = false;
        Assert.Same(settings.Devices.HydroShift2CurveLighting, LianLiSync.CurveLighting(settings));
        Assert.Same(settings.Devices.UniversalScreen88Lighting, LianLiSync.Screen88Lighting(settings));
    }

    [Fact]
    public void The_route_keeps_one_look_per_effect_and_checks_the_colour_count()
    {
        var sync = new LianLiSyncSettings();
        Assert.Equal("unknown effect", LianLiSyncRoutes.Validate(new LianLiSyncRequest { Effect = "lava" }, sync));
        Assert.Equal("runway takes 2 colors", LianLiSyncRoutes.Validate(new LianLiSyncRequest { Effect = "runway", Colors = Four.ToArray() }, sync));
        Assert.Equal("invalid color", LianLiSyncRoutes.Validate(new LianLiSyncRequest { Effect = "runway", Colors = new[] { "red", "#000000" } }, sync));

        var meteor = LianLiSyncRoutes.Patch(sync, new LianLiSyncRequest { Enabled = true, Effect = "meteor", Speed = 1, Colors = Four.ToArray() });
        Assert.True(meteor.Enabled);
        Assert.Equal("meteor", meteor.Effect);
        Assert.Equal(1, meteor.Looks["meteor"].Speed);
        var runway = LianLiSyncRoutes.Patch(meteor, new LianLiSyncRequest { Effect = "runway" });
        Assert.Equal("runway", runway.Effect);
        Assert.Equal(Four, runway.Looks["meteor"].Colors);
        Assert.False(runway.Looks.ContainsKey("runway"));
        Assert.Equal(new[] { "#FF0000", "#00FFAA" }, LianLiSync.LookOf(runway, "runway").Colors);
    }

    [Fact]
    public void The_sync_lists_the_TL_chains_and_USB_devices_it_drives()
    {
        var devices = LianLiMergeService.Present(new Slv3State(), curve: true, screen88: false);
        var response = LianLiSyncRoutes.BuildResponse(Synced("breathing"), devices);
        Assert.True(response.Enabled);
        Assert.Equal("breathing", response.Effect);
        Assert.Equal(6, response.Effects.Count(e => !e.Merge));
        Assert.Equal(4, response.Effects.Single(e => e.Key == "breathing").ColorsMax);
        var device = Assert.Single(response.Devices);
        Assert.Equal("hydroShift2Curve", device.Kind);
        Assert.Equal("breathing", device.Mode);
    }
}
