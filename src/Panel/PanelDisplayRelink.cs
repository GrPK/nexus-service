using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Nexus.Service.Models.Displays;

namespace Nexus.Service.Panel;

internal readonly record struct RelinkRecord(
    string RecordId,
    string DisplayId,
    DisplayFingerprint? Fingerprint,
    IReadOnlyList<string>? Siblings = null);

internal readonly record struct RelinkDisplay(string Id, DisplayFingerprint? Fingerprint);

/// <param name="Weak">The match rests on model alone (a serial is missing or placeholder on a side); such moves need confirmation across scans.</param>
internal readonly record struct RelinkMove(string RecordId, string OldDisplayId, string NewDisplayId, DisplayFingerprint Fingerprint, bool Weak);

internal sealed class RelinkPlan
{
    public List<(string RecordId, DisplayFingerprint Fingerprint)> Backfills { get; } = new();
    public List<(string RecordId, List<string> Siblings)> SiblingUpdates { get; } = new();
    public List<RelinkMove> Moves { get; } = new();
}

/// <summary>
/// Decides which display-bound panel records follow their monitor to a new
/// OS display id. Pure: callers apply the plan.
/// </summary>
internal static partial class PanelDisplayRelink
{
    internal const int MaxSiblings = 16;
    private const long PlaceholderSerial = 0x01010101;

    // The PnP-derived id: EISA letters + hex product, optionally behind the old DISPLAY\ prefix.
    [GeneratedRegex(@"^(?:DISPLAY[\\#])?(?<mfg>[A-Z]{3})(?<prod>[0-9A-F]{4})(?:[-\\#]|$)")]
    private static partial Regex PnpIdPattern();

    public static RelinkPlan Plan(IReadOnlyList<RelinkRecord> records, IReadOnlyList<RelinkDisplay> displays)
    {
        var plan = new RelinkPlan();
        var shown = Demote(displays);
        var attached = new Dictionary<string, DisplayFingerprint?>(StringComparer.Ordinal);
        foreach (var d in shown)
        {
            if (!string.IsNullOrEmpty(d.Id)) attached.TryAdd(d.Id, d.Fingerprint);
        }
        var claimed = new HashSet<string>(records.Select(r => r.DisplayId), StringComparer.Ordinal);

        var orphans = new List<RelinkRecord>();
        foreach (var r in records)
        {
            if (string.IsNullOrEmpty(r.DisplayId)) continue;
            if (attached.TryGetValue(r.DisplayId, out var live))
            {
                if (live is not null && (r.Fingerprint is null || Score(live) > Score(r.Fingerprint)))
                    plan.Backfills.Add((r.RecordId, live));
                var merged = MergeSiblings(r.Siblings, attached.Keys, r.DisplayId);
                if (merged is not null) plan.SiblingUpdates.Add((r.RecordId, merged));
                continue;
            }
            var fingerprint = r.Fingerprint ?? FromStoredId(r.DisplayId);
            if (fingerprint is not null) orphans.Add(r with { Fingerprint = fingerprint });
        }

        var unclaimed = attached
            .Where(kv => kv.Value is not null && !claimed.Contains(kv.Key))
            .Select(kv => new RelinkDisplay(kv.Key, kv.Value))
            .ToList();

        // A monitor that was attached beside the record's own display is a different one.
        static bool Eligible(RelinkRecord orphan, RelinkDisplay d)
            => orphan.Siblings is null || !orphan.Siblings.Contains(d.Id, StringComparer.Ordinal);

        foreach (var orphan in orphans.Where(o => IsStrong(o.Fingerprint!)).ToList())
        {
            var sameOrphans = orphans.Count(o => SameDevice(o.Fingerprint!, orphan.Fingerprint!));
            var candidates = unclaimed.Where(d => Eligible(orphan, d) && SameDevice(d.Fingerprint!, orphan.Fingerprint!)).ToList();
            if (sameOrphans != 1 || candidates.Count != 1) continue;
            Move(plan, orphan, candidates[0], weak: false);
            orphans.Remove(orphan);
            unclaimed.Remove(candidates[0]);
        }

        foreach (var group in orphans.GroupBy(o => ModelKey(o.Fingerprint!)))
        {
            if (group.Count() != 1) continue;
            var orphan = group.First();
            // Two strong serials that differ are two physical monitors.
            var candidates = unclaimed
                .Where(d => Eligible(orphan, d)
                    && ModelKey(d.Fingerprint!) == group.Key
                    && !(IsStrong(orphan.Fingerprint!) && IsStrong(d.Fingerprint!)))
                .ToList();
            if (candidates.Count != 1) continue;
            Move(plan, orphan, candidates[0], weak: true);
        }
        return plan;
    }

    /// <summary>Weak fingerprint from the manufacturer and product embedded in a PnP-format display id; null for fallback ids.</summary>
    internal static DisplayFingerprint? FromStoredId(string displayId)
    {
        var m = PnpIdPattern().Match(displayId);
        return m.Success
            ? new DisplayFingerprint { Manufacturer = m.Groups["mfg"].Value, Product = m.Groups["prod"].Value }
            : null;
    }

    /// <summary>Strong fingerprints shared by several attached monitors (placeholder serial text) identify none of them.</summary>
    private static IReadOnlyList<RelinkDisplay> Demote(IReadOnlyList<RelinkDisplay> displays)
    {
        var result = new List<RelinkDisplay>(displays.Count);
        foreach (var d in displays)
        {
            var f = d.Fingerprint;
            if (f is not null && IsStrong(f)
                && displays.Count(o => o.Fingerprint is not null && SameDevice(o.Fingerprint, f)) > 1)
            {
                f = new DisplayFingerprint { Manufacturer = f.Manufacturer, Product = f.Product };
            }
            result.Add(new RelinkDisplay(d.Id, f));
        }
        return result;
    }

    private static List<string>? MergeSiblings(IReadOnlyList<string>? stored, IEnumerable<string> attachedIds, string ownId)
    {
        var current = stored ?? Array.Empty<string>();
        var merged = new List<string>(current);
        foreach (var id in attachedIds)
        {
            if (id != ownId && !merged.Contains(id, StringComparer.Ordinal)) merged.Add(id);
        }
        if (merged.Count > MaxSiblings) merged.RemoveRange(0, merged.Count - MaxSiblings);
        return merged.SequenceEqual(current, StringComparer.Ordinal) ? null : merged;
    }

    private static int Score(DisplayFingerprint f)
        => ((f.Serial != 0 && f.Serial != PlaceholderSerial) ? 1 : 0) + (string.IsNullOrEmpty(f.SerialText) ? 0 : 1);

    private static void Move(RelinkPlan plan, RelinkRecord record, RelinkDisplay target, bool weak)
        => plan.Moves.Add(new RelinkMove(record.RecordId, record.DisplayId, target.Id, target.Fingerprint!, weak));

    private static bool IsStrong(DisplayFingerprint f) => Score(f) > 0;

    private static string ModelKey(DisplayFingerprint f)
        => $"{f.Manufacturer.ToUpperInvariant()}:{f.Product.ToUpperInvariant()}";

    private static bool SameDevice(DisplayFingerprint a, DisplayFingerprint b)
        => ModelKey(a) == ModelKey(b)
           && IsStrong(a) && IsStrong(b)
           && a.Serial == b.Serial
           && string.Equals(a.SerialText ?? "", b.SerialText ?? "", StringComparison.Ordinal);
}
