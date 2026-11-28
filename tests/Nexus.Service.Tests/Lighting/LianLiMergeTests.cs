using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Persistence;
using Nexus.Service.Routes;
using Xunit;

namespace Nexus.Service.Tests.Lighting;

/// <summary>
/// L-Connect 2.1.29's merged Quick Sync effects: one line through every device in merge order. The pinned
/// hashes are outputs the L-Connect comparison harness matched byte for byte (decompiled GetUIeEffect,
/// UiMode*, GetTL, GetHS2 and GetL88 against <see cref="LianLiMerge.Render"/>, 380 cases).
/// </summary>
public class LianLiMergeTests
{
    // The user's set: three TL chains (3, 3 and 1 fans, the second read reversed), the OLED Curved, the 8.8 bezel reversed.
    private static readonly List<LianLiMerge.Participant> Lineup = new()
    {
        new(LianLiMerge.KindTl, "AA", 3, 39, 0),
        new(LianLiMerge.KindTl, "BB", 3, 39, 1),
        new(LianLiMerge.KindTl, "CC", 1, 13, 0),
        new(LianLiMerge.KindCurve, LianLiMergeService.CurveId, 0, 14, 0),
        new(LianLiMerge.KindScreen88, LianLiMergeService.Screen88Id, 0, 31, 1),
    };

    private static LianLiSyncLook Defaults(LianLiMerge.EffectInfo e) =>
        new() { Speed = e.DefaultSpeed, Brightness = 4, Direction = 0, Colors = e.DefaultColors.ToList() };

    [Theory]
    [InlineData("mergeMeteor", 148, 48, 30, "E3C451FB925CF98446EDEBAFDEB9FBF62AE4CBD7FF3E3385922E888F25C198F1", "751FA89EC04D82443D843F020F399A3F86CACAE155A9C82374CFC207F89EB006", "83F2072C8DA5EAFF8DF24729F3CADE8AE1BBAFA46E798D5641CCEA5DE2424423", "83B99940E18D7814A18A5EA5963FE44491617E0D5CEE085A6FD60302221167DD", "AFA9F23319AD67049A5CB8BC50FF9A834D48E8FE61DA6DD8B10CAAFAD8F07A10")]
    [InlineData("mergeWave", 24, 48, 30, "040F06FF14FE4C44B978CDB04A5DB58F3A40351AB47B8808098013159D36941A", "C384FB44C8D9B0CC09174DC8C1BCC214452CB17A287F10E2D8247F96BB35C333", "93A268E5EA606438A698ECEA022AA2F461F3ABF655DBAB38B36CF8D60A9DA538", "26BA5C31604D478E30E9AF409ACD8C52E7BA18F429D2E4029B380D6C59FEDB81", "C52F3477B531B99C73C921795B544D60E43EC1F9F4BA2A02A9DEB3A7305F9725")]
    [InlineData("mergeHeartBeat", 400, 72, 45, "5CF598FBA498AC2B7E3776AFAC4CAD9281DFA089C952E04E9C8025779C529768", "F56ADA14084AA52A9864879B43DBF366EFD16B955738F3CE36D732A98522DA2D", "AED0B99D473D66AD1424EF345CFC4F51C8FBF39AB59EA0C95D5F8C5790CC0BEB", "C65235464838EFD9B62DDDE94CEF4ED4E6CF8E0DA05F9EEC1A2BA4A8BFA4AC20", "6C85DD8ED6F32267C8ADA0E55336BE0A2AEB5D368F80D6EDEB6D740C6CF5F3A6")]
    public void The_line_matches_L_Connect_byte_for_byte(string key, int frames, int tlInterval, int usbPeriod, string aa, string bb, string cc, string curve, string screen88)
    {
        var effect = LianLiMerge.Find(key)!;
        var outputs = LianLiMerge.Render(effect, Defaults(effect), Lineup);
        var expected = new[] { aa, bb, cc, curve, screen88 };
        for (var i = 0; i < Lineup.Count; i++)
        {
            var o = outputs[Lineup[i].Id];
            if (o.Tl is { } tl)
            {
                Assert.Equal(frames, tl.FrameCount);
                Assert.Equal(tlInterval, tl.IntervalMs);
                Assert.Equal(frames * Lineup[i].FanCount * 26 * 3, tl.Frames.Length);
                Assert.Equal(expected[i], Convert.ToHexString(SHA256.HashData(tl.Frames)));
            }
            else
            {
                Assert.Equal(frames, o.Frames!.Count);
                Assert.Equal(usbPeriod, o.PeriodMs);
                Assert.All(o.Frames, f => Assert.Equal((Lineup[i].Kind == LianLiMerge.KindCurve ? 35 : 60) * 3, f.Length));
                Assert.Equal(expected[i], Convert.ToHexString(SHA256.HashData(o.Frames.SelectMany(f => f).ToArray())));
            }
        }
    }

