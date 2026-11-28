using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Lighting;
using Nexus.Service.Models;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

/// <summary>One Quick Sync effect: what it offers, its defaults and the look it was last given.</summary>
public sealed class LianLiSyncEffectDto
{
    public string Key { get; set; } = "";
    /// <summary>
    /// One of Quick Sync's merged ("wireless") effects: drawn once across every device in <c>devices</c>
    /// order, each reading its stretch in its own direction; false for the six each device plays alone.
    /// </summary>
    public bool Merge { get; set; }
    public bool HasSpeed { get; set; }
    public bool HasDirection { get; set; }
    /// <summary>Colour pickers the effect shows; the colours sent must be exactly this many.</summary>
    public int ColorsMax { get; set; }
    public int DefaultSpeed { get; set; }
    public string[] DefaultColors { get; set; } = Array.Empty<string>();
    public int Speed { get; set; }
    public int Brightness { get; set; }
    public int Direction { get; set; }
    public string[] Colors { get; set; } = Array.Empty<string>();
}

/// <summary>A Lian Li device Quick Sync drives, and the effect it plays under sync.</summary>
public sealed class LianLiSyncDeviceDto
{
    /// <summary>"tlChain", "hydroShift2Curve" or "universalScreen88".</summary>
    public string Kind { get; set; } = "";
    /// <summary>The chain's MAC for a TL chain; the kind for the single USB devices.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int FanCount { get; set; }
    /// <summary>The device's own effect key it plays while sync is on.</summary>
    public string Mode { get; set; } = "";
    /// <summary>Its stretch of a merged effect's line: 13 per TL fan, 14 for the OLED Curved, 31 for the 8.8 bezel.</summary>
    public int Dots { get; set; }
    /// <summary>The direction it reads its stretch of a merged effect in: 0 as drawn, 1 reversed.</summary>
    public int Direction { get; set; }
}

/// <summary>One device in a new merge order, with its direction (null keeps it).</summary>
public sealed class LianLiSyncDeviceOrderDto
{
    public string Id { get; set; } = "";
    public int? Direction { get; set; }
}

public sealed class LianLiSyncResponse
{
    public bool Enabled { get; set; }
    public string Effect { get; set; } = "";
    /// <summary>Top of the speed and brightness levels (L-Connect's sliders at 0, 25, 50, 75, 100).</summary>
    public int MaxLevel { get; set; }
    public LianLiSyncEffectDto[] Effects { get; set; } = Array.Empty<LianLiSyncEffectDto>();
    /// <summary>The devices present, in merge order (L-Connect's: a device joins the end when first seen).</summary>
    public LianLiSyncDeviceDto[] Devices { get; set; } = Array.Empty<LianLiSyncDeviceDto>();
}

/// <summary>Patch; null fields keep their value. The look fields go to <see cref="Effect"/>, or to the current effect.</summary>
public sealed class LianLiSyncRequest
{
    public bool? Enabled { get; set; }
    public string? Effect { get; set; }
    public int? Speed { get; set; }
    public int? Brightness { get; set; }
    public int? Direction { get; set; }
    public string[]? Colors { get; set; }
    /// <summary>
    /// A new merge order: these devices first, in this order (each with its direction), then the others
    /// in their stored order.
    /// </summary>
    public LianLiSyncDeviceOrderDto[]? Devices { get; set; }
}

/// <summary>L-Connect's Quick Sync ("Sync all") over the Lian Li devices this PC drives.</summary>
public static partial class LianLiSyncRoutes
{
    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColor();

    public static void MapLianLiSyncEndpoints(this WebApplication app)
    {
        app.MapGet("/devices/lianli/sync", (IConfigStore store, LianLiMergeService merge) =>
        {
            var settings = store.Load();
            return Results.Json(BuildResponse(settings, merge.Devices(settings)), AppJsonContext.Default.LianLiSyncResponse);
        });

        app.MapPut("/devices/lianli/sync", (LianLiSyncRequest body, IConfigStore store, LianLiMergeService merge, Nexus.Service.Sockets.MultiplexHub mux) =>
        {
            var known = new List<string>();
            foreach (var d in merge.Devices(store.Load()))
            {
                known.Add(d.Id);
            }
            var current = store.Load().Devices.LianLiSync;
            foreach (var m in current.MergeDevices)
            {
                known.Add(m.Id);
            }
            var error = Validate(body, current, known);
            if (error is not null)
            {
                return Results.BadRequest(ApiResponse.Fail(error));
            }
            // The frame writers read the sync outside the store lock, so a new one is swapped in whole.
            store.Update(s => s.Devices.LianLiSync = Patch(s.Devices.LianLiSync, body));
            Nexus.Service.Sockets.PanelTopics.BroadcastLighting(mux);
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });
    }

