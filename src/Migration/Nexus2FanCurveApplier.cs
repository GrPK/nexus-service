using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Cooling;
using Nexus.Service.Persistence;

namespace Nexus.Service.Migration;

/// <summary>Writes imported curves into cooling settings; a channel is bound by the curve that lists it, so only claimed channels' output lists change.</summary>
internal static class Nexus2FanCurveApplier
{
    private const string KeptSuffix = "-kept";

    /// <summary>The curve list after importing; pure, so the lint pass and the real write build the same config.</summary>
    public static List<CurveDocument> Compose(
        IReadOnlyList<CurveDocument> current,
        IReadOnlyList<CurveDocument> imported,
        string prefix = Nexus2FanCurvePlanner.ImportedIdPrefix,
        IReadOnlySet<string>? extraClaimed = null)
    {
        var claimed = imported.SelectMany(c => c.Outputs.Select(o => o.Id)).ToHashSet(StringComparer.Ordinal);
        if (extraClaimed is not null)
        {
            claimed.UnionWith(extraClaimed);
        }
        // A Mixed curve reads its members by id, so a referenced import keeps its id and document.
        // An earlier import's own Mixed curve counts only while it survives on leftover channels; one this import replaces would otherwise pin its members and make the new copies collide with them.
        var referenced = current
            .Where(c => c?.Mixed is not null && (!IsImported(c, prefix) || c.Outputs.Any(o => !claimed.Contains(o.Id))))
            .SelectMany(c => c.Mixed!.CurveIds).ToHashSet(StringComparer.Ordinal);
        var result = new List<CurveDocument>();
        var kept = new List<CurveDocument>();

        // One allocator for every id this list ends up holding; ids that stay put are reserved first.
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var curve in current)
        {
            if (curve is not null && (!IsImported(curve, prefix) || referenced.Contains(curve.Id)))
            {
                taken.Add(curve.Id);
            }
        }

        // An existing kept copy that still has leftovers keeps its own id.
        foreach (var curve in current)
        {
            if (curve is not null && IsImported(curve, prefix) && !referenced.Contains(curve.Id) && curve.Id.EndsWith(KeptSuffix, StringComparison.Ordinal)
                && curve.Outputs.Any(o => !claimed.Contains(o.Id)))
            {
                taken.Add(curve.Id);
            }
        }

        foreach (var curve in current)
        {
            if (curve is null)
            {
                continue;
            }
            if (!IsImported(curve, prefix) || referenced.Contains(curve.Id))
            {
                result.Add(RemoveOutputs(curve, claimed));
                continue;
            }

            // A channel an earlier import drove and this one does not claim keeps its behavior on a retained copy.
            var leftover = curve.Outputs.Where(o => !claimed.Contains(o.Id)).ToList();
            if (leftover.Count == 0)
            {
                continue;
            }
            // Leftovers from different source documents are never merged: each set stays on a copy of its own document.
            var copy = CoolingConfigLint.CloneCurve(curve);
            copy.Id = curve.Id.EndsWith(KeptSuffix, StringComparison.Ordinal) ? curve.Id : Allocate(curve.Id + KeptSuffix, taken);
            copy.Outputs = leftover.Select(o => new CurveOutputDocument { Id = o.Id, Type = o.Type }).ToList();
            kept.Add(copy);
        }

        result.AddRange(kept);
        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        var added = new List<CurveDocument>();
        foreach (var curve in imported)
        {
            var copy = CoolingConfigLint.CloneCurve(curve);
            copy.Id = Allocate(copy.Id, taken);
            if (copy.Id != curve.Id)
            {
                renamed[curve.Id] = copy.Id;
            }
            added.Add(copy);
        }
        // An imported Mixed curve must keep reading its own members after an id moved.
        if (renamed.Count > 0)
        {
            foreach (var copy in added.Where(c => c.Mixed is not null))
            {
                copy.Mixed!.CurveIds = copy.Mixed.CurveIds.Select(id => renamed.TryGetValue(id, out var to) ? to : id).ToList();
            }
        }
        result.AddRange(added);
        return result;
    }

    private static string Allocate(string desired, HashSet<string> taken)
    {
        var id = desired;
        for (var n = 2; !taken.Add(id); n++)
        {
            id = desired + "-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return id;
    }

    private static bool IsImported(CurveDocument curve, string prefix) =>
        curve.Id.StartsWith(prefix, StringComparison.Ordinal);

    private static CurveDocument RemoveOutputs(CurveDocument curve, HashSet<string> claimed)
    {
        if (curve.Outputs is null || !curve.Outputs.Any(o => o is not null && claimed.Contains(o.Id)))
        {
            return curve;
        }
        var copy = CoolingConfigLint.CloneCurve(curve);
        copy.Outputs.RemoveAll(o => claimed.Contains(o.Id));
        return copy;
    }

    /// <summary>Inside a store update: swaps the curve list and clears per-channel state that would fight a claimed channel's curve.</summary>
    public static int Apply(CoolingSettings cooling, IReadOnlyList<CurveDocument> imported)
    {
        var composed = Compose(cooling.Curves, imported);
        var claimed = imported.SelectMany(c => c.Outputs.Select(o => o.Id)).ToList();
        var liveIds = composed.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);

        cooling.Curves = composed;

        // Custom-mode restore must not point at curves that no longer exist.
        var restore = new Dictionary<string, string>(cooling.CustomFanCurveAssignments ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        foreach (var stale in restore.Where(kv => kv.Value.StartsWith(Nexus2FanCurvePlanner.ImportedIdPrefix, StringComparison.Ordinal) && !liveIds.Contains(kv.Value)).Select(kv => kv.Key).ToList())
        {
            restore.Remove(stale);
        }
        foreach (var channel in claimed)
        {
            var owner = composed.FirstOrDefault(c => c.Outputs.Any(o => o.Id == channel));
            if (owner is not null)
            {
                restore[channel] = owner.Id;
            }
        }
        cooling.CustomFanCurveAssignments = restore;

        // A manual duty or offset on a claimed channel overrides or skews its new curve.
        foreach (var id in claimed)
        {
            cooling.ManualSpeeds.Remove(id);
            cooling.CustomManualSpeeds?.Remove(id);
            cooling.FanOffsets.Remove(id);
        }
        return imported.Count;
    }
}
