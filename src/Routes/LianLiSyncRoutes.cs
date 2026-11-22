using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Lighting;
using Nexus.Service.Models;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

/// <summary>One Quick Sync effect: what it offers, its defaults and the look it was last given.</summary>
public sealed class LianLiSyncEffectDto
{
    public string Key { get; set; } = "";
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
}

public sealed class LianLiSyncResponse
{
    public bool Enabled { get; set; }
    public string Effect { get; set; } = "";
    /// <summary>Top of the speed and brightness levels (L-Connect's sliders at 0, 25, 50, 75, 100).</summary>
    public int MaxLevel { get; set; }
    public LianLiSyncEffectDto[] Effects { get; set; } = Array.Empty<LianLiSyncEffectDto>();
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
}

/// <summary>L-Connect's Quick Sync ("Sync all") over the Lian Li devices this PC drives.</summary>
public static partial class LianLiSyncRoutes
{
    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColor();

    public static void MapLianLiSyncEndpoints(this WebApplication app)
    {
        app.MapGet("/devices/lianli/sync", (IConfigStore store, Slv3Hub hub, HydroShift2CurveBoard curve, UniversalScreen88LedBoard screen88) =>
            Results.Json(BuildResponse(store.Load(), hub.State, curve.IsAvailable, screen88.IsAvailable), AppJsonContext.Default.LianLiSyncResponse));

        app.MapPut("/devices/lianli/sync", (LianLiSyncRequest body, IConfigStore store, Nexus.Service.Sockets.MultiplexHub mux) =>
        {
            var error = Validate(body, store.Load().Devices.LianLiSync);
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

    internal static string? Validate(LianLiSyncRequest body, LianLiSyncSettings current)
    {
        if (body.Effect is not null && LianLiSync.Find(body.Effect) is null)
        {
            return "unknown effect";
        }
        var info = LianLiSync.Find(body.Effect ?? current.Effect) ?? LianLiSync.Effects[0];
        if (body.Colors is not null)
        {
            if (body.Colors.Length != info.DefaultColors.Count)
            {
                return $"{info.Key} takes {info.DefaultColors.Count} colors";
            }
            if (body.Colors.Any(c => !HexColor().IsMatch(c)))
            {
                return "invalid color";
            }
        }
        return null;
    }

    /// <summary>The sync after a validated patch; the look fields land on the patched effect's own look.</summary>
    internal static LianLiSyncSettings Patch(LianLiSyncSettings old, LianLiSyncRequest body)
    {
        var effect = body.Effect ?? (LianLiSync.Find(old.Effect) is null ? LianLiSync.Rainbow : old.Effect);
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
        };
    }

    internal static LianLiSyncResponse BuildResponse(NexusSettings settings, Slv3State wireless, bool curve, bool screen88)
    {
        var sync = settings.Devices.LianLiSync;
        var effects = LianLiSync.Effects.Select(e =>
        {
            var look = LianLiSync.LookOf(sync, e.Key);
            return new LianLiSyncEffectDto
            {
                Key = e.Key,
                HasSpeed = e.HasSpeed,
                HasDirection = e.HasDirection,
                ColorsMax = e.DefaultColors.Count,
                DefaultSpeed = e.DefaultSpeed,
                DefaultColors = e.DefaultColors.ToArray(),
                Speed = look.Speed,
                Brightness = look.Brightness,
                Direction = look.Direction,
                Colors = look.Colors.ToArray(),
            };
        }).ToArray();
        var effect = LianLiSync.Find(sync.Effect)?.Key ?? LianLiSync.Rainbow;
        var devices = new List<LianLiSyncDeviceDto>();
        foreach (var fan in wireless.Fans)
        {
            if (!fan.BoundToUs || fan.FanCount <= 0 || Slv3Protocol.IsStrimerDevType((byte)fan.DevType) || Slv3Protocol.IsHydroShiftDevType((byte)fan.DevType)
                || !Slv3FanEffects.IsTlFamily(Slv3Protocol.ClassifyFanFamily((byte)fan.FanType)))
            {
                continue;
            }
            devices.Add(new LianLiSyncDeviceDto { Kind = "tlChain", Id = fan.Mac.ToUpperInvariant(), Name = "Lian Li Wireless TL", FanCount = fan.FanCount, Mode = effect });
        }
        if (curve)
        {
            devices.Add(new LianLiSyncDeviceDto { Kind = "hydroShift2Curve", Id = "hydroShift2Curve", Name = "HydroShift II OLED Curved", Mode = effect });
        }
        if (screen88)
        {
            devices.Add(new LianLiSyncDeviceDto { Kind = "universalScreen88", Id = "universalScreen88", Name = "Universal Screen 8.8", Mode = effect });
        }
        return new LianLiSyncResponse
        {
            Enabled = sync.Enabled,
            Effect = effect,
            MaxLevel = LianLiSync.MaxLevel,
            Effects = effects,
            Devices = devices.ToArray(),
        };
    }
}
