using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Widgets.AppActions;

/// <summary>
/// The user's unit choices (temperature unit, 12h/24h, number and date format)
/// for apps, so an app shows times and dates the way the rest of Nexus does.
/// Values are echoed as stored, with the same keys as GET /preferences units.
/// </summary>
public static class UnitsActions
{
    public static void RegisterAll(AppActionRegistry registry)
    {
        registry.Register("prefs.units", (services, _, _) =>
        {
            var units = services.GetRequiredService<IConfigStore>().Load().Units ?? new UnitsSettings();
            var json = JsonSerializer.Serialize(units, AppJsonContext.Default.UnitsSettings);
            using var doc = JsonDocument.Parse(json);
            return Task.FromResult<JsonElement?>(doc.RootElement.Clone());
        });
    }

    public static IReadOnlyList<string> AllActions => new[] { "prefs.units" };
}
