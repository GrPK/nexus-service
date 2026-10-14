using System.Text.Json.Nodes;
using Nexus.Service.Cooling;
using Nexus.Service.Migration;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Migration;

public class Nexus2FanCurvePlannerTests
{
    private static readonly (double, double)[] SafePoints = { (0, 30), (50, 50), (80, 80), (100, 100) };

    private static Nexus2FanCurvePlan Plan(Hw hw, params JsonObject[] curves) =>
        Nexus2FanCurvePlanner.Build(N2.Single(100, curves), hw.Env());

    private static Hw Verified()
    {
        var hw = Hw.Y70();
        hw.AllowHubs = true;
        return hw;
    }

    private static Nexus2PlannedCurve Only(Nexus2FanCurvePlan plan) => Assert.Single(plan.Curves);

    private static Nexus2PlannedCurve Skipped(Nexus2FanCurvePlan plan, string reason)
    {
        var curve = Only(plan);
        Assert.False(curve.Import);
        Assert.Equal(reason, curve.Reason);
        Assert.Null(curve.Document);
        return curve;
    }

    private static string TargetReason(Hw hw, JsonObject output, bool flat = false)
    {
        var curve = flat
            ? Only(Plan(hw, N2.Flat("T", 60, new[] { output })))
            : Only(Plan(hw, N2.Graph("T", SafePoints, new[] { output })));
        return Assert.Single(curve.Targets).Reason ?? "import";
    }

    private static string ChannelOf(Hw hw, JsonObject output) =>
        Assert.Single(Only(Plan(hw, N2.Graph("T", SafePoints, new[] { output }))).Document!.Outputs).Id;

    // ── Selection ──

    [Fact]
    public void Easy_mode_imports_no_curves_and_says_why()
    {
        var plan = Nexus2FanCurvePlanner.Build(N2.Root(N2.Profile("P", true, 100, easy: true, N2.Graph("G", SafePoints, new[] { N2.Pwm(1) }))), Hw.Y70().Env());
        Assert.Equal("recommendedMode", plan.Note);
        Assert.Empty(plan.Curves);
        Assert.Equal(0, plan.ImportableCount);
    }

    [Fact]
    public void Only_the_active_profile_is_read_whatever_the_array_order()
    {
        var quiet = N2.Profile("Quiet", false, 100, false, N2.Flat("Quiet flat", 80, new[] { N2.Pwm(2) }));
        var main = N2.Profile("Main", true, 100, false, N2.Graph("Main graph", SafePoints, new[] { N2.Pwm(1) }));
        foreach (var root in new[] { N2.Root(quiet, main), N2.Root(main, quiet) })
        {
            var plan = Nexus2FanCurvePlanner.Build(root, Hw.Y70().Env());
            Assert.Equal("Main graph", Assert.Single(plan.Curves).Name);
            Assert.Equal(1, plan.OtherProfiles);
        }
    }

