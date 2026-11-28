using System;
using System.Collections.Generic;
using System.Text;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// The devices on Quick Sync's merged line in L-Connect's merge order, and the share of the merged effect each
/// one plays. L-Connect keeps one device list for the merge: a device joins the end the first time it is seen,
/// keeps its place and direction while it is away, and the user can reorder it; the line runs through the
/// devices present, in that order. The render is shared by the three frame writers and redone only when the
/// effect, its look or the line changes.
/// </summary>
public sealed class LianLiMergeService
{
    /// <summary>Quick Sync ids of the single USB devices (a TL chain's is its MAC).</summary>
    public const string CurveId = LianLiMerge.KindCurve;
    public const string Screen88Id = LianLiMerge.KindScreen88;

    private readonly IConfigStore _store;
    private readonly Slv3Hub _hub;
    private readonly HydroShift2CurveBoard _curve;
    private readonly UniversalScreen88LedBoard _screen88;
    private readonly Func<long> _nowMs;
    private readonly object _lock = new();
    private Snapshot? _current;

    /// <summary>One render of a merged effect over the devices present.</summary>
    public sealed class Snapshot
    {
        public required LianLiMerge.EffectInfo Effect { get; init; }
        public required IReadOnlyList<LianLiMerge.Participant> Devices { get; init; }
        /// <summary>Each device's share, by Quick Sync id (case-insensitive).</summary>
        public required IReadOnlyDictionary<string, LianLiMerge.Output> Outputs { get; init; }
        public required string Signature { get; init; }
        /// <summary>When this render took over (<see cref="Environment.TickCount64"/> ms), so the USB devices step together.</summary>
        public required long EpochMs { get; init; }
    }

    public LianLiMergeService(IConfigStore store, Slv3Hub hub, HydroShift2CurveBoard curve, UniversalScreen88LedBoard screen88, Func<long>? nowMs = null)
    {
        _store = store;
        _hub = hub;
        _curve = curve;
        _screen88 = screen88;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
    }

    public long NowMs => _nowMs();

    /// <summary>The devices present, in merge order; a device seen for the first time is stored at the end.</summary>
    public IReadOnlyList<LianLiMerge.Participant> Devices(NexusSettings settings)
    {
        var present = Present(_hub.State, _curve.IsAvailable, _screen88.IsAvailable);
        var ordered = Order(settings.Devices.LianLiSync.MergeDevices, present, out var members);
        if (members is not null)
        {
            // The frame writers read the sync outside the store lock, so a new one is swapped in whole.
            _store.Update(s =>
            {
                Order(s.Devices.LianLiSync.MergeDevices, present, out var latest);
                if (latest is not null)
                {
                    s.Devices.LianLiSync = WithMembers(s.Devices.LianLiSync, latest);
                }
            });
        }
        return ordered;
    }

    /// <summary>The merged effect playing now, or null while Quick Sync is off or on one of its six per-device effects.</summary>
    public Snapshot? Current(NexusSettings settings)
    {
        var sync = settings.Devices.LianLiSync;
        if (!sync.Enabled || LianLiMerge.Find(sync.Effect) is not { } effect)
        {
            return null;
        }
        var devices = Devices(settings);
        var look = LianLiSync.LookOf(sync, effect.Key);
        var signature = Signature(effect, look, devices);
        lock (_lock)
        {
            if (_current?.Signature != signature)
            {
                _current = new Snapshot
                {
                    Effect = effect,
                    Devices = devices,
                    Outputs = LianLiMerge.Render(effect, look, devices),
                    Signature = signature,
                    EpochMs = _nowMs(),
                };
            }
            return _current;
        }
    }

    /// <summary>
    /// The Lian Li devices that can join the line: the TL chains bound to this PC's receiver, in the
    /// receiver's order, then the OLED Curved edge and the 8.8 bezel while Nexus drives them.
    /// </summary>
    internal static List<LianLiMerge.Participant> Present(Slv3State wireless, bool curve, bool screen88)
    {
        var list = new List<LianLiMerge.Participant>();
        if (wireless.IsConnected)
        {
            foreach (var fan in wireless.Fans)
            {
                if (IsTlChain(fan))
                {
                    list.Add(new LianLiMerge.Participant(LianLiMerge.KindTl, fan.Mac.ToUpperInvariant(), fan.FanCount, fan.FanCount * LianLiMergeLine.TlDotsPerFan, 0));
                }
            }
        }
        if (curve)
        {
            list.Add(new LianLiMerge.Participant(LianLiMerge.KindCurve, CurveId, 0, LianLiMergeLine.CurveDots, 0));
        }
        if (screen88)
        {
            list.Add(new LianLiMerge.Participant(LianLiMerge.KindScreen88, Screen88Id, 0, LianLiMergeLine.Screen88Dots, 0));
        }
        return list;
    }

