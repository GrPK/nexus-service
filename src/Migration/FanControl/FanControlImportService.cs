using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Cooling;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Persistence;
using Nexus.Service.Routes;

namespace Nexus.Service.Migration.FanControl;

/// <summary>
/// Reads a FanControl configuration and imports it: curves (including the
/// trigger, sync and auto modes ported for this), the fans they drive, the
/// calibration tables FanControl already measured, nicknames, per-fan offsets
/// and manual duties.
///
/// Curves this import created are tagged by an <c>fc-</c> id prefix, so
/// re-importing replaces them instead of stacking duplicates. Curves the user
/// made here are never touched.
/// </summary>
public sealed class FanControlImportService
{
    /// <summary>Id prefix marking a curve this import owns.</summary>
    internal const string ImportedIdPrefix = "fc-";

    public const string CategoryCurves = "curves";
    public const string CategoryCalibration = "calibration";
    public const string CategoryNames = "names";
    public const string CategoryOffsets = "offsets";
    public const string CategoryManual = "manual";

    private readonly IFanControlDetector _detector;
    private readonly IFanControlProvider _fans;
    private readonly IConfigStore _store;

    public FanControlImportService(IFanControlDetector detector, IFanControlProvider fans, IConfigStore store)
    {
        _detector = detector;
        _fans = fans;
        _store = store;
    }

    public FanControlPreviewResponse Preview(string? configPath)
    {
        var plan = BuildPlan(configPath, out var error);
        if (plan is null)
        {
            return new FanControlPreviewResponse { Available = false, Error = true, Msg = error };
        }
        return plan.Preview;
    }