    [Fact]
    public void An_inactive_profile_without_curves_is_not_counted()
    {
        var empty = N2.Profile("Empty", false, 100, false, N2.Graph("Unwired", SafePoints, Array.Empty<JsonObject>()));
        var main = N2.Profile("Main", true, 100, false, N2.Graph("G", SafePoints, new[] { N2.Pwm(1) }));
        Assert.Equal(0, Nexus2FanCurvePlanner.Build(N2.Root(empty, main), Hw.Y70().Env()).OtherProfiles);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void A_file_without_exactly_one_active_profile_imports_nothing(int activeCount)
    {
        var profiles = Enumerable.Range(0, 2)
            .Select(i => N2.Profile("P" + i, i < activeCount, 100, false, N2.Graph("G", SafePoints, new[] { N2.Pwm(1) })))
            .ToArray();
        Assert.Empty(Nexus2FanCurvePlanner.Build(N2.Root(profiles), Hw.Y70().Env()).Curves);
    }

    [Fact]
    public void Curves_with_no_outputs_and_unknown_types_are_ignored_silently()
    {
        var unknown = N2.Graph("Legacy", SafePoints, new[] { N2.Pwm(1) });
        unknown["type"] = "trigger";
        var plan = Plan(Hw.Y70(), N2.Graph("Seeded", SafePoints, Array.Empty<JsonObject>()), unknown, N2.Graph("Real", SafePoints, new[] { N2.Pwm(1) }));
        Assert.Equal("Real", Assert.Single(plan.Curves).Name);
    }

    [Fact]
    public void No_curve_with_outputs_means_noUserCurves()
    {
        var plan = Plan(Hw.Y70(), N2.Graph("Default", SafePoints, Array.Empty<JsonObject>()));
        Assert.Equal("noUserCurves", plan.Note);
    }

    // ── Types ──

    [Fact]
    public void Mixed_curves_are_skipped_but_still_listed()
    {
        var curve = Skipped(Plan(Hw.Y70(), N2.Mixed("Mix", new[] { N2.Gpu(2) })), "mixedCurve");
        Assert.Equal("mixed", curve.Kind);
        Assert.All(curve.Targets, t => Assert.False(t.Import));
    }

    [Fact]
    public void A_graph_reads_graph_points_never_the_stray_top_level_points()
    {
        var curve = N2.Graph("G", SafePoints, new[] { N2.Pwm(1) });
        curve["points"] = new JsonArray(new JsonObject { ["temp"] = 999, ["speed"] = -4 });
        var doc = Only(Plan(Hw.Y70(), curve)).Document!;
        Assert.Equal(4, doc.Graph!.Points.Count);
    }

    [Fact]
    public void Linear_becomes_a_two_point_graph_with_no_per_curve_modifier()
    {
        var curve = N2.Linear("L", 30, 75, 45, 100, new[] { N2.Pwm(2) });
        // The graph block's modifier never applied to a linear curve.
        curve["graph"]!["speedModifier"] = 10;
        var doc = Only(Plan(Hw.Y70(), curve)).Document!;
        Assert.Equal("Graph", doc.Type);
        Assert.Equal(1.0, doc.Graph!.SpeedModifier);
        Assert.Equal(new[] { (30.0, 45.0), (75.0, 100.0) }, doc.Graph.Points.Select(p => (p.Temp, p.Speed)).ToArray());
    }

    [Theory]
    [InlineData(75, 30)]
    [InlineData(30, 30)]
    public void A_linear_curve_that_is_not_a_ramp_is_badPoints(double minTemp, double maxTemp) =>
        Skipped(Plan(Hw.Y70(), N2.Linear("L", minTemp, maxTemp, 45, 100, new[] { N2.Pwm(2) })), "badPoints");

    // ── Modifiers ──

    [Fact]
    public void Graph_and_global_modifiers_are_baked_in_and_nexus_3_global_is_left_alone()
    {
        var curve = N2.Graph("G", new (double, double)[] { (0, 50), (50, 60), (80, 100) }, new[] { N2.Pwm(1) }, modifier: 90);
        var plan = Nexus2FanCurvePlanner.Build(N2.Root(N2.Profile("P", true, 95, false, curve)), Hw.Y70().Env());
        var doc = Only(plan).Document!;
        Assert.Equal(1.0, doc.Graph!.SpeedModifier);
        Assert.Equal(new[] { 42.75, 51.3, 85.5 }, doc.Graph.Points.Select(p => p.Speed).ToArray());
    }

    [Fact]
    public void The_global_modifier_applies_to_flat_and_linear_curves_too()
    {
        var plan = Nexus2FanCurvePlanner.Build(N2.Root(N2.Profile("P", true, 80, false,
            N2.Flat("F", 90, new[] { N2.Pwm(1) }),
            N2.Linear("L", 30, 70, 50, 100, new[] { N2.Pwm(2) }))), Hw.Y70().Env());
        Assert.Equal(72, plan.Curves[0].Document!.Flat!.Speed);
        Assert.Equal(new[] { 40.0, 80.0 }, plan.Curves[1].Document!.Graph!.Points.Select(p => p.Speed).ToArray());
    }

    [Fact]
    public void A_global_modifier_of_zero_leaves_nothing_safe()
    {
        var plan = Nexus2FanCurvePlanner.Build(N2.Root(N2.Profile("P", true, 0, false, N2.Graph("G", SafePoints, new[] { N2.Pwm(1) }))), Hw.Y70().Env());
        Skipped(plan, "unsafeCurve");
    }

    [Theory]
    [InlineData(101)]
    [InlineData(-5)]
    public void A_global_modifier_outside_the_range_nexus_2_accepts_skips_every_curve(double global)
    {
        var plan = Nexus2FanCurvePlanner.Build(N2.Root(N2.Profile("P", true, global, false, N2.Graph("G", SafePoints, new[] { N2.Pwm(1) }))), Hw.Y70().Env());
        Skipped(plan, "badPoints");
    }

    [Theory]
    [InlineData(101)]
    [InlineData(-1)]
    public void A_per_curve_modifier_nexus_2_refuses_is_badPoints(double modifier) =>
        Skipped(Plan(Hw.Y70(), N2.Graph("G", SafePoints, new[] { N2.Pwm(1) }, modifier: modifier)), "badPoints");

    [Fact]
    public void A_missing_per_curve_modifier_is_badPoints()
    {
        var curve = N2.Graph("G", SafePoints, new[] { N2.Pwm(1) });
        curve["graph"]!.AsObject().Remove("speedModifier");
        Skipped(Plan(Hw.Y70(), curve), "badPoints");
    }

    [Fact]
    public void The_response_time_is_not_carried_over()
    {
        var doc = Only(Plan(Hw.Y70(), N2.Graph("G", SafePoints, new[] { N2.Pwm(1) }))).Document!;
        Assert.Equal(new GraphCurveData().ResponseTime, doc.Graph!.ResponseTime);
    }

    // ── Input ──

    [Theory]
    [InlineData("Power")]
    [InlineData("Load")]
    [InlineData("Clock")]
    public void Only_a_temperature_input_imports(string type) =>
        Skipped(Plan(Hw.Y70(), N2.Graph("G", SafePoints, new[] { N2.Pwm(1) }, input: N2.Input(type: type))), "nonTemperatureInput");

    [Fact]
    public void A_graph_with_no_input_never_ran_in_nexus_2()
    {
        var curve = N2.Graph("G", SafePoints, new[] { N2.Pwm(1) });
        curve["input"] = null;
        Skipped(Plan(Hw.Y70(), curve), "sensorNotFound");
    }

    [Fact]
    public void An_exact_sensor_id_resolves_to_that_one_sensor()
    {
        var doc = Only(Plan(Hw.Y70(), N2.Graph("G", SafePoints, new[] { N2.Pwm(1) }))).Document!;
        Assert.Equal("/intelcpu/0/temperature/14", doc.Input.Id);
        Assert.Equal("Temperature", doc.Input.Type);
    }

    [Fact]
    public void An_unknown_sensor_is_sensorNotFound() =>
        Skipped(Plan(Hw.Y70(), N2.Graph("G", SafePoints, new[] { N2.Pwm(1) }, input: N2.Input("/intelcpu/0/temperature/99"))), "sensorNotFound");

    [Fact]
    public void A_sensor_whose_normalized_identifier_matches_two_sources_is_sensorAmbiguous()
    {
        var hw = Hw.Y70();
        hw.Sources.Add(new TemperatureSource { Id = "/intelcpu/temperature/14", Name = "CPU Package", Category = "CPU", Value = 40 });
        var curve = N2.Graph("G", SafePoints, new[] { N2.Pwm(1) }, input: N2.Input("/intelcpu/0/0/temperature/14"));
        Skipped(Plan(hw, curve), "sensorAmbiguous");
    }

    [Fact]
    public void A_fuzzy_identifier_match_needs_the_hardware_name_to_agree()
    {
        var hw = Hw.Y70();
        hw.Sources.RemoveAll(s => s.Id == "/intelcpu/0/temperature/14");
        hw.Sources.Add(new TemperatureSource { Id = "/intelcpu/temperature/14", Name = "CPU Package", Category = "CPU", Value = 40 });
        Assert.NotNull(Only(Plan(hw, N2.Graph("G", SafePoints, new[] { N2.Pwm(1) }))).Document);

        hw.CpuModel = "Some Other CPU";
        Skipped(Plan(hw, N2.Graph("G", SafePoints, new[] { N2.Pwm(1) })), "sensorNotFound");
    }

    [Fact]
    public void An_exact_match_on_different_known_hardware_is_a_swap_not_a_match()
    {
        var hw = Hw.Y70();
        hw.CpuModel = "Replacement CPU";
        Skipped(Plan(hw, N2.Graph("G", SafePoints, new[] { N2.Pwm(1) })), "sensorNotFound");
    }

    [Theory]
    [InlineData(14.9)]
    [InlineData(7)]
    [InlineData(115)]
    [InlineData(105.5)]
    [InlineData(float.NaN)]
    public void A_sensor_reading_outside_the_plausible_band_is_sensorImplausible(float reading)
    {
        var hw = Hw.Y70();
        hw.Sources.First(s => s.Id == "/intelcpu/0/temperature/14").Value = reading;
        Skipped(Plan(hw, N2.Graph("G", SafePoints, new[] { N2.Pwm(1) })), "sensorImplausible");
    }

    [Theory]
    [InlineData(15)]
    [InlineData(105)]
    public void The_plausible_band_includes_its_edges(float reading)
    {
        var hw = Hw.Y70();
        hw.Sources.First(s => s.Id == "/intelcpu/0/temperature/14").Value = reading;
        Assert.True(Only(Plan(hw, N2.Graph("G", SafePoints, new[] { N2.Pwm(1) }))).Import);
    }

    [Fact]
    public void Pump_coolant_inputs_map_to_the_one_connected_cooler()
    {
        var hw = Hw.Y70().AddQSeries();
        var doc = Only(Plan(hw, N2.Graph("G", SafePoints, new[] { N2.Gpu(1) }, input: N2.Input("pumpTempOut", "HYTE THICC Q60")))).Document!;
        Assert.Equal($"qseries:{Hw.QSeries}:coolant-out", doc.Input.Id);
    }

    [Fact]
    public void Pump_coolant_inputs_need_exactly_one_matching_cooler()
    {
        var curve = N2.Graph("G", SafePoints, new[] { N2.Pwm(1) }, input: N2.Input("pumpTempIn", "HYTE THICC Q60"));
        Skipped(Plan(Hw.Y70(), curve), "sensorNotFound");

        var two = Hw.Y70().AddQSeries();
        two.Sources.Add(new TemperatureSource { Id = "qseries:OTHER:coolant-in", Name = "Coolant in", Category = "Cooler", Value = 33, DeviceName = "HYTE Q60" });
        Skipped(Plan(two, curve), "sensorAmbiguous");

        // A Q80 curve never binds to a Q60.
        Skipped(Plan(Hw.Y70().AddQSeries(), N2.Graph("G", SafePoints, new[] { N2.Pwm(1) }, input: N2.Input("pumpTempIn", "HYTE THICC Q80"))), "sensorNotFound");
    }

    [Fact]
    public void The_portal_onboard_sensor_has_no_nexus_3_counterpart() =>
        Skipped(Plan(Hw.Y70().AddNp50(), N2.Graph("G", SafePoints, new[] { N2.Pwm(1) }, input: N2.Input("portalTemp", "Nexus Portal"))), "sensorNotFound");

    // ── Points ──

    [Theory]
    [InlineData(111, 50)]
    [InlineData(-1, 50)]
    [InlineData(40, 101)]
    [InlineData(40, -1)]
    public void A_point_outside_the_stored_ranges_is_badPoints(double temp, double speed) =>
        Skipped(Plan(Hw.Y70(), N2.Graph("G", new (double, double)[] { (temp, speed), (50, 60), (80, 90) }, new[] { N2.Pwm(1) })), "badPoints");

    [Fact]
    public void A_graph_needs_two_points()
    {
        Skipped(Plan(Hw.Y70(), N2.Graph("G", new (double, double)[] { (50, 80) }, new[] { N2.Pwm(1) })), "badPoints");
        Skipped(Plan(Hw.Y70(), N2.Graph("G", new (double, double)[] { (50, 80), (50, 90) }, new[] { N2.Pwm(1) })), "badPoints");
    }

    [Fact]
    public void A_point_that_is_not_a_number_is_badPoints()
    {
        var curve = N2.Graph("G", SafePoints, new[] { N2.Pwm(1) });
        curve["graph"]!["points"]![1]!["temp"] = "hot";
        Skipped(Plan(Hw.Y70(), curve), "badPoints");
    }

    [Fact]
    public void Points_are_sorted_equal_temperatures_keep_the_higher_speed_and_fractions_survive()
    {
        var points = new (double, double)[] { (80, 80), (20, 40), (20.5, 45), (20, 60), (110, 100), (0, 30) };
        var doc = Only(Plan(Hw.Y70(), N2.Graph("G", points, new[] { N2.Pwm(1) }))).Document!;
        Assert.Equal(new[] { (0.0, 30.0), (20.0, 60.0), (20.5, 45.0), (80.0, 80.0), (110.0, 100.0) }, doc.Graph!.Points.Select(p => (p.Temp, p.Speed)).ToArray());
    }

    // ── Safety floors ──

    [Theory]
    [InlineData(60, true)]
    [InlineData(59.9, false)]
    public void A_graph_must_command_sixty_percent_at_eighty_degrees(double speedAt80, bool imports)
    {
        var curve = N2.Graph("G", new (double, double)[] { (0, 30), (80, speedAt80), (100, 100) }, new[] { N2.Pwm(1) });
        var plan = Plan(Hw.Y70(), curve);
        Assert.Equal(imports, Only(plan).Import);
        if (!imports)
        {
            Assert.Equal("unsafeCurve", Only(plan).Reason);
        }
    }

    [Fact]
    public void The_floor_is_judged_after_the_modifiers()
    {
        // Safe as stored, under the floor once the per-curve modifier is applied.
        Skipped(Plan(Hw.Y70(), N2.Graph("G", new (double, double)[] { (0, 30), (80, 65), (100, 70) }, new[] { N2.Pwm(1) }, modifier: 90)), "unsafeCurve");
    }

    [Fact]
    public void The_floor_counts_the_nexus_3_global_modifier_that_still_scales_the_curve()
    {
        var hw = Hw.Y70();
        hw.NexusGlobal = 0.5;
        Skipped(Plan(hw, N2.Graph("G", new (double, double)[] { (0, 30), (80, 100) }, new[] { N2.Pwm(1) })), "unsafeCurve");
        hw.NexusGlobal = 1.5;
        Assert.True(Only(Plan(hw, N2.Graph("G", new (double, double)[] { (0, 30), (80, 60) }, new[] { N2.Pwm(1) }))).Import);
    }

    [Theory]
    [InlineData(30, true)]
    [InlineData(29, false)]
    public void A_flat_curve_must_hold_at_least_the_flat_floor(double speed, bool imports)
    {
        var plan = Plan(Hw.Y70(), N2.Flat("F", speed, new[] { N2.Pwm(1) }));
        Assert.Equal(imports, Only(plan).Import);
        Assert.Equal(imports ? null : "unsafeCurve", Only(plan).Reason);
    }

    [Fact]
    public void A_flat_speed_that_is_not_a_whole_percent_never_ran()
    {
        Skipped(Plan(Hw.Y70(), N2.Flat("F", 55.5, new[] { N2.Pwm(1) })), "badPoints");
        Skipped(Plan(Hw.Y70(), N2.Flat("F", 101, new[] { N2.Pwm(1) })), "badPoints");
    }

    [Fact]
    public void Points_under_the_stop_threshold_are_raised_and_flagged()
    {
        var curve = N2.Graph("G", new (double, double)[] { (0, 0), (25, 10), (60, 50), (80, 90) }, new[] { N2.Pwm(1) });
        var planned = Only(Plan(Hw.Y70(), curve));
        Assert.True(planned.MinimumRaised);
        Assert.Equal(new[] { 20.0, 20.0, 50.0, 90.0 }, planned.Document!.Graph!.Points.Select(p => p.Speed).ToArray());
    }

    [Fact]
    public void A_curve_that_already_clears_the_threshold_is_not_flagged() =>
        Assert.False(Only(Plan(Hw.Y70(), N2.Graph("G", SafePoints, new[] { N2.Pwm(1) }))).MinimumRaised);

    [Fact]
    public void A_curve_the_lint_flags_as_new_is_skipped_as_unsafe()
    {
        var hw = Hw.Y70();
        // Only a config carrying an imported curve has the hazard.
        hw.Lint = (curves, scope) => new LintInput
        {
            Curves = curves,
            Channels = hw.Channels,
            Sources = hw.Sources,
            ManualSpeeds = curves.Any(c => c.Id.StartsWith("n2-", StringComparison.Ordinal))
                ? new Dictionary<string, int> { [$"smarthub:{Hw.SmartHub}:fan3"] = 5 }
                : new Dictionary<string, int>(),
        };
        Skipped(Plan(hw, N2.Graph("G", SafePoints, new[] { N2.Pwm(1) })), "unsafeCurve");
    }

    [Fact]
    public void A_hazard_the_config_already_had_does_not_block_the_import()
    {
        var hw = Hw.Y70();
        hw.Lint = (curves, scope) => new LintInput
        {
            Curves = curves,
            Channels = hw.Channels,
            Sources = hw.Sources,
            ManualSpeeds = new Dictionary<string, int> { [$"smarthub:{Hw.SmartHub}:fan3"] = 5 },
        };
        Assert.True(Only(Plan(hw, N2.Graph("G", SafePoints, new[] { N2.Pwm(1) }))).Import);
    }

    // ── Targets ──

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 3)]
    public void A_smart_hub_pwm_port_maps_to_the_zero_based_fan_channel(int pwm, int fan) =>
        Assert.Equal($"smarthub:{Hw.SmartHub}:fan{fan}", ChannelOf(Hw.Y70(), N2.Pwm(pwm)));

    [Fact]
    public void A_pwm_port_on_another_hub_or_out_of_range_is_not_guessed()
    {
        Assert.Equal("targetNotFound", TargetReason(Hw.Y70(), N2.Out("PWM 1", "FanPort", "SOMEOTHERHUB")));
        Assert.Equal("unsupportedTarget", TargetReason(Hw.Y70(), N2.Out("PWM 5", "FanPort", Hw.SmartHub)));
        Assert.Equal("unsupportedTarget", TargetReason(Hw.Y70(), N2.Out("PWM 1", "FanPort")));
    }

    [Theory]
    [InlineData(0, "port1")]
    [InlineData(1, "port2")]
    public void A_mini_hub_usb_port_digit_maps_to_its_fan_port(int digit, string port)
    {
        var hw = Hw.Y70();
        hw.ManualDriven.Add($"minihub:{Hw.MiniHub}:{(digit == 0 ? "port2" : "port1")}");
        Assert.Equal($"minihub:{Hw.MiniHub}:{port}", ChannelOf(hw, N2.Mini(digit)));
    }

    [Fact]
    public void A_mini_hub_digit_for_an_led_strip_is_unsupported() =>
        Assert.Equal("unsupportedTarget", TargetReason(Hw.Y70(), N2.Mini(2)));

    [Fact]
    public void A_mini_hub_that_is_not_connected_is_targetNotFound()
    {
        var hw = Hw.Y70();
        hw.Channels.RemoveAll(c => c.Id.StartsWith("minihub:", StringComparison.Ordinal));
        Assert.Equal("targetNotFound", TargetReason(hw, N2.Mini(0)));
    }

    [Fact]
    public void Gpu_fans_map_by_hardware_and_fan_name_so_the_second_fan_is_the_second_channel()
    {
        var hw = Hw.Y70();
        Assert.Equal("/gpu-nvidia/0/control/0", ChannelOf(hw, N2.Gpu(1)));
        Assert.Equal("/gpu-nvidia/0/control/1", ChannelOf(hw, N2.Gpu(2)));
    }

    [Fact]
    public void A_gpu_fan_matches_a_channel_whose_name_carries_the_gpu_prefix()
    {
        var hw = Hw.Y70();
        hw.Channels.First(c => c.Id == "/gpu-nvidia/0/control/0").Name = "GPU Fan 1";
        var curve = N2.Graph("G", SafePoints, new[] { N2.Out("Fan 1", "GPUFan", Hw.Gpu + "-ID") });
        Assert.Equal("/gpu-nvidia/0/control/0", Assert.Single(Only(Plan(hw, curve)).Document!.Outputs).Id);
    }

    [Fact]
    public void A_gpu_fan_on_other_or_missing_hardware_is_not_found_and_two_matching_cards_are_ambiguous()
    {
        Assert.Equal("targetNotFound", TargetReason(Hw.Y70(), N2.Out("GPU Fan 1", "GPUFan", "Some Other Card-ID")));
        Assert.Equal("targetNotFound", TargetReason(Hw.Y70(), N2.Out("GPU Fan 3", "GPUFan", Hw.Gpu + "-ID")));

        var twin = Hw.Y70();
        twin.Channels.Add(new FanChannel { Id = "/gpu-nvidia/1/control/0", Name = "GPU Fan 1", IsGpu = true, DeviceId = "/gpu-nvidia/1", DeviceName = Hw.Gpu });
        Assert.Equal("targetAmbiguous", TargetReason(twin, N2.Gpu(1)));
    }

    [Fact]
    public void A_renamed_channel_is_still_found_by_its_hardware_name()
    {
        var hw = Hw.Y70();
        var fan = hw.Channels.First(c => c.Id == "/gpu-nvidia/0/control/1");
        fan.OriginalName = fan.Name;
        fan.Name = "Top exhaust";
        Assert.Equal("/gpu-nvidia/0/control/1", ChannelOf(hw, N2.Gpu(2)));
    }

    [Fact]
    public void A_motherboard_fan_maps_by_unique_name_on_the_matching_board()
    {
        var hw = Hw.Y70().AddBoardFan("/lpc/nct6798d/0/control/1", "Fan #2");
        var output = N2.Out("Fan #2", "Chassis", Hw.Board);
        Assert.Equal("/lpc/nct6798d/0/control/1", ChannelOf(hw, output));

        hw.AddBoardFan("/lpc/it8696e/0/control/1", "Fan #2");
        Assert.Equal("targetAmbiguous", TargetReason(hw, output));

        var swapped = Hw.Y70().AddBoardFan("/lpc/nct6798d/0/control/1", "Fan #2");
        swapped.BoardModel = "A Different Board";
        Assert.Equal("targetNotFound", TargetReason(swapped, output));
    }

    [Fact]
    public void The_portal_legacy_fan_maps_when_exactly_one_portal_is_connected()
    {
        var output = N2.Out("NP50Fan", "Legacy", Hw.Np50);
        Assert.Equal($"np50:{Hw.Np50}:legacy", ChannelOf(Hw.Y70().AddNp50(), output));
        Assert.Equal("targetNotFound", TargetReason(Verified(), output));

        var two = Hw.Y70().AddNp50();
        two.Channels.Add(new FanChannel { Id = "np50:OTHERNP:legacy", Name = "Legacy 4-pin", DeviceName = "HYTE NP50" });
        Assert.Equal("unsupportedTarget", TargetReason(two, output));
    }

    [Fact]
    public void A_portal_fan_module_maps_by_channel_and_firmware_index()
    {
        var hw = Hw.Y70().AddNp50();
        Assert.Equal($"np50:{Hw.Np50}:port1:dev2", ChannelOf(hw, N2.Out("NP50 Channel1Fan2", "Fan", Hw.Np50)));
        Assert.Equal("targetNotFound", TargetReason(hw, N2.Out("NP50 Channel1Fan3", "Fan", Hw.Np50)));
        Assert.Equal("unsupportedTarget", TargetReason(hw, N2.Out("NP50 Channel1Fan2/0", "Duo", Hw.Np50)));
    }

    [Fact]
    public void A_cooler_fan_maps_and_a_duo_blade_order_moves_from_zero_based_to_one_based()
    {
        var hw = Hw.Y70().AddQSeries();
        Assert.Equal($"qseries:{Hw.QSeries}:p1:2", ChannelOf(hw, N2.Out("Q60 Channel1Fan2", "Fan", Hw.QSeries)));
        Assert.Equal($"qseries:{Hw.QSeries}:p2:1:2", ChannelOf(hw, N2.Out("Q60 Channel2Fan1/1", "Duo", Hw.QSeries)));
    }

    [Fact]
    public void A_cooler_fan_needs_that_exact_model_connected_once()
    {
        var output = N2.Out("Q60 Channel1Fan2", "Fan", Hw.QSeries);
        Assert.Equal("targetNotFound", TargetReason(Verified(), output));
        Assert.Equal("targetNotFound", TargetReason(Hw.Y70().AddQSeries("HYTE Q80"), output));

        var two = Hw.Y70().AddQSeries();
        two.Channels.Add(new FanChannel { Id = "qseries:ANOTHER:p1:2", Name = "FP12", DeviceName = "HYTE Q60" });
        Assert.Equal("unsupportedTarget", TargetReason(two, output));
    }

    [Theory]
    [InlineData("Q60 Pump", "Pump")]
    [InlineData("Q80 Pump", "Pump")]
    [InlineData("Whatever Pump 1", "Fan")]
    public void Pumps_are_never_imported(string id, string deviceType)
    {
        var hw = Hw.Y70().AddQSeries();
        var curve = Only(Plan(hw, N2.Graph("G", SafePoints, new[] { N2.Out(id, deviceType, Hw.QSeries), N2.Pwm(1) })));
        Assert.Equal("pumpKept", curve.Targets[0].Reason);
        Assert.True(curve.Import);
        Assert.Single(curve.Document!.Outputs);
    }

    [Fact]
    public void A_resolved_channel_that_is_a_pump_is_still_kept()
    {
        var hw = Hw.Y70().AddQSeries();
        hw.Channels.First(c => c.Id == $"qseries:{Hw.QSeries}:p1:2").Kind = FanKinds.Pump;
        Assert.Equal("pumpKept", TargetReason(hw, N2.Out("Q60 Channel1Fan2", "Fan", Hw.QSeries)));
    }

    [Fact]
    public void A_channel_the_user_marked_uncontrolled_is_not_claimed()
    {
        var hw = Hw.Y70();
        hw.Uncontrolled.Add($"smarthub:{Hw.SmartHub}:fan0");
        var curve = Only(Plan(hw, N2.Graph("G", SafePoints, new[] { N2.Pwm(1) })));
        Assert.Equal("uncontrolledChannel", Assert.Single(curve.Targets).Reason);
        Assert.Equal("noTargets", curve.Reason);
    }

    [Fact]
    public void A_flat_curve_skips_a_gpu_fan_but_keeps_its_other_targets()
    {
        var curve = Only(Plan(Hw.Y70(), N2.Flat("F", 60, new[] { N2.Gpu(1), N2.Pwm(1) })));
        Assert.Equal("gpuFlat", curve.Targets[0].Reason);
        Assert.True(curve.Targets[1].Import);
        Assert.True(curve.Import);
        Assert.Equal($"smarthub:{Hw.SmartHub}:fan0", Assert.Single(curve.Document!.Outputs).Id);
    }

    [Fact]
    public void A_graph_curve_may_drive_a_gpu_fan() =>
        Assert.Equal("/gpu-nvidia/0/control/0", ChannelOf(Hw.Y70(), N2.Gpu(1)));

    [Fact]
    public void A_curve_whose_targets_all_fail_is_noTargets()
    {
        var curve = Skipped(Plan(Hw.Y70(), N2.Graph("G", SafePoints, new[] { N2.Out("PWM 1", "FanPort", "GONE"), N2.Out("Q60 Pump", "Pump") })), "noTargets");
        Assert.Equal(new[] { "targetNotFound", "pumpKept" }, curve.Targets.Select(t => t.Reason).ToArray());
    }

    [Fact]
    public void A_channel_two_curves_claim_is_ambiguous_for_both()
    {
        var plan = Plan(Hw.Y70(),
            N2.Graph("A", SafePoints, new[] { N2.Pwm(1), N2.Pwm(2) }),
            N2.Flat("B", 70, new[] { N2.Pwm(2), N2.Pwm(3) }));
        Assert.Equal("targetAmbiguous", plan.Curves[0].Targets[1].Reason);
        Assert.Equal("targetAmbiguous", plan.Curves[1].Targets[0].Reason);
        Assert.Equal($"smarthub:{Hw.SmartHub}:fan0", Assert.Single(plan.Curves[0].Document!.Outputs).Id);
        Assert.Equal($"smarthub:{Hw.SmartHub}:fan2", Assert.Single(plan.Curves[1].Document!.Outputs).Id);
    }

    [Fact]
    public void A_target_label_is_the_channel_name_when_resolved_and_the_nexus_2_id_otherwise()
    {
        var curve = Only(Plan(Hw.Y70(), N2.Graph("G", SafePoints, new[] { N2.Pwm(1), N2.Out("PWM 2", "FanPort", "GONE") })));
        Assert.Equal("Fan Port 1", curve.Targets[0].Label);
        Assert.Equal("PWM 2", curve.Targets[1].Label);
    }

    [Fact]
    public void Imported_curves_get_n2_ids_unique_even_when_nexus_2_ids_repeat()
    {
        var a = N2.Graph("A", SafePoints, new[] { N2.Pwm(1) }, id: "41d76d84-42ad");
        var b = N2.Graph("B", SafePoints, new[] { N2.Pwm(2) }, id: "41d76d84-42ad");
        var plan = Plan(Hw.Y70(), a, b);
        Assert.Equal(new[] { "n2-41d76d84-42ad", "n2-41d76d84-42ad-2" }, plan.Curves.Select(c => c.Document!.Id).ToArray());
    }

    [Fact]
    public void The_sanitizer_leaves_an_imported_curve_unchanged()
    {
        var doc = Only(Plan(Hw.Y70(), N2.Graph("G", SafePoints, new[] { N2.Pwm(1) }))).Document!;
        var again = CoolingSafety.Sanitize(new SetCurvesBody { Curves = { CurveWireMapper.ToWire(doc) } });
        Assert.Equal(doc.Graph!.Points.Select(p => p.Speed), again.Curves[0].Graph!.Points.Select(p => p.Speed));
    }
}
