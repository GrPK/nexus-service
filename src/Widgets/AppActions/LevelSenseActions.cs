using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Models.Widgets;
using Nexus.Service.Routes;
using Nexus.Service.Sensors;
using Nexus.Service.Serialization;

namespace Nexus.Service.Widgets.AppActions;

/// <summary>
/// ROG Astral Level Sense for apps: read each card's calibration, and take a new
/// one (the card's current position becomes level), the same as
/// POST /diagnostics/gpu/level-sense/calibrate.
/// </summary>
public static class LevelSenseActions
{
    public static void RegisterAll(AppActionRegistry registry)
    {
        registry.Register("levelsense.state", (services, _, _) =>
        {
            var sensors = services.GetRequiredService<ISensorProvider>();
            var dto = new LevelSenseStateDto();
            foreach (var state in sensors.GetGpuLevelSenseStates())
            {
                dto.Gpus.Add(new LevelSenseGpuStateDto
                {
                    GpuId = state.GpuId,
                    Name = state.Name,
                    Calibrating = state.Calibrating,
                    CalibratedAtMs = state.CalibratedAtMs,
                });
            }
            return Task.FromResult<JsonElement?>(ToElement(JsonSerializer.Serialize(dto, AppJsonContext.Default.LevelSenseStateDto)));
        });

        registry.Register("levelsense.calibrate", (services, _, _) =>
        {
            var sensors = services.GetRequiredService<ISensorProvider>();
            // A calibration already running is left to finish: starting it over on
            // every press would keep it from ever completing.
            var running = sensors.GetGpuLevelSenseStates().Where(s => s.Calibrating).Select(s => s.GpuId).ToList();
            var gpus = running.Count > 0 ? running : sensors.RecalibrateGpuLevelSense(null);
            var response = new LevelSenseCalibrateResponse { Gpus = gpus };
            return Task.FromResult<JsonElement?>(ToElement(JsonSerializer.Serialize(response, AppJsonContext.Default.LevelSenseCalibrateResponse)));
        });
    }

    public static IReadOnlyList<string> AllActions => new[] { "levelsense.state", "levelsense.calibrate" };

    private static JsonElement ToElement(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
