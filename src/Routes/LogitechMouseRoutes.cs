using System;
using System.IO;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Models;
using Nexus.Service.Models.Devices;
using Nexus.Service.Peripherals.LogitechHidpp;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public static partial class DevicesRoutes
{
    // A Logitech mouse's onboard memory (G502 X PLUS): its profiles as G HUB's onboard memory
    // mode edits them. Every change answers with the state after it.
    private static void MapLogitechMouseEndpoints(WebApplication app)
    {
        app.MapGet("/devices/logitech-mouse", (IServiceProvider sp) =>
            Results.Json(Mouse(sp)?.GetState() ?? new LogitechMouseStateDto(), AppJsonContext.Default.LogitechMouseStateDto));

        // What the mouse uses right now (profile, DPI level), read from it; polled by the sensitivity page.
        app.MapGet("/devices/logitech-mouse/live", (IServiceProvider sp) =>
            Results.Json(Mouse(sp)?.GetLive() ?? new LogitechMouseLiveDto(), AppJsonContext.Default.LogitechMouseLiveDto));

        // The picture of the mouse the user keeps in its Nexus folder; 404 without one.
        app.MapGet("/devices/logitech-mouse/picture", (IServiceProvider sp) =>
            Mouse(sp)?.Picture() is { } picture && File.Exists(picture.Path)
                ? Results.File(File.ReadAllBytes(picture.Path), picture.ContentType,
                    lastModified: File.GetLastWriteTimeUtc(picture.Path))
                : Results.NotFound());

        app.MapPut("/devices/logitech-mouse/profiles/{number:int}", (int number, LogitechMouseProfileRequest body, IServiceProvider sp) =>
            MouseChange(sp, m => m.UpdateProfile(number, body)));

        app.MapPost("/devices/logitech-mouse/profiles/{number:int}/restore", (int number, IServiceProvider sp) =>
            MouseChange(sp, m => m.RestoreProfile(number)));

        app.MapPost("/devices/logitech-mouse/profiles/{number:int}/cycle", (int number, LogitechMouseCycleRequest body, IServiceProvider sp) =>
            MouseChange(sp, m => body.Enabled is { } on ? m.SetProfileEnabled(number, on) : MouseResult.Invalid("enabled is required")));

        app.MapPost("/devices/logitech-mouse/active", (LogitechMouseActiveRequest body, IServiceProvider sp) =>
            MouseChange(sp, m => body.Profile is { } n ? m.SetActiveProfile(n) : MouseResult.Invalid("profile is required")));

        app.MapPost("/devices/logitech-mouse/copy/save", (IServiceProvider sp) =>
            MouseChange(sp, m => m.SaveCopy()));

        app.MapPost("/devices/logitech-mouse/copy/restore", (IServiceProvider sp) =>
            MouseChange(sp, m => m.RestoreCopy()));

        app.MapPost("/devices/logitech-mouse/keep-onboard", (LogitechMouseKeepOnboardRequest body, IServiceProvider sp) =>
            MouseChange(sp, m => body.Enabled is { } on ? m.SetKeepOnboard(on) : MouseResult.Invalid("enabled is required")));

        app.MapPost("/devices/logitech-mouse/mode", (LogitechMouseModeRequest body, IServiceProvider sp) =>
            MouseChange(sp, m => m.SetMode(body.Mode)));
    }

    private static LogitechMouseService? Mouse(IServiceProvider sp) => sp.GetService<LogitechMouseService>();

    private static IResult MouseChange(IServiceProvider sp, Func<LogitechMouseService, MouseResult> change)
    {
        if (Mouse(sp) is not { } mouse)
        {
            return Results.Json(ApiResponse.Fail("no Logitech mouse support on this platform"), AppJsonContext.Default.ApiResponse, statusCode: StatusCodes.Status404NotFound);
        }
        var result = change(mouse);
        if (!result.Ok)
        {
            var status = result.Status switch
            {
                "invalid" => StatusCodes.Status400BadRequest,
                "not-present" => StatusCodes.Status404NotFound,
                _ => StatusCodes.Status502BadGateway,
            };
            return Results.Json(ApiResponse.Fail(result.Message ?? result.Status), AppJsonContext.Default.ApiResponse, statusCode: status);
        }
        return Results.Json(mouse.GetState(), AppJsonContext.Default.LogitechMouseStateDto);
    }
}
