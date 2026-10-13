using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Cooling;
using Nexus.Service.Lighting;
using Nexus.Service.Models;
using Nexus.Service.Peripherals.AsiahorseUmbra;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;

namespace Nexus.Service.Routes;

public static partial class DevicesRoutes
{
    private static readonly TimeSpan UmbraReconnectTimeout = TimeSpan.FromSeconds(10);

    private static void MapAsiahorseUmbraEndpoints(WebApplication app)
    {
        app.MapGet("/devices/asiahorse-umbra", (UmbraHub hub, IConfigStore store, AsiahorseUmbraCoolingProvider cooling) =>
            UmbraState(hub, store, cooling));

        app.MapPut("/devices/asiahorse-umbra/lighting", (
            UmbraLightingRequest body, UmbraHub hub, IConfigStore store, AsiahorseUmbraCoolingProvider cooling,
            AsiahorseUmbraLightingDeviceProvider lighting, MultiplexHub mux) =>
        {
            if (UmbraUnavailable(hub) is { } unavailable)
            {
                return unavailable;
            }
            var error = AsiahorseUmbraRouteLogic.ValidateLighting(body);
            if (error is not null)
            {
                return UmbraError(error, StatusCodes.Status400BadRequest);
            }
            store.Update(s => s.Devices.AsiahorseUmbra = AsiahorseUmbraRouteLogic.ApplyLighting(s.Devices.AsiahorseUmbra, body));
            return Changed(hub, store, cooling, lighting, mux);
        });

        app.MapPut("/devices/asiahorse-umbra/fan", (
            UmbraFanRequest body, UmbraHub hub, IConfigStore store, AsiahorseUmbraCoolingProvider cooling,
            AsiahorseUmbraLightingDeviceProvider lighting, MultiplexHub mux) =>
        {
            if (UmbraUnavailable(hub) is { } unavailable)
            {
                return unavailable;
            }
            if (body.SyncMb is null)
            {
                return UmbraError("syncMb is required", StatusCodes.Status400BadRequest);
            }
            var sync = body.SyncMb.Value;
            store.Update(s => s.Devices.AsiahorseUmbra.FanSyncMb = sync);
            if (!cooling.ApplyFanSync())
            {
                return UmbraError("the device did not accept the fan mode", StatusCodes.Status500InternalServerError);
            }
            return Changed(hub, store, cooling, lighting, mux);
        });

        app.MapPut("/devices/asiahorse-umbra/ports/{index:int}", async (
            int index, UmbraPortRequest body, UmbraHub hub, IConfigStore store, AsiahorseUmbraCoolingProvider cooling,
            AsiahorseUmbraLightingDeviceProvider lighting, MultiplexHub mux, CancellationToken ct) =>
        {
            if (UmbraUnavailable(hub) is { } unavailable)
            {
                return unavailable;
            }
            var (status, portError) = await AsiahorseUmbraRouteLogic.SetPortAsync(hub, index, body, UmbraReconnectTimeout, ct).ConfigureAwait(false);
            if (portError is not null)
            {
                return UmbraError(portError, status);
            }
            return Changed(hub, store, cooling, lighting, mux);
        });

        app.MapPost("/devices/asiahorse-umbra/auto-detect", async (
            UmbraHub hub, IConfigStore store, AsiahorseUmbraCoolingProvider cooling,
            AsiahorseUmbraLightingDeviceProvider lighting, MultiplexHub mux, CancellationToken ct) =>
        {
            if (UmbraUnavailable(hub) is { } unavailable)
            {
                return unavailable;
            }
            // Clear every manual count first: the next boot would otherwise re-apply a stale larger one.
            var restart = await hub.RestartAsync(UmbraReconnectTimeout, ct, AsiahorseUmbraRouteLogic.ClearManualCounts()).ConfigureAwait(false);
            if (RestartFailure(restart) is { } failure)
            {
                return failure;
            }
            hub.RefreshPorts();
            return Changed(hub, store, cooling, lighting, mux);
        });

        app.MapPut("/devices/asiahorse-umbra/boot", (
            UmbraBootRequest body, UmbraHub hub, IConfigStore store, AsiahorseUmbraCoolingProvider cooling,
            AsiahorseUmbraLightingDeviceProvider lighting, MultiplexHub mux) =>
        {
            if (UmbraUnavailable(hub) is { } unavailable)
            {
                return unavailable;
            }
            if (body.Mode is not null && body.Mode is not ("memory" or "fixed"))
            {
                return UmbraError("mode must be memory or fixed", StatusCodes.Status400BadRequest);
            }
            var ok = true;
            if (body.Mode == "memory")
            {
                ok = hub.SetBootMemory();
            }
            else if (body.Mode == "fixed")
            {
                ok = hub.SetBootFixed();
            }
            if (ok && body.SelfCheck is not null)
            {
                ok = hub.SetSelfCheck(body.SelfCheck.Value);
            }
            if (!ok)
            {
                return UmbraError("the device did not accept the boot setting", StatusCodes.Status500InternalServerError);
            }
            return Changed(hub, store, cooling, lighting, mux);
        });
    }

