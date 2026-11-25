using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.LogitechHidpp;

namespace Nexus.Service.Devices.Handlers;

/// <summary>
/// A wired Logitech mouse with onboard memory (the G502 X PLUS), listed under Devices with a
/// page for the profiles it keeps in its own memory: DPI levels, report rate, button bindings,
/// names, the active profile. Lighting stays with OpenRGB; this only talks HID++ 0x8100.
/// </summary>
public sealed class LogitechMouseHandler : IDeviceHandler
{
    public const string HandlerId = "logitech-mouse";

    private readonly LogitechMouseService? _mouse;

    public LogitechMouseHandler(LogitechMouseService? mouse = null)
    {
        _mouse = mouse;
    }

    public string Id => HandlerId;
    public string Name => $"Logitech {_mouse?.PresentModel() ?? "G502 X PLUS"}";
    public string Category => "mouse";
    public IReadOnlyList<UsbId> Identifiers => Array.Empty<UsbId>();
    public bool SupportsNexusControl => false;

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices) => _mouse?.PresentModel() is not null;

    public string GetFirmwareVersion() => "";
}
