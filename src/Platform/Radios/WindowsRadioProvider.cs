using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Radios;
using Nexus.Service.Models.Radios;

namespace Nexus.Service.Platform.Radios;

/// <summary>
/// Wi-Fi and Bluetooth through Windows.Devices.Radios, which works from the
/// LocalSystem service in Session 0 (no helper hop). Airplane Mode has no
/// public API and the IRadioManager COM route did not change state on the lab
/// PC, so it reports null.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsRadioProvider : IRadioProvider
{
    public async Task<RadiosState> GetStateAsync(CancellationToken ct = default)
    {
        var radios = await GetRadiosAsync().ConfigureAwait(false);
        return new RadiosState
        {
            Wifi = ToggleFor(radios, RadioKind.WiFi),
            Bluetooth = ToggleFor(radios, RadioKind.Bluetooth),
        };
    }

    public async Task<bool> SetAsync(string kind, bool on, CancellationToken ct = default)
    {
        var target = kind switch
        {
            RadioKinds.Wifi => RadioKind.WiFi,
            RadioKinds.Bluetooth => RadioKind.Bluetooth,
            _ => RadioKind.Other,
        };
        if (target == RadioKind.Other)
        {
            return false;
        }

        var matches = (await GetRadiosAsync().ConfigureAwait(false)).Where(r => r.Kind == target).ToList();
        if (matches.Count == 0)
        {
            return false;
        }

        var ok = true;
        foreach (var radio in matches)
        {
            var status = await radio.SetStateAsync(on ? RadioState.On : RadioState.Off).AsTask(ct).ConfigureAwait(false);
            ok &= status == RadioAccessStatus.Allowed;
        }
        return ok;
    }

    private static RadioToggle? ToggleFor(IReadOnlyList<Radio> radios, RadioKind kind)
    {
        var matches = radios.Where(r => r.Kind == kind).ToList();
        return matches.Count == 0 ? null : new RadioToggle { On = matches.Any(r => r.State == RadioState.On) };
    }

    private static async Task<IReadOnlyList<Radio>> GetRadiosAsync()
    {
        try
        {
            var access = await Radio.RequestAccessAsync().AsTask().ConfigureAwait(false);
            if (access != RadioAccessStatus.Allowed)
            {
                return Array.Empty<Radio>();
            }
            return await Radio.GetRadiosAsync().AsTask().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[radios] enumerate failed: {ex.Message}");
            return Array.Empty<Radio>();
        }
    }
}