    internal static string? Validate(LianLiSyncRequest body, LianLiSyncSettings current, IReadOnlyCollection<string>? knownDevices = null)
    {
        if (body.Effect is not null && !LianLiSync.IsEffect(body.Effect))
        {
            return "unknown effect";
        }
        var key = body.Effect ?? (LianLiSync.IsEffect(current.Effect) ? current.Effect : LianLiSync.Rainbow);
        var colorCount = LianLiSync.Find(key)?.DefaultColors.Count ?? LianLiMerge.Find(key)?.DefaultColors.Count ?? 0;
        if (body.Colors is not null)
        {
            if (body.Colors.Length != colorCount)
            {
                return $"{key} takes {colorCount} colors";
            }
            if (body.Colors.Any(c => !HexColor().IsMatch(c)))
            {
                return "invalid color";
            }
        }
        if (body.Devices is not null)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in body.Devices)
            {
                if (string.IsNullOrWhiteSpace(d.Id) || !seen.Add(d.Id))
                {
                    return "each device once";
                }
                if (knownDevices is not null && !knownDevices.Contains(d.Id, StringComparer.OrdinalIgnoreCase))
                {
                    return $"unknown device {d.Id}";
                }
                if (d.Direction is not (null or 0 or 1))
                {
                    return "direction is 0 or 1";
                }
            }
        }
        return null;
    }

    /// <summary>The sync after a validated patch; the look fields land on the patched effect's own look.</summary>
    internal static LianLiSyncSettings Patch(LianLiSyncSettings old, LianLiSyncRequest body)
    {
        var effect = body.Effect ?? (LianLiSync.IsEffect(old.Effect) ? old.Effect : LianLiSync.Rainbow);
        var looks = new Dictionary<string, LianLiSyncLook>(old.Looks, StringComparer.Ordinal);
        if (body.Speed.HasValue || body.Brightness.HasValue || body.Direction.HasValue || body.Colors is not null)
        {
            var look = LianLiSync.LookOf(old, effect);
            looks[effect] = new LianLiSyncLook
            {
                Speed = body.Speed.HasValue ? Math.Clamp(body.Speed.Value, 0, LianLiSync.MaxLevel) : look.Speed,
                Brightness = body.Brightness.HasValue ? Math.Clamp(body.Brightness.Value, 0, LianLiSync.MaxLevel) : look.Brightness,
                Direction = body.Direction.HasValue ? Math.Clamp(body.Direction.Value, 0, 1) : look.Direction,
                Colors = body.Colors is not null ? new List<string>(body.Colors) : new List<string>(look.Colors),
            };
        }
        return new LianLiSyncSettings
        {
            Enabled = body.Enabled ?? old.Enabled,
            Effect = effect,
            Looks = looks,
            MergeDevices = body.Devices is not null ? Reorder(old.MergeDevices, body.Devices) : old.MergeDevices,
        };
    }

    /// <summary>The merge order with <paramref name="order"/>'s devices first, in its order, then the rest as stored.</summary>
    internal static List<LianLiMergeMember> Reorder(IReadOnlyList<LianLiMergeMember> stored, IReadOnlyList<LianLiSyncDeviceOrderDto> order)
    {
        var result = new List<LianLiMergeMember>(stored.Count + order.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in order)
        {
            if (!seen.Add(o.Id))
            {
                continue;
            }
            var was = stored.FirstOrDefault(m => string.Equals(m.Id, o.Id, StringComparison.OrdinalIgnoreCase));
            result.Add(new LianLiMergeMember { Id = was?.Id ?? o.Id, Direction = Math.Clamp(o.Direction ?? was?.Direction ?? 0, 0, 1) });
        }
        foreach (var m in stored)
        {
            if (seen.Add(m.Id))
            {
                result.Add(new LianLiMergeMember { Id = m.Id, Direction = m.Direction });
            }
        }
        return result;
    }

    internal static LianLiSyncResponse BuildResponse(NexusSettings settings, IReadOnlyList<LianLiMerge.Participant> devices)
    {
        var sync = settings.Devices.LianLiSync;
        var effects = new List<LianLiSyncEffectDto>(LianLiSync.Effects.Count + LianLiMerge.Effects.Count);
        foreach (var e in LianLiSync.Effects)
        {
            effects.Add(EffectDto(sync, e.Key, merge: false, e.HasSpeed, e.HasDirection, e.DefaultSpeed, e.DefaultColors));
        }
        foreach (var e in LianLiMerge.Effects)
        {
            effects.Add(EffectDto(sync, e.Key, merge: true, e.HasSpeed, e.HasDirection, e.DefaultSpeed, e.DefaultColors));
        }
        var effect = LianLiSync.IsEffect(sync.Effect) ? sync.Effect : LianLiSync.Rainbow;
        var list = new List<LianLiSyncDeviceDto>(devices.Count);
        foreach (var d in devices)
        {
            list.Add(new LianLiSyncDeviceDto
            {
                Kind = d.Kind,
                Id = d.Id,
                Name = d.Kind switch
                {
                    LianLiMerge.KindTl => "Lian Li Wireless TL",
                    LianLiMerge.KindCurve => "HydroShift II OLED Curved",
                    _ => "Universal Screen 8.8",
                },
                FanCount = d.FanCount,
                Mode = effect,
                Dots = d.Dots,
                Direction = d.Direction,
            });
        }
        return new LianLiSyncResponse
        {
            Enabled = sync.Enabled,
            Effect = effect,
            MaxLevel = LianLiSync.MaxLevel,
            Effects = effects.ToArray(),
            Devices = list.ToArray(),
        };
    }

    private static LianLiSyncEffectDto EffectDto(LianLiSyncSettings sync, string key, bool merge, bool hasSpeed, bool hasDirection, int defaultSpeed, IReadOnlyList<string> defaultColors)
    {
        var look = LianLiSync.LookOf(sync, key);
        return new LianLiSyncEffectDto
        {
            Key = key,
            Merge = merge,
            HasSpeed = hasSpeed,
            HasDirection = hasDirection,
            ColorsMax = defaultColors.Count,
            DefaultSpeed = defaultSpeed,
            DefaultColors = defaultColors.ToArray(),
            Speed = look.Speed,
            Brightness = look.Brightness,
            Direction = look.Direction,
            Colors = look.Colors.ToArray(),
        };
    }
}