    public FanControlApplyResponse Apply(string? configPath, IReadOnlyList<string> categories)
    {
        var plan = BuildPlan(configPath, out var error, out var config);
        if (plan is null || config is null)
        {
            return new FanControlApplyResponse { Error = true, Msg = error };
        }

        var wanted = new HashSet<string>(categories, StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0)
        {
            return new FanControlApplyResponse { Error = true, Msg = "No categories selected" };
        }

        var response = new FanControlApplyResponse();

        // The import only changes the fans it brings over. Pinned and
        // uncontrolled channels are never touched, and no wholesale preset
        // activation runs: activating one rewrites every unpinned fan, which
        // would hand the fans this import does not bring back to BIOS control.
        var channels = _fans.GetFanChannels();
        var changedAny = false;
        var driveNow = new Dictionary<string, int>(StringComparer.Ordinal);
        _store.Update(s =>
        {
            var cooling = s.Cooling;
            var uncontrolled = new HashSet<string>(cooling.UncontrolledFanChannels, StringComparer.Ordinal);
            var eligible = new HashSet<string>(
                channels.Where(c => !FanProfiles.IsPinned(c, cooling.FanPinOverrides) && !uncontrolled.Contains(c.Id)).Select(c => c.Id),
                StringComparer.Ordinal);

            var curves = plan.Curves.Select(CoolingConfigLint.CloneCurve).ToList();
            foreach (var curve in curves)
            {
                curve.Outputs.RemoveAll(o => !eligible.Contains(o.Id));
            }
            // Only channels whose curve binding is written in this import.
            var claimed = wanted.Contains(CategoryCurves)
                ? new HashSet<string>(curves.SelectMany(c => c.Outputs.Select(o => o.Id)), StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            var manual = wanted.Contains(CategoryManual)
                ? plan.ManualSpeeds.Where(kv => eligible.Contains(kv.Key) && !claimed.Contains(kv.Key))
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal)
                : new Dictionary<string, int>(StringComparer.Ordinal);
            var offsets = wanted.Contains(CategoryOffsets)
                ? plan.Offsets.Where(kv => eligible.Contains(kv.Key))
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal)
                : new Dictionary<string, int>(StringComparer.Ordinal);

            if (wanted.Contains(CategoryCurves))
            {
                cooling.ClearHeal("import");
                // Fans still on an earlier import's curves that this one does
                // not claim keep them on retained copies, and a claimed or
                // manual channel leaves whatever curve drove it: two curves on
                // one channel would fight every tick.
                var previous = cooling.Curves;
                var oldOwner = ImportedOwners(previous);
                var extra = new HashSet<string>(manual.Keys, StringComparer.Ordinal);
                cooling.Curves = Nexus2FanCurveApplier.Compose(previous, curves, ImportedIdPrefix, extra);
                FollowRetainedCopies(cooling, oldOwner, claimed);
                var restore = new Dictionary<string, string>(cooling.CustomFanCurveAssignments, StringComparer.Ordinal);
                foreach (var channel in claimed)
                {
                    var owner = cooling.Curves.FirstOrDefault(c => c.Outputs.Any(o => o.Id == channel));
                    if (owner is not null)
                    {
                        restore[channel] = owner.Id;
                    }
                }
                foreach (var channel in manual.Keys)
                {
                    restore.Remove(channel);
                }
                cooling.CustomFanCurveAssignments = restore;
                response.CurvesImported = plan.Curves.Count;
                changedAny |= claimed.Count > 0;
            }
            else if (manual.Count > 0)
            {
                var removed = false;
                foreach (var curve in cooling.Curves)
                {
                    removed |= curve.Outputs.RemoveAll(o => manual.ContainsKey(o.Id)) > 0;
                }
                if (removed)
                {
                    // Undo heal would restore the old curves over this import.
                    cooling.ClearHeal("import");
                }
                foreach (var channel in manual.Keys)
                {
                    cooling.CustomFanCurveAssignments.Remove(channel);
                }
            }

            // A manual duty on a channel that now follows a curve would fight it.
            foreach (var channel in claimed)
            {
                cooling.ManualSpeeds.Remove(channel);
                cooling.CustomManualSpeeds.Remove(channel);
            }
            foreach (var (channel, duty) in manual)
            {
                cooling.ManualSpeeds[channel] = duty;
                cooling.CustomManualSpeeds[channel] = duty;
            }
            response.ManualImported = manual.Count;
            foreach (var (channel, duty) in manual)
            {
                driveNow[channel] = duty;
            }
            changedAny |= manual.Count > 0;

            // An offset on a claimed channel skews its new curve unless the
            // import brings its own.
            foreach (var channel in claimed)
            {
                if (!offsets.ContainsKey(channel))
                {
                    cooling.FanOffsets.Remove(channel);
                }
            }
            foreach (var (channel, offset) in offsets)
            {
                cooling.FanOffsets[channel] = offset;
            }
            response.OffsetsImported = offsets.Count;

            if (wanted.Contains(CategoryCalibration))
            {
                foreach (var (id, calibration) in plan.Calibrations)
                {
                    cooling.FanCalibrations[id] = calibration;
                }
                response.CalibrationsImported = plan.Calibrations.Count;
            }

            if (wanted.Contains(CategoryNames))
            {
                foreach (var (id, name) in plan.Names)
                {
                    cooling.FanNames[id] = name;
                }
                response.NamesImported = plan.Names.Count;
            }

            if (changedAny)
            {
                // Derivation ignores unresponsive channels, so a built-in label
                // could survive and the boot re-apply would pull the imported
                // fans back onto its curve.
                cooling.ActivePreset = "custom";
            }
        });

        // CurveEngine replays a manual duty once per run, so a fan it already
        // replayed would keep its old duty until restart. Drive the new one the
        // way the manual-speed route does.
        foreach (var (channel, duty) in driveNow)
        {
            _fans.SetFanSpeed(channel, duty);
        }

        var appliedAny = response.CurvesImported > 0
            || response.CalibrationsImported > 0
            || response.NamesImported > 0
            || response.OffsetsImported > 0
            || response.ManualImported > 0;
        if (appliedAny && !_store.Load().FanControlImportCompleted)
        {
            // Double-check under the store lock so two racing applies write once.
            _store.Update(s =>
            {
                if (!s.FanControlImportCompleted)
                {
                    s.FanControlImportCompleted = true;
                }
            });
        }

