using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Cooling;
using Nexus.Service.Migration;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Persistence;
using Nexus.Service.Sensors;

namespace Nexus.Service.Tests.Migration;

/// <summary>The planner and the apply against real Nexus 2 captures (scrubbed serials only), plus the invariant that channels not imported keep their behavior.</summary>
public class Nexus2FanCurveImportTests
{
    private static Nexus2FanCurvePlan PlanFixture(string fixture, Hw? hw = null) =>
        Nexus2FanCurvePlanner.Build(N2.FromFixture(fixture), (hw ?? Hw.Y70()).Env());

    // ── Captures ──

    [Fact]
    public void The_final_2_17_capture_imports_four_curves_and_skips_the_mixed_one()
    {
        var plan = PlanFixture("n2-217-final.json");
        Assert.Null(plan.Note);
        Assert.Equal(1, plan.OtherProfiles);
        Assert.Equal(new[] { "Default", "CPU Chassis", "Flat #1", "Linear #1", "GPU+CPU Mix" }, plan.Curves.Select(c => c.Name).ToArray());
        Assert.Equal(new[] { true, true, true, true, false }, plan.Curves.Select(c => c.Import).ToArray());
        Assert.Equal("mixedCurve", plan.Curves[4].Reason);
        Assert.Equal(4, plan.ImportableCount);
        Assert.All(plan.Curves.Take(4), c => Assert.False(c.MinimumRaised));
    }

    [Fact]
    public void The_final_capture_curves_carry_the_baked_speeds_and_resolved_channels()
    {
        var plan = PlanFixture("n2-217-final.json");

        var gpu = plan.Curves[0].Document!;
        Assert.Equal("/gpu-nvidia/0/temperature/0", gpu.Input.Id);
        Assert.Equal("/gpu-nvidia/0/control/0", Assert.Single(gpu.Outputs).Id);
        // Both the per-curve and the global modifier are baked in.
        Assert.Equal(new[] { 40.19, 41.04, 45.32, 53.87, 72.68, 85.5 }, gpu.Graph!.Points.Select(p => p.Speed).ToArray());

        Assert.Equal($"smarthub:{Hw.SmartHub}:fan0", Assert.Single(plan.Curves[1].Document!.Outputs).Id);

        Assert.Equal(57, plan.Curves[2].Document!.Flat!.Speed);
        Assert.Equal(new[] { (30.0, 42.75), (75.0, 95.0) }, plan.Curves[3].Document!.Graph!.Points.Select(p => (p.Temp, p.Speed)).ToArray());
    }

    [Fact]
    public void The_easy_mode_capture_imports_nothing()
    {
        var plan = PlanFixture("n2-217-easy-mode.json");
        Assert.Equal("recommendedMode", plan.Note);
        Assert.Empty(plan.Curves);
    }

    [Fact]
    public void The_2_16_capture_ignores_the_unwired_seeded_curves_and_keeps_the_pump_curve_out()
    {
        var plan = PlanFixture("n2-216-seeded.json");
        var performance = Assert.Single(plan.Curves);
        Assert.Equal("Performance", performance.Name);
        Assert.False(performance.Import);
        Assert.Equal("pumpKept", Assert.Single(performance.Targets).Reason);
        Assert.Equal(0, plan.ImportableCount);
    }

    [Fact]
    public void The_2_16_capture_pump_curve_stays_out_even_with_the_cooler_connected()
    {
        var plan = PlanFixture("n2-216-seeded.json", Hw.Y70().AddQSeries());
        var performance = Assert.Single(plan.Curves);
        Assert.Equal("noTargets", performance.Reason);
        Assert.Equal("pumpKept", Assert.Single(performance.Targets).Reason);
    }

    [Theory]
    [InlineData("n2-217-final.json", "Default")]
    [InlineData("n2-217-quiet-active.json", "Default")]
    public void The_multi_profile_captures_read_the_active_profile_in_either_array_order(string fixture, string firstCurve)
    {
        var plan = PlanFixture(fixture);
        Assert.Equal(firstCurve, plan.Curves[0].Name);
        Assert.Equal(1, plan.OtherProfiles);
    }