    [Fact]
    public void Every_merged_effect_renders_for_every_device()
    {
        foreach (var effect in LianLiMerge.Effects)
        {
            var outputs = LianLiMerge.Render(effect, Defaults(effect), Lineup);
            Assert.Equal(Lineup.Count, outputs.Count);
            foreach (var p in Lineup)
            {
                var o = outputs[p.Id];
                Assert.True(p.Kind == LianLiMerge.KindTl ? o.Tl is { FrameCount: > 0 } : o.Frames is { Count: > 0 }, effect.Key + " " + p.Id);
            }
        }
    }

    [Fact]
    public void A_one_frame_effect_holds_with_L_Connect_s_1600_interval()
    {
        var effect = LianLiMerge.Find("mergeStatic")!;
        var outputs = LianLiMerge.Render(effect, Defaults(effect), Lineup);
        Assert.Single(outputs[LianLiMergeService.CurveId].Frames!);
        Assert.Equal(1000, outputs[LianLiMergeService.CurveId].PeriodMs);
        // The first dot sits on the Curve's seven middle LEDs (14..20).
        var frame = outputs[LianLiMergeService.CurveId].Frames![0];
        Assert.Equal(frame.AsSpan(14 * 3, 3).ToArray(), frame.AsSpan(20 * 3, 3).ToArray());
    }

    [Fact]
    public void Only_the_effect_s_own_colour_slots_count()
    {
        // Twinkle shows no colours in L-Connect; a stray stored colour does not change it.
        var twinkle = LianLiMerge.Find("mergeTwinkle")!;
        var tl = new List<LianLiMerge.Participant> { Lineup[0] };
        var plain = LianLiMerge.Render(twinkle, Defaults(twinkle), tl)["AA"].Tl!;
        var stray = LianLiMerge.Render(twinkle, new LianLiSyncLook { Speed = 2, Brightness = 4, Colors = new() { "#FF0000" } }, tl)["AA"].Tl!;
        Assert.Equal(plain.Frames, stray.Frames);
    }

    [Fact]
    public void A_device_joins_the_end_when_first_seen_and_keeps_its_place_while_away()
    {
        var tlA = new LianLiMerge.Participant(LianLiMerge.KindTl, "AA", 3, 39, 0);
        var tlB = new LianLiMerge.Participant(LianLiMerge.KindTl, "BB", 1, 13, 0);
        var curve = new LianLiMerge.Participant(LianLiMerge.KindCurve, LianLiMergeService.CurveId, 0, 14, 0);
        var screen = new LianLiMerge.Participant(LianLiMerge.KindScreen88, LianLiMergeService.Screen88Id, 0, 31, 0);

        // First run: everything is new, in discovery order.
        var first = LianLiMergeService.Order(new List<LianLiMergeMember>(), new[] { tlA, curve }, out var stored);
        Assert.Equal(new[] { "AA", LianLiMergeService.CurveId }, first.Select(p => p.Id));
        Assert.NotNull(stored);

        // The user put the Curve first and reversed it; the 8.8 and another chain show up later and go to the end.
        stored = new List<LianLiMergeMember> { new() { Id = LianLiMergeService.CurveId, Direction = 1 }, new() { Id = "AA" } };
        var later = LianLiMergeService.Order(stored, new[] { tlA, tlB, curve, screen }, out var grown);
        Assert.Equal(new[] { LianLiMergeService.CurveId, "AA", "BB", LianLiMergeService.Screen88Id }, later.Select(p => p.Id));
        Assert.Equal(1, later[0].Direction);
        Assert.Equal(new[] { LianLiMergeService.CurveId, "AA", "BB", LianLiMergeService.Screen88Id }, grown!.Select(m => m.Id));

        // A chain away keeps its slot; nothing new means nothing to store.
        var away = LianLiMergeService.Order(grown!, new[] { tlB, curve }, out var unchanged);
        Assert.Equal(new[] { LianLiMergeService.CurveId, "BB" }, away.Select(p => p.Id));
        Assert.Null(unchanged);
    }

    [Fact]
    public void The_line_takes_TL_chains_in_receiver_order_then_the_Curve_and_the_bezel()
    {
        var state = new Slv3State
        {
            IsConnected = true,
            Fans = new[]
            {
                new Slv3FanInfo { Mac = "aa", BoundToUs = true, FanCount = 3, FanType = 28 },
                // Another PC's chain, and a non-TL chain, stay off the line.
                new Slv3FanInfo { Mac = "bb", BoundToUs = false, FanCount = 3, FanType = 28 },
                new Slv3FanInfo { Mac = "dd", BoundToUs = true, FanCount = 3, FanType = 20 },
                new Slv3FanInfo { Mac = "cc", BoundToUs = true, FanCount = 1, FanType = 33 },
            },
        };
        var present = LianLiMergeService.Present(state, curve: true, screen88: true);
        Assert.Equal(new[] { "AA", "CC", LianLiMergeService.CurveId, LianLiMergeService.Screen88Id }, present.Select(p => p.Id));
        Assert.Equal(new[] { 39, 13, 14, 31 }, present.Select(p => p.Dots));
    }

