using System;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Models;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public static partial class DevicesRoutes
{
    private static void MapUniversalScreen88Endpoints(WebApplication app)
    {
        app.MapGet("/devices/lianli-screen88/settings", (IServiceProvider services, IConfigStore store, HardwarePresence presence) =>
        {
            var hub = Screen88Hub(services);
            var s = store.Load().Devices.UniversalScreen88;
            return Results.Json(
                new UniversalScreen88SettingsResponse
                {
                    Connected = hub?.IsConnected == true,
                    Firmware = (hub?.Driver as UniversalScreen88Driver)?.Firmware,
                    InWindowsDisplay = presence.UsbPresent(UniversalScreen88Protocol.DesktopVendorId, UniversalScreen88Protocol.DesktopProductId),
                    OfflineMode = s.OfflineMode,
                    WindowsDisplay = s.WindowsDisplay,
                },
                AppJsonContext.Default.UniversalScreen88SettingsResponse);
        });

        app.MapPut("/devices/lianli-screen88/settings", (UniversalScreen88SettingsRequest body, IServiceProvider services, IConfigStore store) =>
        {
            store.Update(s =>
            {
                var g = s.Devices.UniversalScreen88;
                g.OfflineMode = body.OfflineMode ?? g.OfflineMode;
                g.WindowsDisplay = body.WindowsDisplay ?? g.WindowsDisplay;
            });
            var hub = Screen88Hub(services);
            if (hub?.Driver is UniversalScreen88Driver driver)
            {
                if (body.OfflineMode is { } offline
                    && !hub.Exchange(pipe => driver.ApplyOfflineMode(pipe, offline), false))
                {
                    ServiceLog.Info($"[{UniversalScreen88Driver.Id}] offline mode {(offline ? "on" : "off")} sent at the next connect");
                }
                // Into the Windows display mode happens in the handshake; back out of it is the desktop-mode exit's next poll.
                if (body.WindowsDisplay == true && hub.IsConnected)
                {
                    driver.RequestHandshake();
                }
            }
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });
    }

    private static BulkPanelHub? Screen88Hub(IServiceProvider services) =>
        services.GetServices<BulkPanelHub>().FirstOrDefault(h => h.Driver is UniversalScreen88Driver);
}

public sealed class UniversalScreen88SettingsResponse
{
    /// <summary>The glass is attached as a panel under Nexus Control.</summary>
    public bool Connected { get; set; }
    public string? Firmware { get; set; }
    /// <summary>The glass is in its own Windows display mode now (1A86:AD21 present).</summary>
    public bool InWindowsDisplay { get; set; }
    /// <summary>Null until set in Nexus; the glass's own flag is then left as found.</summary>
    public bool? OfflineMode { get; set; }
    /// <summary>Null leaves the mode as found; true wants the Windows display mode, false the panel.</summary>
    public bool? WindowsDisplay { get; set; }
}

public sealed class UniversalScreen88SettingsRequest
{
    public bool? OfflineMode { get; set; }
    public bool? WindowsDisplay { get; set; }
}
