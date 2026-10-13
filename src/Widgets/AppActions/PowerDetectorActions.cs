using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Sensors.Astral;
using Nexus.Service.Serialization;
using static Nexus.Service.Widgets.AppActions.AppActionHelpers;

namespace Nexus.Service.Widgets.AppActions;

/// <summary>
/// The ROG Astral Power Detector for apps: its state (pins, level, countdown,
/// latest alert, settings), a change to its settings, and the two answers to a
/// shutdown countdown, cancel or shut down now. Neither can start a shutdown:
/// shut down now only cuts short a countdown the detector started.
/// </summary>
public static class PowerDetectorActions
{
    public static void RegisterAll(AppActionRegistry registry)
    {
        registry.Register("powerdetector.state", (services, _, _) =>
            Task.FromResult<JsonElement?>(State(services.GetService<AstralPowerDetectorService>()?.GetState())));

        registry.Register("powerdetector.setConfig", (services, args, _) =>
        {
            var detector = services.GetService<AstralPowerDetectorService>();
            if (detector is null)
            {
                return Task.FromResult<JsonElement?>(Ack(false, "no power detector on this platform"));
            }
            var body = new PowerDetectorConfigBody
            {
                TrayAlerts = Bool(args, "trayAlerts"),
                InAppAlerts = Bool(args, "inAppAlerts"),
                Equalizer = Bool(args, "equalizer"),
                HideEqualizerReminder = Bool(args, "hideEqualizerReminder"),
                OvercurrentShutdown = Bool(args, "overcurrentShutdown"),
                OvercurrentSeconds = Seconds(args, "overcurrentSeconds"),
                CountdownSeconds = Seconds(args, "countdownSeconds"),
                CriticalShutdown = Bool(args, "criticalShutdown"),
            };
            return Task.FromResult<JsonElement?>(State(detector.SetConfig(body)));
        });

        registry.Register("powerdetector.cancel", (services, _, _) =>
            Task.FromResult<JsonElement?>(Ack(services.GetService<AstralPowerDetectorService>()?.Cancel() ?? false)));

        registry.Register("powerdetector.shutdownNow", (services, _, _) =>
            Task.FromResult<JsonElement?>(Ack(services.GetService<AstralPowerDetectorService>()?.ShutdownNow() ?? false)));
    }

    public static IReadOnlyList<string> AllActions => new[]
    {
        "powerdetector.state", "powerdetector.setConfig", "powerdetector.cancel", "powerdetector.shutdownNow",
    };

    private static int? Seconds(Dictionary<string, JsonElement>? args, string key) =>
        Num(args, key) is { } value && double.IsFinite(value) ? (int)Math.Round(Math.Clamp(value, 0, int.MaxValue)) : null;

    private static JsonElement State(PowerDetectorStateDto? state)
    {
        var json = JsonSerializer.Serialize(state ?? new PowerDetectorStateDto(), AppJsonContext.Default.PowerDetectorStateDto);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