    /// <summary>A wireless TL chain on this PC's receiver (not a Strimer, a HydroShift or another fan family).</summary>
    public static bool IsTlChain(Slv3FanInfo fan) =>
        fan.BoundToUs && fan.FanCount > 0 && fan.FanCount <= Slv3FanEffects.MaxFans
        && !Slv3Protocol.IsStrimerDevType((byte)fan.DevType) && !Slv3Protocol.IsHydroShiftDevType((byte)fan.DevType)
        && Slv3FanEffects.IsTlFamily(Slv3Protocol.ClassifyFanFamily((byte)fan.FanType));

    /// <summary>
    /// The present devices in stored order with their stored directions, newcomers appended in
    /// <paramref name="present"/>'s order. <paramref name="members"/> is the stored list with the newcomers
    /// added, or null when nothing was new.
    /// </summary>
    internal static List<LianLiMerge.Participant> Order(IReadOnlyList<LianLiMergeMember> stored, IReadOnlyList<LianLiMerge.Participant> present, out List<LianLiMergeMember>? members)
    {
        var byId = new Dictionary<string, LianLiMerge.Participant>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in present)
        {
            byId.TryAdd(p.Id, p);
        }
        var ordered = new List<LianLiMerge.Participant>(present.Count);
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in stored)
        {
            if (!known.Add(m.Id))
            {
                continue;
            }
            if (byId.TryGetValue(m.Id, out var p))
            {
                ordered.Add(p with { Direction = Math.Clamp(m.Direction, 0, 1) });
            }
        }
        List<LianLiMerge.Participant>? newcomers = null;
        foreach (var p in present)
        {
            if (known.Add(p.Id))
            {
                (newcomers ??= new List<LianLiMerge.Participant>()).Add(p);
                ordered.Add(p);
            }
        }
        members = null;
        if (newcomers is not null)
        {
            members = new List<LianLiMergeMember>(stored.Count + newcomers.Count);
            foreach (var m in stored)
            {
                members.Add(new LianLiMergeMember { Id = m.Id, Direction = m.Direction });
            }
            foreach (var p in newcomers)
            {
                members.Add(new LianLiMergeMember { Id = p.Id, Direction = 0 });
            }
        }
        return ordered;
    }

    /// <summary>A copy of <paramref name="sync"/> with another merge order.</summary>
    internal static LianLiSyncSettings WithMembers(LianLiSyncSettings sync, List<LianLiMergeMember> members) => new()
    {
        Enabled = sync.Enabled,
        Effect = sync.Effect,
        Looks = sync.Looks,
        MergeDevices = members,
    };

    /// <summary>
    /// <paramref name="frames"/> for the card and master brightness (<paramref name="cap"/>, 1 leaves them as
    /// L-Connect draws them) and the card's colour order.
    /// </summary>
    internal static List<byte[]> Adjust(IReadOnlyList<byte[]> frames, double cap, string? colorOrder)
    {
        var reorder = DeviceColorAdjust.NormalizeOrder(colorOrder) is not (null or "RGB");
        var scale = Math.Clamp(cap, 0, 1);
        var result = new List<byte[]>(frames.Count);
        foreach (var source in frames)
        {
            var frame = (byte[])source.Clone();
            if (scale < 1)
            {
                for (var i = 0; i < frame.Length; i++)
                {
                    frame[i] = (byte)(frame[i] * scale);
                }
            }
            if (reorder)
            {
                for (var i = 0; i + 2 < frame.Length; i += 3)
                {
                    DeviceColorAdjust.ReorderChannels(colorOrder!, ref frame[i], ref frame[i + 1], ref frame[i + 2]);
                }
            }
            result.Add(frame);
        }
        return result;
    }

    /// <summary>The frame of a USB device's share due at <paramref name="nowMs"/>, and the ms until the next one.</summary>
    internal static (int Index, int WaitMs) FrameAt(long epochMs, long nowMs, int periodMs, int count, int idleMs)
    {
        if (count <= 1 || periodMs <= 0)
        {
            return (0, idleMs);
        }
        var elapsed = Math.Max(0, nowMs - epochMs);
        var index = (int)(elapsed / periodMs % count);
        var wait = (int)(periodMs - (elapsed % periodMs));
        return (index, Math.Max(1, wait));
    }

    private static string Signature(LianLiMerge.EffectInfo effect, LianLiSyncLook look, IReadOnlyList<LianLiMerge.Participant> devices)
    {
        var sb = new StringBuilder(effect.Key);
        sb.Append('|').Append(look.Speed).Append('|').Append(look.Brightness).Append('|').Append(look.Direction).Append('|').Append(string.Join(',', look.Colors));
        foreach (var d in devices)
        {
            sb.Append('|').Append(d.Kind).Append(':').Append(d.Id).Append(':').Append(d.FanCount).Append(':').Append(d.Direction);
        }
        return sb.ToString();
    }
}