    [Fact]
    public void The_quiet_profile_capture_uses_its_own_modifiers_and_speeds()
    {
        var plan = PlanFixture("n2-217-quiet-active.json");
        Assert.Equal(new[] { "Default", "Graph #2", "Flat #1", "Linear #1", "Mixed #1" }, plan.Curves.Select(c => c.Name).ToArray());
        // The global modifier is neutral here, but the copied Default curve keeps its per-curve one.
        Assert.Equal(new[] { 42.3, 43.2, 47.7, 56.7, 76.5, 90.0 }, plan.Curves[0].Document!.Graph!.Points.Select(p => p.Speed).ToArray());
        Assert.Equal(60, plan.Curves[2].Document!.Flat!.Speed);
        Assert.Equal("mixedCurve", plan.Curves[4].Reason);
    }

    [Fact]
    public void The_captured_curves_add_no_lint_hazard_under_the_real_lint()
    {
        var hw = Hw.Y70();
        var plan = Nexus2FanCurvePlanner.Build(N2.FromFixture("n2-217-final.json"), hw.Env());
        var composed = Nexus2FanCurveApplier.Compose(Array.Empty<CurveDocument>(), plan.Curves.Where(c => c.Import).Select(c => c.Document!).ToList());
        var hazards = CoolingConfigLint.Analyze(new LintInput { Curves = composed, Channels = hw.Channels, Sources = hw.Sources });
        Assert.Empty(hazards);
    }

    // ── Apply ──

    private static List<CurveDocument> Imported(Hw hw, JsonElement root) =>
        Nexus2FanCurvePlanner.Build(root, hw.Env()).Curves.Where(c => c.Import).Select(c => c.Document!).ToList();

    /// <summary>What the curve computes: everything but its output list, which is the binding rather than the behavior.</summary>
    private static string Fingerprint(CurveDocument? curve)
    {
        if (curve is null)
        {
            return "none";
        }
        var behavior = CoolingConfigLint.CloneCurve(curve);
        behavior.Outputs = new List<CurveOutputDocument>();
        return Wire(new List<CurveDocument> { behavior });
    }

    private static string Wire(IEnumerable<CurveDocument> curves) => JsonSerializer.Serialize(
        new SetCurvesBody { Curves = curves.Select(CurveWireMapper.ToWire).ToList() },
        Nexus.Service.Serialization.AppJsonContext.Default.SetCurvesBody);

    private static Dictionary<string, string> EffectiveByChannel(IEnumerable<CurveDocument> curves, IEnumerable<string> channels)
    {
        var list = curves.ToList();
        return channels.ToDictionary(id => id, id =>
        {
            var owners = list.Where(c => c.Outputs.Any(o => o.Id == id)).ToList();
            Assert.True(owners.Count <= 1, $"{id} is bound to {owners.Count} curves");
            return owners.Count == 0 ? "none" : Fingerprint(owners[0]);
        }, StringComparer.Ordinal);
    }

    /// <summary>A machine under a given mode: preset curves own fans, one user curve owns a GPU fan, one fan is on BIOS.</summary>
    private static CoolingSettings Settings(Hw hw, string mode)
    {
        var cooling = new CoolingSettings { ActivePreset = mode };
        var presetFans = hw.Channels.Where(c => c.Id.StartsWith("smarthub:", StringComparison.Ordinal)).Take(3).ToList();
        if (mode is "silent" or "balanced")
        {
            var curve = FanProfiles.BuildPresetCurve(mode, hw.Sources[0]);
            curve.Outputs = presetFans.Select(c => new CurveOutputDocument { Id = c.Id, Type = "Fan" }).ToList();
            cooling.Curves.Add(curve);
        }
        else if (mode == "custom")
        {
            cooling.Curves.Add(new CurveDocument
            {
                Id = "user-1",
                Name = "Mine",
                Type = "Flat",
                Flat = new FlatCurveData { Speed = 55 },
                Outputs = presetFans.Select(c => new CurveOutputDocument { Id = c.Id, Type = "Fan" }).ToList(),
            });
        }
        cooling.Curves.Add(new CurveDocument
        {
            Id = "user-gpu",
            Name = "GPU mine",
            Type = "Graph",
            Input = new CurveInputDocument { Id = "/gpu-nvidia/0/temperature/0", Type = "Temperature", Device = "GPU" },
            Graph = new GraphCurveData { Points = new List<Nexus.Service.Persistence.GraphPoint> { new() { Temp = 30, Speed = 40 }, new() { Temp = 80, Speed = 100 } } },
            Outputs = new List<CurveOutputDocument> { new() { Id = "/gpu-nvidia/0/control/1", Type = "Fan" } },
        });
        cooling.ManualSpeeds[$"minihub:{Hw.MiniHub}:port1"] = 70;
        cooling.FanOffsets[$"smarthub:{Hw.SmartHub}:fan0"] = -10;
        return cooling;
    }

