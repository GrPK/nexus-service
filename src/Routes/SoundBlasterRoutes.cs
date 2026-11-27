using System;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Models;
using Nexus.Service.Models.Devices;
using Nexus.Service.Peripherals.CreativeAudio;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public static partial class DevicesRoutes
{
    // A Creative Sound Blaster (G8, G6): Creative App's SBX acoustic engine, EQ, Scout Mode, mic,
    // output and DAC filter. Every change answers with the state after it.
    private static void MapSoundBlasterEndpoints(WebApplication app)
    {
        app.MapGet("/devices/soundblaster", (IServiceProvider sp) =>
            Results.Json(SoundBlaster(sp)?.GetState() ?? new SoundBlasterStateDto(), AppJsonContext.Default.SoundBlasterStateDto));

        app.MapPut("/devices/soundblaster/parameter", (SoundBlasterParameterRequest body, IServiceProvider sp) =>
            SoundBlasterChange(sp, s => s.SetParameter(body.Family, body.Id, body.Value)));

        app.MapPost("/devices/soundblaster/sbx", (SoundBlasterToggleRequest body, IServiceProvider sp) =>
            SoundBlasterChange(sp, s => body.Enabled is { } on ? s.SetSbx(on) : SoundBlasterResult.Invalid("enabled is required")));

        app.MapPost("/devices/soundblaster/scout", (SoundBlasterToggleRequest body, IServiceProvider sp) =>
            SoundBlasterChange(sp, s => body.Enabled is { } on ? s.SetScout(on) : SoundBlasterResult.Invalid("enabled is required")));

        app.MapPost("/devices/soundblaster/output", (SoundBlasterOutputRequest body, IServiceProvider sp) =>
            SoundBlasterChange(sp, s => s.SetOutput(body.Output)));

        app.MapPost("/devices/soundblaster/filter", (SoundBlasterValueRequest body, IServiceProvider sp) =>
            SoundBlasterChange(sp, s => s.SetFilter(body.Value)));

        app.MapPost("/devices/soundblaster/mic-boost", (SoundBlasterValueRequest body, IServiceProvider sp) =>
            SoundBlasterChange(sp, s => s.SetMicBoost(body.Value)));
    }

    private static SoundBlasterService? SoundBlaster(IServiceProvider sp) => sp.GetService<SoundBlasterService>();

    private static IResult SoundBlasterChange(IServiceProvider sp, Func<SoundBlasterService, SoundBlasterResult> change)
    {
        if (SoundBlaster(sp) is not { } device)
        {
            return Results.Json(ApiResponse.Fail("no Sound Blaster support on this platform"), AppJsonContext.Default.ApiResponse, statusCode: StatusCodes.Status404NotFound);
        }
        var result = change(device);
        if (result.Ok)
        {
            return Results.Json(device.GetState(), AppJsonContext.Default.SoundBlasterStateDto);
        }
        var status = result.Status switch
        {
            "invalid" => StatusCodes.Status400BadRequest,
            "not-present" => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status409Conflict,
        };
        return Results.Json(ApiResponse.Fail(result.Message ?? result.Status), AppJsonContext.Default.ApiResponse, statusCode: status);
    }
}