    private static IResult? RestartFailure(UmbraRestartResult restart) => restart switch
    {
        UmbraRestartResult.Busy => UmbraError("a controller restart is already running", StatusCodes.Status409Conflict),
        UmbraRestartResult.NoDevice => UmbraError("Asiahorse Umbra is not connected", StatusCodes.Status409Conflict),
        UmbraRestartResult.TimedOut => UmbraError("the device did not reconnect", StatusCodes.Status504GatewayTimeout),
        _ => null,
    };

    private static IResult? UmbraUnavailable(UmbraHub hub)
    {
        if (hub.IsRestarting)
        {
            return UmbraError("Asiahorse Umbra is restarting", StatusCodes.Status409Conflict);
        }
        return hub.IsConnected ? null : UmbraError("Asiahorse Umbra is not connected", StatusCodes.Status409Conflict);
    }

    private static IResult UmbraError(string message, int status) =>
        Results.Json(ApiResponse.Fail(message), AppJsonContext.Default.ApiResponse, statusCode: status);

    private static IResult Changed(
        UmbraHub hub, IConfigStore store, AsiahorseUmbraCoolingProvider cooling,
        AsiahorseUmbraLightingDeviceProvider lighting, MultiplexHub mux)
    {
        lighting.OnHubStateUpdated();
        PanelTopics.BroadcastLighting(mux);
        PanelTopics.BroadcastCooling(mux);
        return UmbraState(hub, store, cooling);
    }

    private static IResult UmbraState(UmbraHub hub, IConfigStore store, AsiahorseUmbraCoolingProvider cooling) =>
        Results.Json(AsiahorseUmbraRouteLogic.BuildState(hub.IsConnected, hub.Snapshot, store.Load(), cooling.Duty),
            AppJsonContext.Default.UmbraStateResponse);
}

internal static class AsiahorseUmbraRouteLogic
{
    public static string? ValidateLighting(UmbraLightingRequest body)
    {
        if (body.Source is not null && !UmbraLightingPlan.IsSource(body.Source))
        {
            return "source must be nexus, firmware or argbInput";
        }
        if (body.Group is not null && !UmbraEffects.IsGroup(body.Group))
        {
            return "group must be sync, async or merge";
        }
        return ValidatePatch(body.Sync, UmbraEffects.GroupSync)
            ?? ValidatePatch(body.Merge, UmbraEffects.GroupMerge)
            ?? ValidateAsync(body.Async);
    }

    private static string? ValidateAsync(UmbraAsyncPatch? patch)
    {
        if (patch is null)
        {
            return null;
        }
        if (patch.Port is null || patch.Port < 0 || patch.Port >= UmbraProtocol.PortCount)
        {
            return "async.port must be 0 to 9";
        }
        return ValidatePatch(patch, UmbraEffects.GroupAsync);
    }

