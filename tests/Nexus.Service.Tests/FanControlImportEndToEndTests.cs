using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Cooling;
using Nexus.Service.Lifecycle;
using Nexus.Service.Migration.FanControl;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// Import a FanControl configuration and then run the curve engine over what it
/// wrote, so the whole chain is covered in one place: parse, match, apply into a
/// cooling preset, activate it, and fans actually driven at the duty the
/// imported curve asks for. The pieces are unit-tested separately; this is the
/// seam between them, and it is the part a lab box cannot prove unless that box
/// happens to have FanControl actively driving its fans.
/// </summary>
public class FanControlImportEndToEndTests
{
    private sealed class FakeDetector : IFanControlDetector
    {
        private readonly string _path;

        public FakeDetector(string path) => _path = path;

        public FanControlDetectionResult Detect() => new(
            Detected: true, Running: false, InstallLocation: "C:\\FanControl", Version: "217",
            Configs: new List<FanControlConfigFile> { new(_path, "userConfig", 0, true) },
            AutostartPresent: false);

        public string? ReadConfig(string path) =>
            string.Equals(path, _path, StringComparison.OrdinalIgnoreCase) ? File.ReadAllText(_path) : null;

        public Task<bool> CloseAppAsync() => Task.FromResult(true);

        public bool DisableAutostart() => true;
    }

    private const string Other = "/lpc/other/0/control/9";
    private const string Fan0 = "/lpc/nct6797d/0/control/0";
    private const string Fan1 = "/lpc/nct6797d/0/control/1";
    private const string Fan2 = "/lpc/nct6797d/0/control/2";

    private sealed class FakeFanProvider : IFanControlProvider
    {
        public readonly List<(string Id, int Duty)> Driven = new();
        public readonly Dictionary<string, int> Duty = new();
        public float Temperature = 50f;

        public readonly List<FanChannel> Channels = new()
        {
            new() { Id = "/lpc/nct6797d/0/control/0", Name = "Fan #1" },
            new() { Id = "/lpc/nct6797d/0/control/1", Name = "Fan #2" },
            new() { Id = "/lpc/nct6797d/0/control/2", Name = "Fan #3" },
            new() { Id = "/lpc/nct6797d/0/control/3", Name = "Fan #4" },
            new() { Id = Other, Name = "Other fan" },
        };

        public IReadOnlyList<FanChannel> GetFanChannels()
        {
            foreach (var c in Channels)
            {
                c.DutyPercent = Duty.TryGetValue(c.Id, out var d) ? d : 0;
            }
            return Channels;
        }

        public IReadOnlyList<TemperatureSource> GetTemperatureSources() => new List<TemperatureSource>
        {
            new() { Id = "/amdcpu/0/temperature/2", Name = "Core (Tctl/Tdie)", Category = "CPU", Value = Temperature },
            new() { Id = "/gpu-nvidia/0/temperature/0", Name = "GPU Core", Category = "GPU", Value = 50 },
            new() { Id = "/lpc/nct6797d/0/temperature/1", Name = "Temperature #2", Category = "Motherboard", Value = 50 },
        };

        public float? ReadTemperature(string sensorId) => Temperature;

        public int SetFanSpeed(string channelId, int dutyPercent)
        {
            Duty[channelId] = dutyPercent;
            return dutyPercent;
        }

        public void DriveFanSpeed(string channelId, int dutyPercent)
        {
            Driven.Add((channelId, dutyPercent));
            Duty[channelId] = dutyPercent;
        }

        public void ReleaseFan(string channelId) { }
        public void ReleaseAll() { }