    [Fact]
    public void USB_devices_step_on_a_shared_clock()
    {
        Assert.Equal((0, 30), LianLiMergeService.FrameAt(1000, 1000, 30, 148, 33));
        Assert.Equal((3, 20), LianLiMergeService.FrameAt(1000, 1100, 30, 148, 33));
        Assert.Equal((0, 33), LianLiMergeService.FrameAt(1000, 99999, 1000, 1, 33));
        // Card brightness scales, colour order swaps; full brightness leaves L-Connect's bytes alone.
        var frames = new List<byte[]> { new byte[] { 200, 100, 50 } };
        Assert.Equal(new byte[] { 200, 100, 50 }, LianLiMergeService.Adjust(frames, 1.0, null)[0]);
        Assert.Equal(new byte[] { 100, 50, 25 }, LianLiMergeService.Adjust(frames, 0.5, null)[0]);
        Assert.Equal(new byte[] { 100, 200, 50 }, LianLiMergeService.Adjust(frames, 1.0, "GRB")[0]);
    }

    [Fact]
    public void The_route_offers_the_merged_effects_and_the_devices_in_merge_order()
    {
        var settings = new NexusSettings();
        settings.Devices.LianLiSync = new LianLiSyncSettings { Enabled = true, Effect = "mergeColorCycle" };
        var response = LianLiSyncRoutes.BuildResponse(settings, Lineup);
        Assert.Equal("mergeColorCycle", response.Effect);
        Assert.Equal(25, response.Effects.Length);
        Assert.Equal(19, response.Effects.Count(e => e.Merge));
        var cycle = response.Effects.Single(e => e.Key == "mergeColorCycle");
        Assert.Equal(new[] { "#0000FF", "#FF0000", "#FFFF00" }, cycle.Colors);
        Assert.Equal(3, cycle.ColorsMax);
        Assert.Equal(new[] { "AA", "BB", "CC", LianLiMergeService.CurveId, LianLiMergeService.Screen88Id }, response.Devices.Select(d => d.Id));
        Assert.Equal(new[] { 0, 1, 0, 0, 1 }, response.Devices.Select(d => d.Direction));
        Assert.Equal(new[] { 39, 39, 13, 14, 31 }, response.Devices.Select(d => d.Dots));
    }

    [Fact]
    public void The_route_reorders_the_line_and_checks_the_devices()
    {
        var sync = new LianLiSyncSettings
        {
            Effect = "mergeWave",
            MergeDevices = new() { new() { Id = "AA" }, new() { Id = "BB", Direction = 1 }, new() { Id = LianLiMergeService.CurveId } },
        };
        var known = new[] { "AA", "BB", LianLiMergeService.CurveId };
        Assert.Equal("mergeWave takes 1 colors", LianLiSyncRoutes.Validate(new LianLiSyncRequest { Colors = new[] { "#000000", "#111111" } }, sync, known));
        Assert.Null(LianLiSyncRoutes.Validate(new LianLiSyncRequest { Effect = "mergeRainbow" }, sync, known));
        Assert.Equal("unknown device ZZ", LianLiSyncRoutes.Validate(new LianLiSyncRequest { Devices = new[] { new LianLiSyncDeviceOrderDto { Id = "ZZ" } } }, sync, known));
        Assert.Equal("each device once", LianLiSyncRoutes.Validate(new LianLiSyncRequest { Devices = new[] { new LianLiSyncDeviceOrderDto { Id = "AA" }, new LianLiSyncDeviceOrderDto { Id = "aa" } } }, sync, known));
        Assert.Equal("direction is 0 or 1", LianLiSyncRoutes.Validate(new LianLiSyncRequest { Devices = new[] { new LianLiSyncDeviceOrderDto { Id = "AA", Direction = 2 } } }, sync, known));

        var patched = LianLiSyncRoutes.Patch(sync, new LianLiSyncRequest
        {
            Devices = new[] { new LianLiSyncDeviceOrderDto { Id = LianLiMergeService.CurveId, Direction = 1 }, new LianLiSyncDeviceOrderDto { Id = "bb" } },
        });
        Assert.Equal(new[] { LianLiMergeService.CurveId, "BB", "AA" }, patched.MergeDevices.Select(m => m.Id));
        Assert.Equal(new[] { 1, 1, 0 }, patched.MergeDevices.Select(m => m.Direction));
        // Any other patch keeps the order.
        Assert.Same(patched.MergeDevices, LianLiSyncRoutes.Patch(patched, new LianLiSyncRequest { Enabled = true }).MergeDevices);
    }

    [Fact]
    public void A_merged_effect_starts_from_Quick_Sync_s_wireless_defaults()
    {
        var look = LianLiSync.LookOf(new LianLiSyncSettings(), "mergeRunway");
        Assert.Equal(2, look.Speed);
        Assert.Equal(LianLiSync.MaxLevel, look.Brightness);
        Assert.Equal(new[] { "#FF0000", "#00FFAA" }, look.Colors);
        // The six per-device effects do not take a merged effect's place.
        var settings = new NexusSettings();
        settings.Devices.LianLiSync = new LianLiSyncSettings { Enabled = true, Effect = "mergeRunway" };
        Assert.Same(settings.Devices.HydroShift2CurveLighting, LianLiSync.CurveLighting(settings));
        Assert.Null(LianLiSync.TlLighting(settings.Devices.LianLiSync, null));
    }
}