    private static string? ValidatePatch(UmbraEffectPatch? patch, string group)
    {
        if (patch is null)
        {
            return null;
        }
        if (patch.Effect is not null)
        {
            var info = UmbraEffects.Find(patch.Effect);
            if (info is null)
            {
                return $"unknown effect {patch.Effect}";
            }
            if (!UmbraEffects.AllowedIn(group, info))
            {
                return $"{patch.Effect} is not available in the {group} group";
            }
        }
        if (patch.Speed is < 0 or > UmbraEffects.SpeedMax)
        {
            return "speed must be 0 to 5";
        }
        if (patch.Brightness is < UmbraEffects.BrightnessMin or > UmbraEffects.BrightnessMax)
        {
            return "brightness must be 1 to 5";
        }
        if (patch.Direction is not null && patch.Direction is not (UmbraEffects.DirectionForward or UmbraEffects.DirectionReverse))
        {
            return "direction must be forward or reverse";
        }
        if (patch.Colors is not null)
        {
            if (patch.Colors.Length > UmbraProtocol.ColorSlots)
            {
                return "at most 4 colors";
            }
            foreach (var c in patch.Colors)
            {
                if (!UmbraEffects.TryParseColor(c, out _, out _, out _))
                {
                    return "colors must be #rrggbb";
                }
            }
        }
        return null;
    }

    public static string? ValidatePort(int index, UmbraPortRequest body)
    {
        if (index < 0 || index >= UmbraProtocol.PortCount)
        {
            return "port index must be 0 to 9";
        }
        if (body.LedCount is < 0 or > UmbraProtocol.MaxLedCount)
        {
            return "ledCount must be 0 to 120 (0 uses the detected count)";
        }
        if (body.LedCount is not null && (body.Direction is not null || body.Order is not null))
        {
            return "ledCount restarts the controller and must be sent on its own";
        }
        if (body.Direction is not null && body.Direction is not (UmbraEffects.DirectionForward or UmbraEffects.DirectionReverse))
        {
            return "direction must be forward or reverse";
        }
        if (body.Order is < 1 or > UmbraProtocol.MaxOrder)
        {
            return "order must be 1 to 10";
        }
        return null;
    }

    // A count takes effect only at the next boot, so it restarts the controller; direction and order apply at once.
    internal static async Task<(int Status, string? Error)> SetPortAsync(
        UmbraHub hub, int index, UmbraPortRequest body, TimeSpan timeout, CancellationToken ct)
    {
        var error = ValidatePort(index, body);
        if (error is not null)
        {
            return (StatusCodes.Status400BadRequest, error);
        }
        foreach (var (field, value) in PortWrites(body))
        {
            if (!hub.WritePort(index, field, value))
            {
                return (StatusCodes.Status500InternalServerError, "the device did not accept the port setting");
            }
        }
        if (body.LedCount is not { } count)
        {
            return (StatusCodes.Status200OK, null);
        }
        var restart = await hub.RestartAsync(timeout, ct, UmbraProtocol.EncodeSetPort(index, UmbraPortField.LedCount, count)).ConfigureAwait(false);
        switch (restart)
        {
            case UmbraRestartResult.Busy:
                return (StatusCodes.Status409Conflict, "a controller restart is already running");
            case UmbraRestartResult.NoDevice:
                return (StatusCodes.Status409Conflict, "Asiahorse Umbra is not connected");
            case UmbraRestartResult.TimedOut:
                return (StatusCodes.Status504GatewayTimeout, "the device did not reconnect");
        }
        hub.RefreshPorts();
        // The firmware ignores a count at or below the detected one; clear it so it does not stay armed in flash.
        if (count != 0 && hub.Snapshot.Ports[index].LedCount != count)
        {
            hub.WritePort(index, UmbraPortField.LedCount, 0);
        }
        return (StatusCodes.Status200OK, null);
    }

    internal static byte[][] ClearManualCounts()
    {
        var frames = new byte[UmbraProtocol.PortCount][];
        for (var p = 0; p < frames.Length; p++)
        {
            frames[p] = UmbraProtocol.EncodeSetPort(p, UmbraPortField.LedCount, 0);
        }
        return frames;
    }

    internal static IEnumerable<(UmbraPortField Field, int Value)> PortWrites(UmbraPortRequest body)
    {
        if (body.Direction is not null)
        {
            yield return (UmbraPortField.Direction, body.Direction == UmbraEffects.DirectionForward ? 1 : 0);
        }
        if (body.Order is not null)
        {
            yield return (UmbraPortField.Order, body.Order.Value - 1);
        }
    }