        public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
            IReadOnlyList<string> fanIds, IProgress<FanCalibrationProgress> progress, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());
    }

    private static (FanControlImportService Import, FakeFanProvider Fans, InMemoryConfigStore Store) Build()
    {
        var path = Path.Combine("Fixtures", "FanControl", "all-curve-kinds.json");
        var fans = new FakeFanProvider();
        var store = new InMemoryConfigStore();
        var engine = new CurveEngine(fans, store, new MultiplexHub());
        return (new FanControlImportService(new FakeDetector(path), fans, store, engine), fans, store);
    }

    private static readonly string[] AllCategories =
    {
        FanControlImportService.CategoryCurves,
        FanControlImportService.CategoryCalibration,
        FanControlImportService.CategoryNames,
        FanControlImportService.CategoryOffsets,
        FanControlImportService.CategoryManual,
    };

    [Fact]
    public void Apply_ThenTick_DrivesTheFansTheImportedCurvesOwn()
    {
        var (import, fans, store) = Build();
        var result = import.Apply(null, AllCategories);
        Assert.False(result.Error);

        var engine = new CurveEngine(fans, store, new MultiplexHub());
        fans.Temperature = 55f;
        engine.Tick();

        // The graph curve drives Fan #1: at 55 C its imported points put it
        // between 49.5% and 100%, and the fan must actually receive a write.
        var driven = fans.Driven.Where(d => d.Id == "/lpc/nct6797d/0/control/0").ToList();
        Assert.Single(driven);
        Assert.InRange(driven[0].Duty, 50, 60);
    }

    [Fact]
    public void Apply_ThenTick_RunsTheImportedTriggerCurve()
    {
        var (import, fans, store) = Build();
        import.Apply(null, AllCategories);

        var engine = new CurveEngine(fans, store, new MultiplexHub());
        // Above the imported curve's load threshold (65 C).
        fans.Temperature = 70f;
        engine.Tick();

        Assert.Contains(("/lpc/nct6797d/0/control/1", 80), fans.Driven);
    }

    [Fact]
    public void Apply_ThenTick_RunsTheImportedSyncCurve()
    {
        var (import, fans, store) = Build();
        import.Apply(null, AllCategories);

        var engine = new CurveEngine(fans, store, new MultiplexHub());
        fans.Temperature = 55f;
        engine.Tick();

        // "Follow CPU" mirrors Fan #1 at 90% (proportional, -10), and Fan #1 is
        // driven by the graph curve in this same tick.
        var lead = fans.Driven.First(d => d.Id == "/lpc/nct6797d/0/control/0").Duty;
        var follower = fans.Driven.First(d => d.Id == "/lpc/nct6797d/0/control/3").Duty;
        Assert.Equal((int)Math.Round(lead * 0.9), follower);
    }

    private static (FanControlImportService Import, FakeFanProvider Fans, InMemoryConfigStore Store, CurveEngine Engine, long[] Clock) BuildWithEngine()
    {
        var path = Path.Combine("Fixtures", "FanControl", "all-curve-kinds.json");
        var fans = new FakeFanProvider();
        var store = new InMemoryConfigStore();
        var hub = new MultiplexHub();
        var clock = new long[] { 1_000_000 };
        var engine = new CurveEngine(fans, store, hub, new FeatureGates(store), new ThermalGuardController(fans, store, hub))
        {
            Clock = () => clock[0],
        };
        store.Update(s =>
        {
            s.Cooling.ManualSpeeds[Fan2] = 40;
            s.Cooling.CustomManualSpeeds[Fan2] = 40;
        });
        return (new FanControlImportService(new FakeDetector(path), fans, store, engine), fans, store, engine, clock);
    }

    [Fact]
    public void Apply_ReArmsAnAlreadyReplayedManualFanSoTheNextTickDrivesTheImportedDuty()
    {
        var (import, fans, _, engine, _) = BuildWithEngine();
        engine.Tick();
        Assert.Equal(40, fans.Duty[Fan2]);

        import.Apply(null, AllCategories);
        Assert.Equal(40, fans.Duty[Fan2]);

        engine.Tick();
        Assert.Equal(65, fans.Duty[Fan2]);
    }

    [Fact]
    public void Apply_WithCoolingOff_WritesNothingToTheFan()
    {
        var (import, fans, store, engine, _) = BuildWithEngine();
        engine.Tick();
        store.Update(s => s.Features.Cooling = false);

        import.Apply(null, AllCategories);
        engine.Tick();

        Assert.Equal(40, fans.Duty[Fan2]);
    }

    [Fact]
    public void Apply_DuringAGuardTrip_LeavesTheFanAtTheGuardFloor()
    {
        var (import, fans, _, engine, clock) = BuildWithEngine();
        engine.Tick();
        fans.Temperature = 99f;
        engine.Tick();
        clock[0] += 5000;
        engine.Tick();
        Assert.Equal(100, fans.Duty[Fan2]);

        import.Apply(null, AllCategories);
        engine.Tick();

        Assert.Equal(100, fans.Duty[Fan2]);
    }

    [Fact]
    public void Apply_BetweenAReplaySnapshotAndItsCommit_DoesNotLeaveTheOldDutyMarkedReplayed()
    {
        var (import, fans, _, engine, _) = BuildWithEngine();
        var fired = false;
        engine.ReplaySnapshotTaken = () =>
        {
            if (fired)
            {
                return;
            }
            fired = true;
            import.Apply(null, AllCategories);
        };

        engine.Tick();
        engine.Tick();

        Assert.Equal(65, fans.Duty[Fan2]);
    }

    [Fact]
    public void Apply_ImportsTheManualFanAndTheEngineReplaysIt()
    {
        var (import, fans, store) = Build();
        import.Apply(null, AllCategories);

        Assert.Equal(65, store.Load().Cooling.ManualSpeeds["/lpc/nct6797d/0/control/2"]);

        var engine = new CurveEngine(fans, store, new MultiplexHub());
        engine.Tick();
        Assert.Equal(65, fans.Duty["/lpc/nct6797d/0/control/2"]);
    }

    [Fact]
    public void Apply_AppliesTheImportedOffsetToTheDrivenDuty()
    {
        var (import, fans, store) = Build();
        import.Apply(null, AllCategories);

        var engine = new CurveEngine(fans, store, new MultiplexHub());
        fans.Temperature = 55f;
        engine.Tick();
        var withOffset = fans.Driven.First(d => d.Id == "/lpc/nct6797d/0/control/0").Duty;

        // Same tick without the offset, for the difference.
        var (import2, fans2, store2) = Build();
        import2.Apply(null, new[] { FanControlImportService.CategoryCurves });
        var engine2 = new CurveEngine(fans2, store2, new MultiplexHub());
        fans2.Temperature = 55f;
        engine2.Tick();
        var withoutOffset = fans2.Driven.First(d => d.Id == "/lpc/nct6797d/0/control/0").Duty;

        Assert.Equal(withoutOffset + 5, withOffset);
    }

    [Fact]
    public void ReApplying_ReplacesTheEarlierImportInsteadOfStacking()
    {
        var (import, _, store) = Build();
        import.Apply(null, AllCategories);
        var first = store.Load().Cooling.Curves.Count;

        import.Apply(null, AllCategories);
        Assert.Equal(first, store.Load().Cooling.Curves.Count);
    }

    [Fact]
    public void Apply_DoesNotTouchCurvesTheUserMadeHere()
    {
        var (import, _, store) = Build();
        store.Update(s => s.Cooling.Curves.Add(new CurveDocument
        {
            Id = "mine", Name = "Mine", Type = "Flat", Flat = new FlatCurveData { Speed = 33 },
        }));

        import.Apply(null, AllCategories);

        var mine = store.Load().Cooling.Curves.FirstOrDefault(c => c.Id == "mine");
        Assert.NotNull(mine);
        Assert.Equal(33, mine!.Flat!.Speed);
    }

    [Fact]
    public void Apply_ReleasesAChannelTheImportClaimsFromAUserCurve()
    {
        var (import, _, store) = Build();
        store.Update(s => s.Cooling.Curves.Add(new CurveDocument
        {
            Id = "mine", Name = "Mine", Type = "Flat", Flat = new FlatCurveData { Speed = 33 },
            Outputs = { new CurveOutputDocument { Id = "/lpc/nct6797d/0/control/0", Type = "Fan" } },
        }));

        import.Apply(null, AllCategories);

        // Two curves driving one channel would fight every tick.
        var owners = store.Load().Cooling.Curves
            .Where(c => c.Outputs.Any(o => o.Id == "/lpc/nct6797d/0/control/0"))
            .Select(c => c.Id)
            .ToList();
        Assert.Single(owners);
        Assert.StartsWith("fc-", owners[0]);
    }

    [Fact]
    public void Apply_BindsTheFansSoTheEngineDrivesOnTheNextTick()
    {
        var (import, fans, store) = Build();
        import.Apply(null, AllCategories);

        // The bindings are written by the import itself, so the engine drives
        // on the very next tick without anything else being pressed.
        var engine = new CurveEngine(fans, store, new MultiplexHub());
        fans.Temperature = 55f;
        engine.Tick();

        Assert.Contains(fans.Driven, d => d.Id == "/lpc/nct6797d/0/control/0");
    }

    [Fact]
    public void Apply_CalibrationAndNamesAloneNeedNoPreset()
    {
        var (import, _, store) = Build();
        var result = import.Apply(null, new[]
        {
            FanControlImportService.CategoryCalibration,
            FanControlImportService.CategoryNames,
        });

        Assert.False(result.Error);
        Assert.Empty(store.Load().Cooling.Presets);
        Assert.NotEmpty(store.Load().Cooling.FanCalibrations);
    }

    [Fact]
    public void Apply_WithNoCategories_ChangesNothing()
    {
        var (import, _, store) = Build();
        var result = import.Apply(null, Array.Empty<string>());
        Assert.True(result.Error);
        Assert.Empty(store.Load().Cooling.Curves);
    }

    [Fact]
    public void Apply_RecordsTheImportAsCompleted()
    {
        var (import, _, store) = Build();
        Assert.False(store.Load().FanControlImportCompleted);

        var result = import.Apply(null, AllCategories);

        Assert.False(result.Error);
        Assert.True(store.Load().FanControlImportCompleted);
    }

    [Fact]
    public void Apply_ThatAppliesNothing_LeavesTheImportIncomplete()
    {
        var (import, _, store) = Build();

        // A category the importer does not know passes the empty-selection
        // guard and succeeds, yet imports nothing.
        var result = import.Apply(null, new[] { "unknown-category" });

        Assert.False(result.Error);
        Assert.False(store.Load().FanControlImportCompleted);
    }

    private static readonly string[] BindingCategories =
    {
        FanControlImportService.CategoryCurves,
        FanControlImportService.CategoryOffsets,
        FanControlImportService.CategoryManual,
    };

    [Fact]
    public void Apply_ThatBindsNoFan_NeverCreatesOrActivatesAPreset()
    {
        // No channel matches the config, so nothing can bind.
        var (import, fans, store) = Build();
        fans.Channels.Clear();
        store.Update(s =>
        {
            s.Cooling.Presets.Add(new CoolingPreset { Id = "mine", Name = "Mine", Mode = "custom" });
            s.Cooling.ActivePresetId = "mine";
            s.Cooling.ActivePreset = "custom";
            s.Cooling.CustomFanCurveAssignments = new Dictionary<string, string> { ["/lpc/nct6797d/0/control/0"] = "user-curve" };
            s.Cooling.FanOffsets = new Dictionary<string, int> { ["/lpc/nct6797d/0/control/0"] = 7 };
        });
        var before = store.Load().Cooling;
        var activeBefore = before.ActivePreset;
        var assignmentsBefore = new Dictionary<string, string>(before.CustomFanCurveAssignments);
        var offsetsBefore = new Dictionary<string, int>(before.FanOffsets);

        var result = import.Apply(null, BindingCategories);

        Assert.False(result.Error);
        var after = store.Load().Cooling;
        Assert.Equal("Mine", Assert.Single(after.Presets).Name);
        Assert.Equal("mine", after.ActivePresetId);
        Assert.Equal(activeBefore, after.ActivePreset);
        Assert.Equal(assignmentsBefore, after.CustomFanCurveAssignments);
        Assert.Equal(offsetsBefore, after.FanOffsets);
    }

    private static (string? Curve, int? Manual, int? Offset) Effective(CoolingSettings c, string channel) =>
        (c.Curves.FirstOrDefault(x => x.Outputs.Any(o => o.Id == channel))?.Id,
            c.ManualSpeeds.TryGetValue(channel, out var m) ? m : null,
            c.FanOffsets.TryGetValue(channel, out var o) ? o : null);

    [Theory]
    [InlineData("off")]
    [InlineData("silent")]
    [InlineData("balanced")]
    [InlineData("custom")]
    public void Apply_LeavesAnUnclaimedChannelsCurveManualSpeedAndOffsetAlone(string mode)
    {
        var (import, fans, store) = Build();
        FanProfiles.Apply(mode, fans, store);
        store.Update(s =>
        {
            if (mode is "custom")
            {
                s.Cooling.Curves.Add(new CurveDocument
                {
                    Id = "mine", Name = "Mine", Type = "Flat", Flat = new FlatCurveData { Speed = 33 },
                    Outputs = { new CurveOutputDocument { Id = Other, Type = "Fan" } },
                });
            }
            if (mode is "off")
            {
                s.Cooling.ManualSpeeds[Other] = 40;
            }
            s.Cooling.FanOffsets[Other] = 3;
        });
        var before = Effective(store.Load().Cooling, Other);

        import.Apply(null, AllCategories);
        Assert.Equal(before, Effective(store.Load().Cooling, Other));

        // Applying again, with the earlier import's curves in the list, changes nothing for it either.
        import.Apply(null, AllCategories);
        Assert.Equal(before, Effective(store.Load().Cooling, Other));
    }

    [Fact]
    public void Apply_NeverCreatesAPreset()
    {
        var (import, _, store) = Build();
        var result = import.Apply(null, AllCategories);

        Assert.Empty(store.Load().Cooling.Presets);
        Assert.NotNull(Effective(store.Load().Cooling, Fan0).Curve);
        Assert.Equal(65, store.Load().Cooling.ManualSpeeds[Fan2]);
    }

    [Fact]
    public void ReImporting_WithNoMatchingChannels_KeepsTheFansOnTheEarlierImportsCurves()
    {
        var (import, fans, store) = Build();
        import.Apply(null, AllCategories);
        var before = Effective(store.Load().Cooling, Fan0);
        Assert.StartsWith("fc-", before.Curve);

        // The hardware the config matched is gone; only an unrelated fan is left.
        fans.Channels.RemoveAll(c => c.Id != Other);
        import.Apply(null, AllCategories);

        var cooling = store.Load().Cooling;
        var after = Effective(cooling, Fan0);
        Assert.StartsWith("fc-", after.Curve);
        Assert.Equal(before.Manual, after.Manual);
        Assert.Equal(before.Offset, after.Offset);
        Assert.Equal(65, cooling.ManualSpeeds[Fan2]);
        Assert.NotNull(Effective(cooling, Fan1).Curve);
    }

    [Fact]
    public void Apply_OffsetsOnly_ChangesOnlyThatFansOffset()
    {
        var (import, _, store) = Build();
        store.Update(s =>
        {
            s.Cooling.Curves.Add(new CurveDocument
            {
                Id = "mine", Name = "Mine", Type = "Flat", Flat = new FlatCurveData { Speed = 33 },
                Outputs = { new CurveOutputDocument { Id = Fan0, Type = "Fan" }, new CurveOutputDocument { Id = Other, Type = "Fan" } },
            });
            s.Cooling.CustomFanCurveAssignments = new Dictionary<string, string> { [Fan0] = "mine" };
        });
        var before = store.Load().Cooling;
        var curvesBefore = before.Curves.Select(c => (c.Id, string.Join(",", c.Outputs.Select(o => o.Id)))).ToList();
        var assignmentsBefore = new Dictionary<string, string>(before.CustomFanCurveAssignments);

        var result = import.Apply(null, new[] { FanControlImportService.CategoryOffsets });

        var after = store.Load().Cooling;
        Assert.Equal(5, after.FanOffsets[Fan0]);
        Assert.Equal(curvesBefore, after.Curves.Select(c => (c.Id, string.Join(",", c.Outputs.Select(o => o.Id)))).ToList());
        Assert.Equal(assignmentsBefore, after.CustomFanCurveAssignments);
        Assert.Empty(after.ManualSpeeds);
        Assert.Empty(after.Presets);
        Assert.Equal(before.ActivePreset, after.ActivePreset);
        Assert.Equal(result.OffsetsImported, after.FanOffsets.Count);
    }

    [Fact]
    public void Apply_ManualOnly_ChangesOnlyThatFan()
    {
        var (import, _, store) = Build();
        store.Update(s =>
        {
            s.Cooling.Curves.Add(new CurveDocument
            {
                Id = "mine", Name = "Mine", Type = "Flat", Flat = new FlatCurveData { Speed = 33 },
                Outputs = { new CurveOutputDocument { Id = Fan0, Type = "Fan" }, new CurveOutputDocument { Id = Fan2, Type = "Fan" } },
            });
        });

        import.Apply(null, new[] { FanControlImportService.CategoryManual });

        var after = store.Load().Cooling;
        Assert.Equal(65, after.ManualSpeeds[Fan2]);
        Assert.Single(after.ManualSpeeds);
        Assert.Equal("mine", Effective(after, Fan0).Curve);
        Assert.Null(Effective(after, Fan2).Curve);
        Assert.Empty(after.Presets);
    }

    [Fact]
    public void Apply_NeverCountsOrTouchesPinnedOrUncontrolledChannels()
    {
        var (import, _, store) = Build();
        store.Update(s =>
        {
            s.Cooling.FanPinOverrides[Fan0] = true;
            s.Cooling.UncontrolledFanChannels.Add(Fan2);
        });
        var result = import.Apply(null, AllCategories);

        var cooling = store.Load().Cooling;
        Assert.Null(Effective(cooling, Fan0).Curve);
        Assert.False(cooling.FanOffsets.ContainsKey(Fan0));
        Assert.False(cooling.ManualSpeeds.ContainsKey(Fan2));
        Assert.NotNull(Effective(cooling, Fan1).Curve);
        Assert.DoesNotContain(Fan0, cooling.CustomFanCurveAssignments.Keys);
        Assert.Equal(0, result.ManualImported);
    }

    [Theory]
    [InlineData("names")]
    [InlineData("calibration")]
    [InlineData("manual")]
    [InlineData("offsets")]
    public void Apply_WithoutCurves_LeavesAFansEarlierManualSpeedAndOffsetAlone(string category)
    {
        var (import, _, store) = Build();
        store.Update(s =>
        {
            s.Cooling.ManualSpeeds[Fan1] = 50;
            s.Cooling.CustomManualSpeeds[Fan1] = 50;
            s.Cooling.FanOffsets[Fan1] = 9;
        });

        import.Apply(null, new[] { category });

        var cooling = store.Load().Cooling;
        Assert.Equal(50, cooling.ManualSpeeds[Fan1]);
        Assert.Equal(50, cooling.CustomManualSpeeds[Fan1]);
        Assert.Equal(9, cooling.FanOffsets[Fan1]);
    }

    [Fact]
    public void Apply_ManualOnly_ClearsTheHealUndoWhenItTakesAFanOffACurve()
    {
        var (import, _, store) = Build();
        store.Update(s =>
        {
            s.Cooling.Curves.Add(new CurveDocument
            {
                Id = "mine", Name = "Mine", Type = "Flat", Flat = new FlatCurveData { Speed = 33 },
                Outputs = { new CurveOutputDocument { Id = Fan2, Type = "Fan" } },
            });
            s.Cooling.HealedAtUtcMs = 5;
            s.Cooling.HealedChannels = new List<HealedChannelRecord> { new() { Id = Fan2, Name = "Fan #3", Hazard = "x" } };
        });

        import.Apply(null, new[] { FanControlImportService.CategoryManual });

        var cooling = store.Load().Cooling;
        Assert.Null(Effective(cooling, Fan2).Curve);
        Assert.Null(cooling.HealedAtUtcMs);
        Assert.Empty(cooling.HealedChannels);
    }

    [Fact]
    public void ReImporting_MovesASavedPresetsReferenceToTheRetainedCopyOnly()
    {
        var (import, _, store) = Build();
        import.Apply(null, AllCategories);
        // An unclaimed fan was left on an earlier import's graph curve, and a
        // saved preset and the custom snapshot both name that curve for it.
        store.Update(s =>
        {
            s.Cooling.Curves.First(c => c.Id == "fc-cpu-graph").Outputs.Add(new CurveOutputDocument { Id = Other, Type = "Fan" });
            s.Cooling.Presets.Add(new CoolingPreset
            {
                Id = "gaming", Name = "Gaming", Mode = "custom",
                FanCurveAssignments = new Dictionary<string, string> { [Other] = "fc-cpu-graph", [Fan2] = "mine" },
            });
            s.Cooling.CustomFanCurveAssignments[Other] = "fc-cpu-graph";
        });

        import.Apply(null, AllCategories);

        var cooling = store.Load().Cooling;
        var gaming = cooling.Presets.Single(p => p.Id == "gaming");
        Assert.Equal("fc-cpu-graph-kept", gaming.FanCurveAssignments[Other]);
        Assert.Equal("fc-cpu-graph-kept", cooling.CustomFanCurveAssignments[Other]);
        Assert.Equal("mine", gaming.FanCurveAssignments[Fan2]);
        Assert.Equal("fc-cpu-graph-kept", Effective(cooling, Other).Curve);
        Assert.Equal("fc-cpu-graph", Effective(cooling, Fan0).Curve);
    }

    [Fact]
    public void Apply_ThatBindsAFan_LabelsTheProfileCustom()
    {
        var (import, fans, store) = Build();
        FanProfiles.Apply("silent", fans, store);
        Assert.Equal("silent", store.Load().Cooling.ActivePreset);

        import.Apply(null, AllCategories);

        Assert.Equal("custom", store.Load().Cooling.ActivePreset);
    }

    [Fact]
    public void Apply_OnUnresponsiveFans_LabelsCustomAndSurvivesABootReapply()
    {
        var (import, fans, store) = Build();
        foreach (var c in fans.Channels.Where(c => c.Id != Other))
        {
            c.Classification = "Unresponsive";
        }
        FanProfiles.Apply("silent", fans, store);

        import.Apply(null, AllCategories);

        Assert.Equal("custom", store.Load().Cooling.ActivePreset);
        // Boot re-applies the stored built-in label; custom must leave the bindings alone.
        FanProfiles.Apply(store.Load().Cooling.ActivePreset, fans, store);
        var cooling = store.Load().Cooling;
        Assert.Equal("fc-cpu-graph", Effective(cooling, Fan0).Curve);
        Assert.Equal("fc-case-trigger", Effective(cooling, Fan1).Curve);
        Assert.StartsWith("fc-", Effective(cooling, "/lpc/nct6797d/0/control/3").Curve);
    }
}