    [Theory]
    [InlineData("off")]
    [InlineData("silent")]
    [InlineData("balanced")]
    [InlineData("custom")]
    public void Importing_never_changes_the_effective_curve_of_a_channel_it_did_not_import(string mode)
    {
        var hw = Hw.Y70();
        var cooling = Settings(hw, mode);
        hw.Curves.AddRange(cooling.Curves);
        // Claims the first GPU fan, two Smart Hub fans and both Mini Hub ports; the second GPU fan keeps its user curve and the rest stay as they are.
        var root = N2.Single(100,
            N2.Graph("A", new (double, double)[] { (0, 30), (80, 90) }, new[] { N2.Gpu(1), N2.Pwm(1) }),
            N2.Flat("B", 60, new[] { N2.Pwm(2), N2.Mini(0), N2.Mini(1) }));
        var imported = Imported(hw, root);
        Assert.Equal(2, imported.Count);

        var claimed = imported.SelectMany(c => c.Outputs.Select(o => o.Id)).ToHashSet();
        var others = hw.Channels.Select(c => c.Id).Where(id => !claimed.Contains(id)).ToList();
        var before = EffectiveByChannel(cooling.Curves, others);

        Nexus2FanCurveApplier.Apply(cooling, imported);

        Assert.Equal(before, EffectiveByChannel(cooling.Curves, others));
        // The imported channels now run exactly their imported curve.
        foreach (var curve in imported)
        {
            foreach (var output in curve.Outputs)
            {
                Assert.Equal(Fingerprint(curve), EffectiveByChannel(cooling.Curves, new[] { output.Id })[output.Id]);
            }
        }
    }

    [Theory]
    [InlineData("off")]
    [InlineData("silent")]
    [InlineData("balanced")]
    [InlineData("custom")]
    public void The_preset_label_agrees_with_the_bindings_so_a_boot_time_reapply_keeps_the_import(string mode)
    {
        var hw = Hw.Y70();
        var store = new InMemoryConfigStore();
        var fans = new RecordingFans(hw);
        var cooling = Settings(hw, mode);
        store.Update(s => s.Cooling = cooling);

        var imported = Imported(hw, N2.Single(100, N2.Graph("A", new (double, double)[] { (0, 30), (80, 90) }, new[] { N2.Pwm(1), N2.Pwm(2) })));
        store.Update(s => Nexus2FanCurveApplier.Apply(s.Cooling, imported));
        var derived = FanProfiles.DerivePresetFromCurves(store, fans);
        store.Update(s => s.Cooling.ActivePreset = derived);

        // AutoRestoreOnStart and a profile switch re-apply silent, balanced, turbo and max, never custom or off.
        var preset = store.Load().Cooling.ActivePreset;
        if (preset is "silent" or "balanced" or "turbo" or "max")
        {
            FanProfiles.Apply(preset, fans, store);
        }
        var owner = store.Load().Cooling.Curves.First(c => c.Outputs.Any(o => o.Id == $"smarthub:{Hw.SmartHub}:fan0"));
        Assert.StartsWith("n2-", owner.Id);
        Assert.Equal("custom", store.Load().Cooling.ActivePreset);
    }