    // Returns new settings objects; the lighting writer reads the live ones lock-free.
    public static AsiahorseUmbraSettings ApplyLighting(AsiahorseUmbraSettings current, UmbraLightingRequest body)
    {
        var next = new AsiahorseUmbraSettings
        {
            Source = body.Source ?? current.Source,
            LightOn = body.LightOn ?? current.LightOn,
            Group = body.Group ?? current.Group,
            Sync = ApplyPatch(current.Sync ?? new UmbraEffectGroupSettings(), body.Sync, UmbraEffects.GroupSync),
            Merge = ApplyPatch(current.Merge ?? new UmbraEffectGroupSettings(), body.Merge, UmbraEffects.GroupMerge),
            Async = current.Async is null ? new Dictionary<string, UmbraEffectGroupSettings>() : new Dictionary<string, UmbraEffectGroupSettings>(current.Async),
            FanSyncMb = current.FanSyncMb,
        };
        if (body.Async is { Port: not null } async)
        {
            var key = async.Port.Value.ToString(CultureInfo.InvariantCulture);
            next.Async.TryGetValue(key, out var existing);
            next.Async[key] = ApplyPatch(existing ?? new UmbraEffectGroupSettings(), async, UmbraEffects.GroupAsync);
        }
        return next;
    }

    // Selecting an effect restores its stored values; the other fields edit the selected effect's values.
    private static UmbraEffectGroupSettings ApplyPatch(UmbraEffectGroupSettings current, UmbraEffectPatch? patch, string group)
    {
        if (patch is null)
        {
            return current;
        }
        var effectKey = patch.Effect ?? current.Effect;
        var info = UmbraEffects.Find(effectKey);
        if (info is null || !UmbraEffects.AllowedIn(group, info))
        {
            info = UmbraEffects.Find("rainbow")!;
        }
        var selected = new UmbraEffectGroupSettings { Effect = info.Key, Values = current.Values is null ? new Dictionary<string, UmbraEffectValues>() : new Dictionary<string, UmbraEffectValues>(current.Values) };
        var values = UmbraEffects.Resolve(group, selected, info);
        if (patch.Speed is not null)
        {
            values.Speed = patch.Speed.Value;
        }
        if (patch.Brightness is not null)
        {
            values.Brightness = patch.Brightness.Value;
        }
        if (patch.Direction is not null)
        {
            values.Direction = patch.Direction;
        }
        if (patch.Colors is not null)
        {
            for (var i = 0; i < patch.Colors.Length; i++)
            {
                values.Colors[i] = patch.Colors[i].ToLowerInvariant();
            }
        }
        selected.Values[info.Key] = values;
        return selected;
    }

    public static UmbraStateResponse BuildState(bool connected, UmbraSnapshot snap, NexusSettings settings, int fanDuty)
    {
        var s = settings.Devices.AsiahorseUmbra;
        var ports = new UmbraPortDto[snap.Ports.Length];
        for (var i = 0; i < ports.Length; i++)
        {
            var p = snap.Ports[i];
            ports[i] = new UmbraPortDto
            {
                Index = i,
                LedCount = p.LedCount,
                Direction = p.Forward ? UmbraEffects.DirectionForward : UmbraEffects.DirectionReverse,
                Order = p.Order + 1,
            };
        }
        var async = new List<UmbraAsyncEffectDto>();
        for (var i = 0; i < snap.Ports.Length; i++)
        {
            if (snap.Ports[i].LedCount == 0)
            {
                continue;
            }
            UmbraEffectGroupSettings? state = null;
            s.Async?.TryGetValue(i.ToString(CultureInfo.InvariantCulture), out state);
            var (info, values) = UmbraLightingPlan.Current(UmbraEffects.GroupAsync, state);
            async.Add(new UmbraAsyncEffectDto
            {
                Port = i,
                Effect = info.Key,
                Speed = values.Speed,
                Brightness = values.Brightness,
                Direction = values.Direction,
                Colors = values.Colors.ToArray(),
            });
        }
        var effects = new UmbraEffectDto[UmbraEffects.Catalog.Length];
        for (var i = 0; i < effects.Length; i++)
        {
            var e = UmbraEffects.Catalog[i];
            effects[i] = new UmbraEffectDto { Key = e.Key, Colors = e.Colors, Speed = e.HasSpeed, Direction = e.HasDirection, Merge = e.InMerge };
        }
        return new UmbraStateResponse
        {
            IsConnected = connected,
            DeviceId = snap.DeviceId,
            Firmware = snap.Firmware,
            Lighting = new UmbraLightingDto
            {
                Source = UmbraLightingPlan.SourceOf(s),
                LightOn = s.LightOn,
                Group = UmbraEffects.IsGroup(s.Group) ? s.Group : UmbraEffects.GroupSync,
                Sync = EffectState(UmbraEffects.GroupSync, s.Sync),
                Merge = EffectState(UmbraEffects.GroupMerge, s.Merge),
                Async = async.ToArray(),
            },
            Fan = new UmbraFanDto { SyncMb = s.FanSyncMb, Duty = fanDuty },
            Boot = new UmbraBootDto { Mode = snap.BootMemory ? "memory" : "fixed", SelfCheck = snap.SelfCheck },
            Ports = ports,
            Effects = effects,
        };
    }

