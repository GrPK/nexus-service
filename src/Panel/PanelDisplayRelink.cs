using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Models.Displays;

namespace Nexus.Service.Panel;

internal readonly record struct RelinkRecord(string RecordId, string DisplayId, DisplayFingerprint? Fingerprint);

internal readonly record struct RelinkDisplay(string Id, DisplayFingerprint? Fingerprint);

/// <param name="Weak">The match rests on model alone (a serial is missing or placeholder on a side); such moves need confirmation across scans.</param>
internal readonly record struct RelinkMove(string RecordId, string OldDisplayId, string NewDisplayId, DisplayFingerprint Fingerprint, bool Weak);

internal sealed class RelinkPlan
{
    public List<(string RecordId, DisplayFingerprint Fingerprint)> Backfills { get; } = new();
    public List<RelinkMove> Moves { get; } = new();
}

/// <summary>
/// Decides which display-bound panel records follow their monitor to a new
/// OS display id. Pure: callers apply the plan.
/// </summary>
internal static class PanelDisplayRelink
{
    private const long PlaceholderSerial = 0x01010101;

    public static RelinkPlan Plan(IReadOnlyList<RelinkRecord> records, IReadOnlyList<RelinkDisplay> displays)
    {
        var plan = new RelinkPlan();
        var attached = new Dictionary<string, DisplayFingerprint?>(StringComparer.Ordinal);
        foreach (var d in displays)
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
                if (r.Fingerprint is null && live is not null) plan.Backfills.Add((r.RecordId, live));
            }
            else if (r.Fingerprint is not null)
            {
                orphans.Add(r);
            }
        }

        var unclaimed = attached
            .Where(kv => kv.Value is not null && !claimed.Contains(kv.Key))
            .Select(kv => new RelinkDisplay(kv.Key, kv.Value))
            .ToList();

        foreach (var orphan in orphans.Where(o => IsStrong(o.Fingerprint!)).ToList())
        {
            var sameOrphans = orphans.Count(o => SameDevice(o.Fingerprint!, orphan.Fingerprint!));
            var candidates = unclaimed.Where(d => SameDevice(d.Fingerprint!, orphan.Fingerprint!)).ToList();
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
                .Where(d => ModelKey(d.Fingerprint!) == group.Key && !(IsStrong(orphan.Fingerprint!) && IsStrong(d.Fingerprint!)))
                .ToList();
            if (candidates.Count != 1) continue;
            Move(plan, orphan, candidates[0], weak: true);
        }
        return plan;
    }

    private static void Move(RelinkPlan plan, RelinkRecord record, RelinkDisplay target, bool weak)
        => plan.Moves.Add(new RelinkMove(record.RecordId, record.DisplayId, target.Id, target.Fingerprint!, weak));

    private static bool IsStrong(DisplayFingerprint f)
        => (f.Serial != 0 && f.Serial != PlaceholderSerial) || !string.IsNullOrEmpty(f.SerialText);

    private static string ModelKey(DisplayFingerprint f)
        => $"{f.Manufacturer.ToUpperInvariant()}:{f.Product.ToUpperInvariant()}";

    private static bool SameDevice(DisplayFingerprint a, DisplayFingerprint b)
        => ModelKey(a) == ModelKey(b)
           && IsStrong(a) && IsStrong(b)
           && a.Serial == b.Serial
           && string.Equals(a.SerialText ?? "", b.SerialText ?? "", StringComparison.Ordinal);
}