        return response;
    }

    /// <summary>Channel to the id of the earlier import's curve that drives it.</summary>
    private static Dictionary<string, string> ImportedOwners(IEnumerable<CurveDocument> curves)
    {
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var curve in curves.Where(c => c.Id.StartsWith(ImportedIdPrefix, StringComparison.Ordinal)))
        {
            foreach (var output in curve.Outputs)
            {
                owners[output.Id] = curve.Id;
            }
        }
        return owners;
    }

    /// <summary>
    /// A channel this import does not claim stays on a retained copy of its
    /// earlier curve, which gets a new id. Saved assignments that named the
    /// earlier id for that channel follow it; any other reference (a user
    /// curve, a built-in preset curve) is left as it is.
    /// </summary>
    private static void FollowRetainedCopies(CoolingSettings cooling, Dictionary<string, string> oldOwner, HashSet<string> claimed)
    {
        var moved = new Dictionary<string, (string From, string To)>(StringComparer.Ordinal);
        foreach (var (channel, from) in oldOwner)
        {
            if (claimed.Contains(channel))
            {
                continue;
            }
            var now = cooling.Curves.FirstOrDefault(
                c => c.Id.StartsWith(ImportedIdPrefix, StringComparison.Ordinal) && c.Outputs.Any(o => o.Id == channel));
            if (now is not null && now.Id != from)
            {
                moved[channel] = (from, now.Id);
            }
        }

        foreach (var map in cooling.Presets.Select(p => p.FanCurveAssignments).Append(cooling.CustomFanCurveAssignments))
        {
            foreach (var (channel, move) in moved)
            {
                if (map.TryGetValue(channel, out var id) && id == move.From)
                {
                    map[channel] = move.To;
                }
            }
        }
    }

    private FanControlImportPlan? BuildPlan(string? configPath, out string error) =>
        BuildPlan(configPath, out error, out _);

    private FanControlImportPlan? BuildPlan(string? configPath, out string error, out FanControlConfigFile? source)
    {
        error = "Ok";
        source = null;
        var detection = _detector.Detect();
        if (detection.Configs.Count == 0)
        {
            error = "No FanControl configuration found";
            return null;
        }

        var config = string.IsNullOrEmpty(configPath)
            ? detection.Configs[0]
            : detection.Configs.FirstOrDefault(c =>
                string.Equals(c.Path, configPath, StringComparison.OrdinalIgnoreCase));
        if (config is null)
        {
            error = "That FanControl configuration no longer exists";
            return null;
        }

        source = config;
        var json = _detector.ReadConfig(config.Path);
        if (string.IsNullOrEmpty(json))
        {
            error = "Could not read the FanControl configuration";
            return null;
        }

        FanControlConfig parsed;
        try
        {
            parsed = FanControlConfigParser.Parse(json);
        }
        catch (Exception ex)
        {
            error = $"Could not read the FanControl configuration: {ex.Message}";
            return null;
        }

        // A channel the user marked not controlled is not the import's to claim:
        // the write gate would silently drop every duty, leaving the editor
        // showing a fan wired to a curve that drives nothing. Withholding it
        // from the match candidates is what keeps the marker meaningful across
        // an import, not just across a preset apply.
        var uncontrolled = _store.Load().Cooling.UncontrolledFanChannels;
        var channels = _fans.GetFanChannels()
            .Where(c => !uncontrolled.Contains(c.Id))
            .Select(c => new LhmIdentifierMatcher.Candidate(c.Id, c.Name))
            .ToList();
        var sensors = _fans.GetTemperatureSources()
            .Select(s => new LhmIdentifierMatcher.Candidate(s.Id, s.Name))
            .ToList();

        var plan = FanControlImportMapper.Build(parsed, channels, sensors);

        // Imported curves come from another app's file, so they go through the
        // same clamp as anything posted to /cooling/curves/set rather than
        // straight into the store.
        var sanitized = CoolingSafety.Sanitize(new SetCurvesBody
        {
            GlobalSpeedModifier = 1.0,
            Curves = plan.Curves.ConvertAll(CurveWireMapper.ToWire),
        });
        plan.Curves.Clear();
        plan.Curves.AddRange(sanitized.Curves.ConvertAll(CurveWireMapper.ToDocument));

        plan.Preview.ConfigName = config.Name;
        return plan;
    }
}