    private static UmbraEffectStateDto EffectState(string group, UmbraEffectGroupSettings state)
    {
        var (info, values) = UmbraLightingPlan.Current(group, state);
        return new UmbraEffectStateDto
        {
            Effect = info.Key,
            Speed = values.Speed,
            Brightness = values.Brightness,
            Direction = values.Direction,
            Colors = values.Colors.ToArray(),
        };
    }
}

public class UmbraEffectStateDto
{
    public string Effect { get; set; } = "";
    public int Speed { get; set; }
    public int Brightness { get; set; }
    public string Direction { get; set; } = UmbraEffects.DirectionForward;
    public string[] Colors { get; set; } = Array.Empty<string>();
}

public sealed class UmbraAsyncEffectDto : UmbraEffectStateDto
{
    public int Port { get; set; }
}

public sealed class UmbraLightingDto
{
    public string Source { get; set; } = UmbraLightingPlan.SourceNexus;
    public bool LightOn { get; set; }
    public string Group { get; set; } = UmbraEffects.GroupSync;
    public UmbraEffectStateDto Sync { get; set; } = new();
    public UmbraEffectStateDto Merge { get; set; } = new();
    public UmbraAsyncEffectDto[] Async { get; set; } = Array.Empty<UmbraAsyncEffectDto>();
}

public sealed class UmbraFanDto
{
    public bool SyncMb { get; set; }
    public int Duty { get; set; }
}

public sealed class UmbraBootDto
{
    public string Mode { get; set; } = "memory";
    public bool SelfCheck { get; set; }
}

public sealed class UmbraPortDto
{
    public int Index { get; set; }
    public int LedCount { get; set; }
    public string Direction { get; set; } = UmbraEffects.DirectionForward;
    public int Order { get; set; }
}

public sealed class UmbraEffectDto
{
    public string Key { get; set; } = "";
    public int Colors { get; set; }
    public bool Speed { get; set; }
    public bool Direction { get; set; }
    public bool Merge { get; set; }
}

public sealed class UmbraStateResponse
{
    public bool IsConnected { get; set; }
    public string DeviceId { get; set; } = "";
    public string Firmware { get; set; } = "";
    public UmbraLightingDto Lighting { get; set; } = new();
    public UmbraFanDto Fan { get; set; } = new();
    public UmbraBootDto Boot { get; set; } = new();
    public UmbraPortDto[] Ports { get; set; } = Array.Empty<UmbraPortDto>();
    public UmbraEffectDto[] Effects { get; set; } = Array.Empty<UmbraEffectDto>();
}

public class UmbraEffectPatch
{
    public string? Effect { get; set; }
    public int? Speed { get; set; }
    public int? Brightness { get; set; }
    public string? Direction { get; set; }
    public string[]? Colors { get; set; }
}

public sealed class UmbraAsyncPatch : UmbraEffectPatch
{
    public int? Port { get; set; }
}

public sealed class UmbraLightingRequest
{
    public string? Source { get; set; }
    public bool? LightOn { get; set; }
    public string? Group { get; set; }
    public UmbraEffectPatch? Sync { get; set; }
    public UmbraEffectPatch? Merge { get; set; }
    public UmbraAsyncPatch? Async { get; set; }
}

public sealed class UmbraFanRequest
{
    public bool? SyncMb { get; set; }
}

public sealed class UmbraPortRequest
{
    public int? LedCount { get; set; }
    public string? Direction { get; set; }
    public int? Order { get; set; }
}

public sealed class UmbraBootRequest
{
    public string? Mode { get; set; }
    public bool? SelfCheck { get; set; }
}
