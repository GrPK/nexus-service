using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Radios;
using Nexus.Service.Models.Radios;

namespace Nexus.Service.Platform.Radios;

/// <summary>
/// Wi-Fi and Bluetooth through Windows.Devices.Radios; Airplane Mode through the
/// Settings app's IRadioManager COM object, called by raw vtable slot. Both
/// work from the LocalSystem service in Session 0 (no helper hop).
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
            Airplane = await Task.Run(ReadAirplane).ConfigureAwait(false),
        };
    }

    public async Task<bool> SetAsync(string kind, bool on, CancellationToken ct = default)
    {
        if (kind == RadioKinds.Airplane)
        {
            return await SetAirplaneAsync(on, ct).ConfigureAwait(false);
        }

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

    private async Task<bool> SetAirplaneAsync(bool on, CancellationToken ct)
    {
        if (ReadAirplane() is null)
        {
            return false;
        }
        // The manager's flag means "radios enabled", so airplane on is 0.
        if (!await Task.Run(() => RadioManagerCom.SetRadiosEnabled(!on)).ConfigureAwait(false))
        {
            return false;
        }

        // Airplane flips the other radios asynchronously: wait for the flag, then for their states to stop changing.
        var deadline = DateTime.UtcNow + SettleTimeout;
        string? last = null;
        var stableReads = 0;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var flagSettled = ReadAirplane()?.On == on;
            var snapshot = string.Join(',', (await GetRadiosAsync().ConfigureAwait(false)).Select(r => $"{r.Kind}{r.State}"));
            stableReads = snapshot == last ? stableReads + 1 : 0;
            last = snapshot;
            if (flagSettled && stableReads >= 2)
            {
                return true;
            }
            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }
        return ReadAirplane()?.On == on;
    }

    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(150);

    private static RadioToggle? ReadAirplane() => RadioManagerCom.ReadAirplane();

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

/// <summary>Raw-vtable IRadioManager calls, kept out of the async class because unsafe code cannot await.</summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal static unsafe class RadioManagerCom
{
    public static RadioToggle? ReadAirplane()
    {
        var enabled = -1;
        var ok = CallRadioManager(mgr =>
        {
            if (IsRMSupported(mgr, out var supported) < 0 || supported == 0)
            {
                return false;
            }
            return GetSystemRadioState(mgr, out enabled, out _, out _) >= 0;
        });
        return ok ? new RadioToggle { On = enabled == 0 } : null;
    }

    private static bool CallRadioManager(Func<IntPtr, bool> work)
    {
        var clsid = ClsidRadioManagementApi;
        var iid = IidRadioManager;
        IntPtr mgr = IntPtr.Zero;
        try
        {
            if (CoCreateInstance(ref clsid, IntPtr.Zero, ClsCtxAll, ref iid, out mgr) < 0 || mgr == IntPtr.Zero)
            {
                return false;
            }
            return work(mgr);
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[radios] airplane call failed: {ex.Message}");
            return false;
        }
        finally
        {
            if (mgr != IntPtr.Zero)
            {
                ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(mgr, 2))(mgr);
            }
        }
    }

    // IRadioManager after the IUnknown slots: 3 IsRMSupported, 4 GetUIRadioInstances, 5 GetSystemRadioState, 6 SetSystemRadioState.
    private static int IsRMSupported(IntPtr mgr, out uint supported) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, out uint, int>)Slot(mgr, 3))(mgr, out supported);

    private static int GetSystemRadioState(IntPtr mgr, out int enabled, out int unused, out int reason) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, out int, out int, out int, int>)Slot(mgr, 5))(mgr, out enabled, out unused, out reason);

    private static int SetSystemRadioState(IntPtr mgr, int enabled) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Slot(mgr, 6))(mgr, enabled);

    private static IntPtr Slot(IntPtr instance, int slot) => Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size);

    private static readonly Guid ClsidRadioManagementApi = new("581333F6-28DB-41BE-BC7A-FF201F12F3F6");
    private static readonly Guid IidRadioManager = new("DB3AFBFB-08E6-46C6-AA70-BF9A34C30AB7");
    private const uint ClsCtxAll = 0x17;

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint clsContext, ref Guid iid, out IntPtr instance);

    public static bool SetRadiosEnabled(bool enabled) => CallRadioManager(mgr => SetSystemRadioState(mgr, enabled ? 1 : 0) >= 0);
}
