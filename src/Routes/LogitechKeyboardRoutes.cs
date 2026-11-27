using System;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Models;
using Nexus.Service.Models.Devices;
using Nexus.Service.Peripherals.LogitechHidpp;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public static partial class DevicesRoutes
{
    // A Logitech keyboard's key assignments (G915 X): the BASE / FN / G-SHIFT layers of G HUB's
    // KEYCONTROL, kept by Nexus and written to the keyboard. Every change answers with the state.
    private static void MapLogitechKeyboardEndpoints(WebApplication app)
    {
        app.MapGet("/devices/logitech-keyboard", (IServiceProvider sp) =>
            Results.Json(Keyboard(sp)?.GetState() ?? new LogitechKeyboardStateDto(), AppJsonContext.Default.LogitechKeyboardStateDto));

        // The keyboard's picture from the user's own G HUB install (never shipped with Nexus); 404 without one.
        app.MapGet("/devices/logitech-keyboard/image", (string? layout) =>
            LogitechKeyboardService.Photo(layout) is { } photo
                ? Results.File(photo.Bytes, photo.ContentType, entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{photo.Bytes.Length:x}-{layout}\""))
                : Results.NotFound());

        app.MapPut("/devices/logitech-keyboard/layers", (LogitechKeyboardLayersDto body, IServiceProvider sp) =>
            KeyboardChange(sp, k => k.SetLayers(body)));

        app.MapPost("/devices/logitech-keyboard/rewrite", (IServiceProvider sp) =>
            KeyboardChange(sp, k => k.Rewrite()));

        // A copy of the assignments kept in Nexus: save the current ones, or write the saved ones back.
        app.MapPost("/devices/logitech-keyboard/copy/save", (IServiceProvider sp) =>
            KeyboardChange(sp, k => k.SaveCopy()));

        app.MapPost("/devices/logitech-keyboard/copy/restore", (IServiceProvider sp) =>
            KeyboardChange(sp, k => k.RestoreCopy()));

        app.MapPost("/devices/logitech-keyboard/enabled", (LogitechKeyboardEnabledRequest body, IServiceProvider sp) =>
            KeyboardChange(sp, k => body.Enabled is { } on ? k.SetEnabled(on) : MouseResult.Invalid("enabled is required")));

        app.MapPost("/devices/logitech-keyboard/layout", (LogitechKeyboardLayoutRequest body, IServiceProvider sp) =>
            KeyboardChange(sp, k => k.SetLayout(body.Layout)));
    }

    private static LogitechKeyboardService? Keyboard(IServiceProvider sp) => sp.GetService<LogitechKeyboardService>();

    private static IResult KeyboardChange(IServiceProvider sp, Func<LogitechKeyboardService, MouseResult> change)
    {
        if (Keyboard(sp) is not { } keyboard)
        {
            return Results.Json(ApiResponse.Fail("no Logitech keyboard support on this platform"), AppJsonContext.Default.ApiResponse, statusCode: StatusCodes.Status404NotFound);
        }
        var result = change(keyboard);
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
        return Results.Json(keyboard.GetState(), AppJsonContext.Default.LogitechKeyboardStateDto);
    }
}
