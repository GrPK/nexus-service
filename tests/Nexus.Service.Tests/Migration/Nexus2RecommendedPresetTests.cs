using System.Text.Json;
using Nexus.Service.Cooling;
using Nexus.Service.Migration;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Migration;

public sealed class Nexus2RecommendedPresetTests
{
    private const string Fixture = "n2-217-easy-mode.json";

    private static string Text(string selected)
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Nexus2", "cooling", Fixture));
        const string original = "\"selected\": \"balanced\"";
        Assert.Contains(original, text);
        return text.Replace(original, $"\"selected\": \"{selected}\"");
    }

    private static JsonElement Root(string text) => JsonDocument.Parse(text).RootElement;

    private static (Nexus2MigrationService Service, InMemoryConfigStore Store, Hw Hw) Setup(string selected, Action<InMemoryConfigStore, Hw>? arrange = null)
    {
        var (service, store, hw) = FanCurveServices.Service(Fixture, configText: Text(selected));
        arrange?.Invoke(store, hw);
        return (service, store, hw);
    }

    [Theory]
    [InlineData("silent", "silent")]
    [InlineData("balanced", "balanced")]
    [InlineData("performance", "turbo")]
    public void Each_recommended_value_maps_and_previews_as_available(string from, string to)
    {
        var (service, _, _) = Setup(from);
        var cat = service.BuildFanCurvesCategory(Root(Text(from)));
        Assert.True(cat.Available);
        Assert.Equal(0, cat.Count);
        Assert.Equal("recommendedMode", cat.FanCurvesNote);
        Assert.Equal((from, to), (cat.RecommendedFrom, cat.RecommendedPreset));
        Assert.Null(cat.FanCurves);
    }

    [Theory]
    [InlineData("silent", "silent")]
    [InlineData("balanced", "balanced")]
    [InlineData("performance", "turbo")]
    public void Apply_activates_the_mapped_preset_and_reports_it(string from, string to)
    {
        var (service, store, _) = Setup(from);
        var result = service.ApplyFanCurves(Root(Text(from)));
        Assert.Equal(("fanCurves", "applied", to), (result.Id, result.Status, result.Preset));
        Assert.Equal(to, store.Load().Cooling.ActivePreset);
        Assert.Contains(store.Load().Cooling.Curves, c => c.Preset == to && c.Outputs.Count > 0);
    }

    [Theory]
    [InlineData("Silent")]
    [InlineData("max")]
    [InlineData("turbo")]
    [InlineData("")]
    public void An_unmappable_value_skips_with_the_existing_note_and_no_preset_fields(string selected)
    {
        var (service, store, _) = Setup(selected);
        var cat = service.BuildFanCurvesCategory(Root(Text(selected)));
        Assert.False(cat.Available);
        Assert.Equal("recommendedMode", cat.FanCurvesNote);
        Assert.Null(cat.RecommendedFrom);
        Assert.Null(cat.RecommendedPreset);
        var result = service.ApplyFanCurves(Root(Text(selected)));
        Assert.Equal(("skipped", "recommendedMode", null), (result.Status, result.Detail, result.Preset));
        Assert.Equal("off", store.Load().Cooling.ActivePreset);
        Assert.Empty(store.Load().Cooling.Curves);
    }

    [Fact]
    public void A_missing_value_skips()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Nexus2", "cooling", Fixture)).Replace("\"selected\": \"balanced\"", "\"other\": 1");
        var (service, store, _) = FanCurveServices.Service(Fixture, configText: text);
        var result = service.ApplyFanCurves(Root(text));
        Assert.Equal(("skipped", "recommendedMode"), (result.Status, result.Detail));
        Assert.Equal("off", store.Load().Cooling.ActivePreset);
    }

    private static void Seed(CoolingSettings c) => FanProfiles.SeedDefaultPresetCurves(new RecordingFans(Hw.Y70()), new SingleStore(c));

    private static void Customize(string how, CoolingSettings c)
    {
        switch (how)
        {
            case "curve":
                c.Curves.Add(new CurveDocument { Id = "mine", Name = "Mine", Type = "Flat", Flat = new FlatCurveData { Speed = 50 } });
                break;
            case "manual": c.ManualSpeeds["x"] = 40; break;
            case "customManual": c.CustomManualSpeeds["x"] = 40; break;
            case "offset": c.FanOffsets["x"] = 5; break;
            case "pin": c.FanPinOverrides["x"] = true; break;
            case "uncontrolled": c.UncontrolledFanChannels.Add("x"); break;
            case "userPreset": c.Presets.Add(new CoolingPreset { Id = "p", Name = "P" }); break;
            case "activePresetId": c.ActivePresetId = "p"; break;
            case "otherMode": c.ActivePreset = "turbo"; break;
            case "customMode": c.ActivePreset = "custom"; break;
            case "modifier": c.GlobalSpeedModifier = 0.8; break;
            case "customAssignment": c.CustomFanCurveAssignments["x"] = "y"; break;
            case "healedChannels": c.HealedChannels.Add(new HealedChannelRecord { Id = "x", Name = "X" }); break;
            case "healSnapshot": c.HealSnapshot = new List<CurveDocument>(); break;
            case "fanLockOverrides": c.FanLockOverrides = new Dictionary<string, bool> { ["x"] = true }; break;
            case "duplicatePresetCurve":
                Seed(c);
                c.Curves.Add(CoolingConfigLint.CloneCurve(c.Curves.First(k => k.Preset == "silent")));
                c.Curves[^1].Id = "dup";
                break;
            case "outputsWhileOff":
                Seed(c);
                c.Curves.First(k => k.Preset == "silent").Outputs.Add(new CurveOutputDocument { Id = "x", Type = "Fan" });
                break;
            case "outputsOnInactivePresetCurve":
                Seed(c);
                c.ActivePreset = "silent";
                c.Curves.First(k => k.Preset == "balanced").Outputs.Add(new CurveOutputDocument { Id = "x", Type = "Fan" });
                break;
            case "editedPresetCurve":
                Seed(c);
                c.Curves.First(k => k.Preset == "silent").Graph!.Points[0].Speed += 7;
                break;
            default: throw new ArgumentException(how);
        }
    }

    [Theory]
    [InlineData("curve")]
    [InlineData("manual")]
    [InlineData("customManual")]
    [InlineData("offset")]
    [InlineData("pin")]
    [InlineData("uncontrolled")]
    [InlineData("userPreset")]
    [InlineData("activePresetId")]
    [InlineData("otherMode")]
    [InlineData("customMode")]
    [InlineData("modifier")]
    [InlineData("customAssignment")]
    [InlineData("editedPresetCurve")]
    [InlineData("healedChannels")]
    [InlineData("healSnapshot")]
    [InlineData("fanLockOverrides")]
    [InlineData("duplicatePresetCurve")]
    [InlineData("outputsWhileOff")]
    [InlineData("outputsOnInactivePresetCurve")]
    public void Customized_nexus3_cooling_skips_and_changes_nothing(string how)
    {
        var (service, store, _) = Setup("silent", (s, _) => s.Update(x => Customize(how, x.Cooling)));
        var before = System.Text.Json.JsonSerializer.Serialize(store.Load().Cooling, Nexus.Service.Serialization.PersistenceJsonContext.Default.CoolingSettings);

        var cat = service.BuildFanCurvesCategory(Root(Text("silent")));
        Assert.False(cat.Available);
        Assert.Equal("nexus3CoolingCustomized", cat.FanCurvesNote);
        Assert.Equal(("silent", "silent"), (cat.RecommendedFrom, cat.RecommendedPreset));
        Assert.False(service.HasImportableFanCurves());

        var result = service.ApplyFanCurves(Root(Text("silent")));
        Assert.Equal(("skipped", "nexus3CoolingCustomized", null), (result.Status, result.Detail, result.Preset));
        var after = System.Text.Json.JsonSerializer.Serialize(store.Load().Cooling, Nexus.Service.Serialization.PersistenceJsonContext.Default.CoolingSettings);
        Assert.Equal(before, after);
    }

    [Fact]
    public void A_seeded_untouched_install_still_applies()
    {
        var (service, store, _) = Setup("performance", (s, hw) => FanProfiles.SeedDefaultPresetCurves(new RecordingFans(hw), s));
        Assert.Equal(4, store.Load().Cooling.Curves.Count);
        var result = service.ApplyFanCurves(Root(Text("performance")));
        Assert.Equal(("applied", "turbo"), (result.Status, result.Preset));
        Assert.Equal(4, store.Load().Cooling.Curves.Count);
    }

    [Fact]
    public void The_cooling_gate_off_keeps_the_existing_note()
    {
        var (service, store, _) = Setup("silent", (s, _) => s.Update(x => x.Features.Cooling = false));
        var cat = service.BuildFanCurvesCategory(Root(Text("silent")));
        Assert.False(cat.Available);
        Assert.Equal("coolingDisabled", cat.FanCurvesNote);
        Assert.Null(cat.RecommendedPreset);
        Assert.False(service.HasImportableFanCurves());
        var result = service.ApplyFanCurves(Root(Text("silent")));
        Assert.Equal(("skipped", "coolingDisabled"), (result.Status, result.Detail));
        Assert.Equal("off", store.Load().Cooling.ActivePreset);
    }

    [Fact]
    public void Activation_leaves_pinned_channels_off_the_preset_curve()
    {
        var (service, store, hw) = Setup("silent", (_, h) => h.AddQSeries());
        var pump = hw.Channels.Single(c => c.Kind == FanKinds.Pump).Id;
        var gpu = hw.Channels.Where(c => c.IsGpu).Select(c => c.Id).ToList();
        service.ApplyFanCurves(Root(Text("silent")));

        var owned = store.Load().Cooling.Curves.Single(c => c.Preset == "silent").Outputs.Select(o => o.Id).ToHashSet();
        Assert.DoesNotContain(pump, owned);
        Assert.All(gpu, g => Assert.DoesNotContain(g, owned));
        Assert.Contains($"smarthub:{Hw.SmartHub}:fan0", owned);
        Assert.Empty(store.Load().Cooling.FanPinOverrides);
    }

    /// <summary>The import's activation and a direct FanProfiles.Apply, each on its own hardware and store.</summary>
    private static void AssertMatchesDirectApply(string selected, string preset, Action<Hw>? arrange = null)
    {
        RecordingFans? importFans = null;
        var (service, store, hw) = FanCurveServices.Service(Fixture, configText: Text(selected), configureFans: f => importFans = f);
        arrange?.Invoke(hw);
        service.ApplyFanCurves(Root(Text(selected)));

        RecordingFans? directFans = null;
        var (_, direct, directHw) = FanCurveServices.Service(Fixture, configText: Text(selected), configureFans: f => directFans = f);
        arrange?.Invoke(directHw);
        FanProfiles.Apply(preset, directFans!, direct);

        string Shape(InMemoryConfigStore s, RecordingFans f) => string.Join("|", s.Load().Cooling.Curves.OrderBy(c => c.Id).Select(c => $"{c.Preset}:{string.Join(",", c.Outputs.Select(o => o.Id).OrderBy(x => x))}"))
            + "|" + s.Load().Cooling.ActivePreset
            + "|" + string.Join(",", s.Load().Cooling.ManualSpeeds.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}"))
            + "|" + string.Join(",", s.Load().Cooling.FanOffsets.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}"))
            + "|" + string.Join(",", f.Calls);
        Assert.Equal(Shape(direct, directFans!), Shape(store, importFans!));
        Assert.Equal(preset, store.Load().Cooling.ActivePreset);
    }

    [Fact]
    public void Activation_runs_the_ui_preset_path_so_it_matches_a_direct_profile_apply() =>
        AssertMatchesDirectApply("performance", "turbo");

    [Fact]
    public void Activation_with_cooler_hubs_present_equals_a_direct_profile_apply() =>
        AssertMatchesDirectApply("silent", "silent", h => h.AddQSeries().AddNp50());

    [Fact]
    public void A_change_landing_between_the_plan_and_the_write_aborts_the_activation()
    {
        InMemoryConfigStore? late = null;
        // The first source read is the plan's; it arms a second hook that fires on FanProfiles.Apply's own read, after the plan and before its store update.
        var (service, store, _) = FanCurveServices.Service(Fixture, configText: Text("silent"),
            configureFans: f => f.OnFirstSourceRead = () => f.OnFirstSourceRead = () => late!.Update(s => s.Cooling.FanOffsets["x"] = 5));
        late = store;

        var result = service.ApplyFanCurves(Root(Text("silent")));

        Assert.Equal(("skipped", "nexus3CoolingCustomized"), (result.Status, result.Detail));
        Assert.Equal("off", store.Load().Cooling.ActivePreset);
        Assert.Equal(5, store.Load().Cooling.FanOffsets["x"]);
        Assert.DoesNotContain(store.Load().Cooling.Curves, c => c.Outputs.Count > 0);
    }

    private static string EasyModeWithOneStoredCurve()
    {
        var points = new[] { (0.0, 30.0), (50.0, 50.0), (80.0, 80.0), (100.0, 100.0) };
        var root = N2.Root(N2.Profile("P", true, 100, easy: true, N2.Graph("Stored", points, new[] { N2.Pwm(1) }, input: N2.Input())));
        return root.GetRawText().Replace("\"selected\":\"balanced\"", "\"selected\":\"silent\"");
    }

    [Fact]
    public void With_a_cpu_but_no_gpu_temperature_the_stored_curves_import_as_easy_mode_is_off()
    {
        var text = EasyModeWithOneStoredCurve();
        var (service, store, _) = FanCurveServices.Service(Fixture, configText: text);
        var gpuless = FanCurveServices.Service(Fixture, configText: text, configureFans: null);
        gpuless.Hw.Sources.RemoveAll(t => t.Category == "GPU");

        var cat = gpuless.Service.BuildFanCurvesCategory(Root(text));
        Assert.NotEqual("recommendedMode", cat.FanCurvesNote);
        Assert.Null(cat.RecommendedPreset);
        Assert.Equal(1, cat.Count);

        var result = gpuless.Service.ApplyFanCurves(Root(text));
        Assert.Equal(("applied", 1, null), (result.Status, result.Count, result.Preset));
        var imported = Assert.Single(gpuless.Store.Load().Cooling.Curves, c => c.Id.StartsWith("n2-", StringComparison.Ordinal));
        Assert.Equal($"smarthub:{Hw.SmartHub}:fan0", Assert.Single(imported.Outputs).Id);

        // With both sources present the same file maps to the preset instead, and applying activates it.
        Assert.Equal("silent", service.BuildFanCurvesCategory(Root(text)).RecommendedPreset);
        var presetResult = service.ApplyFanCurves(Root(text));
        Assert.Equal(("applied", "silent"), (presetResult.Status, presetResult.Preset));
        Assert.Equal("silent", store.Load().Cooling.ActivePreset);
    }

    [Fact]
    public void With_neither_a_cpu_nor_a_gpu_temperature_the_preset_mapping_is_kept_and_applies()
    {
        var (service, store, _) = Setup("silent", (_, h) => h.Sources.RemoveAll(t => t.Category is "CPU" or "GPU"));
        var cat = service.BuildFanCurvesCategory(Root(Text("silent")));
        Assert.Equal("recommendedMode", cat.FanCurvesNote);
        Assert.Equal("silent", cat.RecommendedPreset);
        var result = service.ApplyFanCurves(Root(Text("silent")));
        Assert.Equal(("applied", "silent"), (result.Status, result.Preset));
        Assert.Equal("silent", store.Load().Cooling.ActivePreset);
    }

    [Fact]
    public void Without_a_cpu_temperature_the_preset_mapping_is_kept()
    {
        var (service, _, _) = Setup("silent", (_, h) => h.Sources.RemoveAll(t => t.Category == "CPU"));
        var cat = service.BuildFanCurvesCategory(Root(Text("silent")));
        Assert.Equal("recommendedMode", cat.FanCurvesNote);
        Assert.Equal("silent", cat.RecommendedPreset);
    }

    [Fact]
    public void A_guard_abort_with_cooling_disabled_reports_cooling_disabled()
    {
        InMemoryConfigStore? late = null;
        var (service, store, _) = FanCurveServices.Service(Fixture, configText: Text("silent"),
            configureFans: f => f.OnFirstSourceRead = () => f.OnFirstSourceRead = () => late!.Update(s => s.Features.Cooling = false));
        late = store;
        var result = service.ApplyFanCurves(Root(Text("silent")));
        Assert.Equal(("skipped", "coolingDisabled"), (result.Status, result.Detail));
    }

    [Fact]
    public void Reimporting_is_idempotent()
    {
        var (service, store, _) = Setup("silent");
        service.ApplyFanCurves(Root(Text("silent")));
        var first = System.Text.Json.JsonSerializer.Serialize(store.Load().Cooling, Nexus.Service.Serialization.PersistenceJsonContext.Default.CoolingSettings);

        var cat = service.BuildFanCurvesCategory(Root(Text("silent")));
        Assert.True(cat.Available);
        var result = service.ApplyFanCurves(Root(Text("silent")));
        Assert.Equal(("applied", "silent"), (result.Status, result.Preset));
        Assert.Equal(first, System.Text.Json.JsonSerializer.Serialize(store.Load().Cooling, Nexus.Service.Serialization.PersistenceJsonContext.Default.CoolingSettings));
    }

    [Fact]
    public void A_different_preset_chosen_after_the_import_is_not_overridden()
    {
        var (service, store, _) = Setup("silent");
        service.ApplyFanCurves(Root(Text("silent")));
        store.Update(s => s.Cooling.ActivePreset = "turbo");
        Assert.Equal("nexus3CoolingCustomized", service.BuildFanCurvesCategory(Root(Text("silent"))).FanCurvesNote);
    }

    [Fact]
    public void Pending_counts_a_mappable_untouched_recommended_mode_only()
    {
        Assert.True(Setup("balanced").Service.HasImportableFanCurves());
        Assert.False(Setup("bogus").Service.HasImportableFanCurves());
        Assert.False(Setup("balanced", (s, _) => s.Update(x => x.Cooling.FanOffsets["a"] = 1)).Service.HasImportableFanCurves());
    }

    [Fact]
    public void Apply_never_writes_stored_curves_in_recommended_mode()
    {
        var (service, store, _) = Setup("silent");
        service.ApplyFanCurves(Root(Text("silent")));
        Assert.DoesNotContain(store.Load().Cooling.Curves, c => c.Id.StartsWith("n2-", StringComparison.Ordinal));
    }

    private sealed class SingleStore : IConfigStore
    {
        private readonly NexusSettings _settings = new();
        public SingleStore(CoolingSettings cooling) => _settings.Cooling = cooling;
        public string SettingsPath => ":memory:";
        public NexusSettings Load() => _settings;
        public void Update(Action<NexusSettings> mutator) => mutator(_settings);
        public void FlushNow() { }
        public void Reload() { }
        public event Action? OnChanged { add { } remove { } }
    }
}
