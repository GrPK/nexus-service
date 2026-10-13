using System;
using System.Collections.Generic;
using System.Globalization;
using Nexus.Service.Persistence;

namespace Nexus.Service.Peripherals.AsiahorseUmbra;

// Frames that commit the persisted lighting source to the device. The Nexus stream itself is the lighting writer's.
internal static class UmbraLightingPlan
{
    public const string SourceNexus = "nexus";
    public const string SourceFirmware = "firmware";
    public const string SourceArgbInput = "argbInput";

    public static bool IsSource(string? source) =>
        source is SourceNexus or SourceFirmware or SourceArgbInput;

    // An unknown stored source falls back to the default.
    public static string SourceOf(AsiahorseUmbraSettings s) => IsSource(s.Source) ? s.Source : SourceNexus;

    public static byte[][] Build(AsiahorseUmbraSettings s, UmbraPort[] ports, float master)
    {
        var frames = new List<byte[]>();
        var source = SourceOf(s);
        if (source == SourceArgbInput)
        {
            frames.Add(UmbraProtocol.EncodeAuthority(false));
            return frames.ToArray();
        }
        frames.Add(UmbraProtocol.EncodeAuthority(true));
        if (source != SourceFirmware)
        {
            return frames.ToArray();
        }
        if (!s.LightOn || master <= 0f)
        {
            frames.Add(UmbraProtocol.EncodeLightsOff());
            return frames.ToArray();
        }
        switch (s.Group)
        {
            case UmbraEffects.GroupMerge:
            {
                var (info, values) = Current(UmbraEffects.GroupMerge, s.Merge);
                frames.Add(UmbraProtocol.EncodeMergeEffect(UmbraEffects.ToBlock(UmbraEffects.GroupMerge, info, values, master)));
                break;
            }
            case UmbraEffects.GroupAsync:
            {
                for (var p = 0; p < ports.Length; p++)
                {
                    if (ports[p].LedCount == 0)
                    {
                        continue;
                    }
                    s.Async.TryGetValue(p.ToString(CultureInfo.InvariantCulture), out var state);
                    var (info, values) = Current(UmbraEffects.GroupAsync, state ?? new UmbraEffectGroupSettings());
                    frames.Add(UmbraProtocol.EncodeAsyncEffect(p, UmbraEffects.ToBlock(UmbraEffects.GroupAsync, info, values, master)));
                }
                break;
            }
            default:
            {
                var (info, values) = Current(UmbraEffects.GroupSync, s.Sync);
                frames.Add(UmbraProtocol.EncodeSyncEffect(UmbraEffects.ToBlock(UmbraEffects.GroupSync, info, values, master)));
                break;
            }
        }
        return frames.ToArray();
    }

    // The selected effect of a group, falling back to rainbow when the stored key is unknown or not allowed in the group.
    public static (UmbraEffectInfo Info, UmbraEffectValues Values) Current(string group, UmbraEffectGroupSettings state)
    {
        var info = UmbraEffects.Find(state.Effect);
        if (info is null || !UmbraEffects.AllowedIn(group, info))
        {
            info = UmbraEffects.Find("rainbow")!;
        }
        return (info, UmbraEffects.Resolve(group, state, info));
    }

    public static int Signature(byte[][] frames)
    {
        var hc = new HashCode();
        foreach (var f in frames)
        {
            hc.AddBytes(f);
        }
        return hc.ToHashCode();
    }
}