    [Fact]
    public void Importing_twice_changes_nothing_the_second_time()
    {
        var hw = Hw.Y70();
        var cooling = Settings(hw, "custom");
        var imported = Imported(hw, N2.FromFixture("n2-217-final.json"));
        Nexus2FanCurveApplier.Apply(cooling, imported);
        var first = Wire(cooling.Curves);
        var assignments = cooling.CustomFanCurveAssignments.OrderBy(kv => kv.Key).ToList();

        hw.Curves.Clear();
        hw.Curves.AddRange(cooling.Curves);
        Nexus2FanCurveApplier.Apply(cooling, Imported(hw, N2.FromFixture("n2-217-final.json")));

        Assert.Equal(first, Wire(cooling.Curves));
        Assert.Equal(assignments, cooling.CustomFanCurveAssignments.OrderBy(kv => kv.Key).ToList());
        Assert.Equal(4, cooling.Curves.Count(c => c.Id.StartsWith("n2-", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_re_import_replaces_only_n2_curves_and_leaves_user_curves_alone()
    {
        var hw = Hw.Y70();
        var cooling = Settings(hw, "off");
        var user = Fingerprint(cooling.Curves.Single(c => c.Id == "user-gpu"));
        Nexus2FanCurveApplier.Apply(cooling, Imported(hw, N2.Single(100, N2.Flat("B", 60, new[] { N2.Pwm(2) }))));

        hw.Curves.Clear();
        hw.Curves.AddRange(cooling.Curves);
        Nexus2FanCurveApplier.Apply(cooling, Imported(hw, N2.Single(100, N2.Flat("B", 70, new[] { N2.Pwm(2) }))));

        Assert.Equal(user, Fingerprint(cooling.Curves.Single(c => c.Id == "user-gpu")));
        var n2 = Assert.Single(cooling.Curves, c => c.Id.StartsWith("n2-", StringComparison.Ordinal));
        Assert.Equal(70, n2.Flat!.Speed);
    }

    [Fact]
    public void A_channel_an_earlier_import_drove_keeps_running_when_a_re_import_no_longer_claims_it()
    {
        var hw = Hw.Y70();
        var cooling = new CoolingSettings();
        Nexus2FanCurveApplier.Apply(cooling, Imported(hw, N2.Single(100, N2.Flat("B", 60, new[] { N2.Pwm(1), N2.Pwm(2) }))));
        // The second run only claims the first port, as if the second no longer resolved.
        hw.Curves.Clear();
        hw.Curves.AddRange(cooling.Curves);
        Nexus2FanCurveApplier.Apply(cooling, Imported(hw, N2.Single(100, N2.Flat("B", 60, new[] { N2.Pwm(1) }))));

        var owner = Assert.Single(cooling.Curves, c => c.Outputs.Any(o => o.Id == $"smarthub:{Hw.SmartHub}:fan1"));
        Assert.Equal(60, owner.Flat!.Speed);
        Assert.Equal("n2-b-kept", owner.Id);
    }

    [Fact]
    public void An_imported_channel_loses_the_manual_duty_and_offset_that_would_fight_its_curve()
    {
        var hw = Hw.Y70();
        var cooling = Settings(hw, "custom");
        cooling.CustomManualSpeeds[$"minihub:{Hw.MiniHub}:port1"] = 70;
        Nexus2FanCurveApplier.Apply(cooling, Imported(hw, N2.Single(100, N2.Flat("B", 60, new[] { N2.Pwm(1), N2.Mini(0), N2.Mini(1) }))));

        Assert.DoesNotContain($"minihub:{Hw.MiniHub}:port1", cooling.ManualSpeeds.Keys);
        Assert.DoesNotContain($"minihub:{Hw.MiniHub}:port1", cooling.CustomManualSpeeds.Keys);
        Assert.DoesNotContain($"smarthub:{Hw.SmartHub}:fan0", cooling.FanOffsets.Keys);
    }

    [Fact]
    public void An_imported_channel_is_recorded_for_the_custom_mode_restore()
    {
        var hw = Hw.Y70();
        var cooling = new CoolingSettings();
        Nexus2FanCurveApplier.Apply(cooling, Imported(hw, N2.Single(100, N2.Flat("B", 60, new[] { N2.Pwm(1) }))));
        Assert.StartsWith("n2-", cooling.CustomFanCurveAssignments[$"smarthub:{Hw.SmartHub}:fan0"]);
    }

    [Fact]
    public void Applying_leaves_the_guard_heal_state_alone()
    {
        var hw = Hw.Y70();
        var snapshot = new List<CurveDocument>();
        var cooling = new CoolingSettings { HealSnapshot = snapshot, HealedAtUtcMs = 5 };
        Nexus2FanCurveApplier.Apply(cooling, Imported(hw, N2.Single(100, N2.Flat("B", 60, new[] { N2.Pwm(1) }))));
        Assert.Same(snapshot, cooling.HealSnapshot);
        Assert.Equal(5, cooling.HealedAtUtcMs);
    }

    // ── Service: preview, apply and eligibility over the real service wiring ──

    [Fact]
    public void Preview_reports_the_category_in_the_contract_shape()
    {
        var (service, _, _) = FanCurveServices.Service("n2-217-final.json");
        var cat = service.BuildFanCurvesCategory(JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Nexus2", "cooling", "n2-217-final.json"))).RootElement);

        Assert.Equal("fanCurves", cat.Id);
        Assert.True(cat.Available);
        Assert.Equal(4, cat.Count);
        Assert.Equal(1, cat.OtherProfiles);
        Assert.Null(cat.FanCurvesNote);
        Assert.Equal(5, cat.FanCurves!.Count);
        var mixed = cat.FanCurves[4];
        Assert.Equal(("GPU+CPU Mix", "mixed", "skip", "mixedCurve"), (mixed.Name, mixed.Kind, mixed.Status, mixed.Reason));
        var flat = cat.FanCurves[2];
        Assert.Equal(("import", (string?)null), (flat.Status, flat.Reason));
        Assert.Equal(new[] { "Port 1 Fan", "Port 2 Fans (3)" }, flat.Targets.Select(t => t.Label).ToArray());
    }

    [Fact]
    public void Preview_serializes_with_the_camel_case_contract_names()
    {
        var (service, _, _) = FanCurveServices.Service("n2-217-easy-mode.json");
        var cat = service.BuildFanCurvesCategory(JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Nexus2", "cooling", "n2-217-easy-mode.json"))).RootElement);
        var json = JsonSerializer.Serialize(cat, Nexus.Service.Serialization.AppJsonContext.Default.Nexus2PreviewCategoryDto);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("fanCurves", doc.RootElement.GetProperty("id").GetString());
        Assert.True(doc.RootElement.GetProperty("available").GetBoolean());
        Assert.Equal("recommendedMode", doc.RootElement.GetProperty("fanCurvesNote").GetString());
        Assert.Equal("balanced", doc.RootElement.GetProperty("recommendedFrom").GetString());
        Assert.Equal("balanced", doc.RootElement.GetProperty("recommendedPreset").GetString());
        Assert.False(doc.RootElement.TryGetProperty("fanCurves", out _));
    }

    [Fact]
    public void Apply_writes_the_curves_into_the_store_reports_the_count_and_sets_a_matching_preset()
    {
        var (service, store, hw) = FanCurveServices.Service("n2-217-final.json");
        var root = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Nexus2", "cooling", "n2-217-final.json"))).RootElement;

        var result = service.ApplyFanCurves(root);

        Assert.Equal(("fanCurves", "applied", 4), (result.Id, result.Status, result.Count));
        var cooling = store.Load().Cooling;
        Assert.Equal(4, cooling.Curves.Count(c => c.Id.StartsWith("n2-", StringComparison.Ordinal)));
        Assert.Equal("custom", cooling.ActivePreset);
        Assert.Equal(1.0, cooling.GlobalSpeedModifier);
        Assert.Equal(3, cooling.Curves.SelectMany(c => c.Outputs).Count(o => o.Id.StartsWith("smarthub:", StringComparison.Ordinal)));
    }

    [Fact]
    public void Apply_skips_when_nothing_imports()
    {
        var (service, store, _) = FanCurveServices.Service("n2-216-seeded.json");
        var root = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Nexus2", "cooling", "n2-216-seeded.json"))).RootElement;
        var result = service.ApplyFanCurves(root);
        Assert.Equal(("skipped", "no-importable-curves"), (result.Status, result.Detail));
        Assert.Empty(store.Load().Cooling.Curves);
    }

    // ── Eligibility ──

    [Fact]
    public void The_offer_is_pending_when_a_curve_would_import_without_a_panel_record()
    {
        var (service, _, _) = FanCurveServices.Service("n2-217-final.json");
        Assert.True(service.HasImportableFanCurves());
    }

    [Theory]
    [InlineData("n2-216-seeded.json")]
    public void The_offer_stays_off_when_no_curve_would_import(string fixture)
    {
        var (service, _, _) = FanCurveServices.Service(fixture);
        Assert.False(service.HasImportableFanCurves());
    }

    [Fact]
    public void The_check_never_throws_when_the_cooling_provider_is_missing()
    {
        var reader = new FakeNexus2ConfigReader { ConfigText = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Nexus2", "cooling", "n2-217-final.json")) };
        var service = new Nexus2MigrationService(reader, null!, null!, null!, new InMemoryConfigStore(), null!, new ServiceCollection().BuildServiceProvider());
        Assert.False(service.HasImportableFanCurves());
    }
}
