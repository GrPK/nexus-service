using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Models;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public static partial class DevicesRoutes
{
    private static void MapUniversalScreen88LightingEndpoints(WebApplication app)
    {
        app.MapGet("/devices/lianli-screen88/lighting", (IConfigStore store, UniversalScreen88LedBoard board) =>
        {
            var ls = store.Load().Devices.UniversalScreen88Lighting;
            var modes = new UniversalScreen88ModeDto[UniversalScreen88LedEffects.Modes.Count];
            for (int i = 0; i < modes.Length; i++)
            {
                var key = UniversalScreen88LedEffects.Modes[i];
                modes[i] = new UniversalScreen88ModeDto
                {
                    Key = key,
                    Colors = UniversalScreen88LedEffects.ColorCount(key),
                    DefaultColors = UniversalScreen88LedEffects.DefaultColors(key).ToArray(),
                    HasDirection = UniversalScreen88LedEffects.HasDirection(key),
                    HasSpeed = UniversalScreen88LedEffects.HasSpeed(key),
                    HasBrightness = true,
                };
            }
            return Results.Json(
                new UniversalScreen88LightingResponse
                {
                    Connected = board.IsAvailable,
                    Firmware = board.Firmware,
                    Mode = ls.Mode,
                    EffectMode = ls.Mode != UniversalScreen88LightingSettings.CanvasMode
                        ? ls.Mode
                        : ls.EffectMode ?? UniversalScreen88LedEffects.Rainbow,
                    Speed = ls.Speed,
                    Direction = ls.Direction,
                    Brightness = ls.Brightness,
                    MaxLevel = UniversalScreen88LedEffects.MaxLevel,
                    Colors = ls.Colors.ToArray(),
                    Modes = modes,
                },
                AppJsonContext.Default.UniversalScreen88LightingResponse);
        });

        app.MapPut("/devices/lianli-screen88/lighting", (
            UniversalScreen88LightingRequest body,
            IConfigStore store,
            Nexus.Service.Sockets.MultiplexHub mux) =>
        {
            if (body.Mode is { } mode
                && mode != UniversalScreen88LightingSettings.CanvasMode
                && !UniversalScreen88LedEffects.Modes.Contains(mode))
            {
                return Results.BadRequest(ApiResponse.Fail("unknown mode key"));
            }
            if (body.Colors is { } colors && (colors.Length > 6 || Array.Exists(colors, c => !HexColor().IsMatch(c))))
            {
                return Results.BadRequest(ApiResponse.Fail("colors must be at most six #rrggbb values"));
            }
            store.Update(s =>
            {
                var ls = s.Devices.UniversalScreen88Lighting;
                if (body.Mode is { } m)
                {
                    ls.Mode = m;
                    if (m != UniversalScreen88LightingSettings.CanvasMode) ls.EffectMode = m;
                }
                if (body.Speed is { } speed) ls.Speed = Math.Clamp(speed, 0, UniversalScreen88LedEffects.MaxLevel);
                if (body.Direction is { } direction) ls.Direction = Math.Clamp(direction, 0, 1);
                if (body.Brightness is { } brightness) ls.Brightness = Math.Clamp(brightness, 0, UniversalScreen88LedEffects.MaxLevel);
                if (body.Colors is { } c) ls.Colors = new List<string>(c);
            });
            Nexus.Service.Sockets.PanelTopics.BroadcastLighting(mux);
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });
    }
}

public sealed class UniversalScreen88ModeDto
{
    public string Key { get; set; } = "";
    /// <summary>Colour pickers the mode shows; 0 when it draws its own colours.</summary>
    public int Colors { get; set; }
    /// <summary>L-Connect's colours for the mode, used while the palette is empty.</summary>
    public string[] DefaultColors { get; set; } = Array.Empty<string>();
    public bool HasDirection { get; set; }
    public bool HasSpeed { get; set; }
    public bool HasBrightness { get; set; }
}

public sealed class UniversalScreen88LightingResponse
{
    /// <summary>The bezel LED board (0416:8050) is open.</summary>
    public bool Connected { get; set; }
    public string? Firmware { get; set; }
    public string Mode { get; set; } = "";
    /// <summary>The bezel effect played when Lighting page control is off.</summary>
    public string EffectMode { get; set; } = "";
    public int Speed { get; set; }
    public int Direction { get; set; }
    public int Brightness { get; set; }
    /// <summary>Top of the speed and brightness levels.</summary>
    public int MaxLevel { get; set; }
    public string[] Colors { get; set; } = Array.Empty<string>();
    public UniversalScreen88ModeDto[] Modes { get; set; } = Array.Empty<UniversalScreen88ModeDto>();
}

public sealed class UniversalScreen88LightingRequest
{
    public string? Mode { get; set; }
    public int? Speed { get; set; }
    public int? Direction { get; set; }
    public int? Brightness { get; set; }
    public string[]? Colors { get; set; }
}
