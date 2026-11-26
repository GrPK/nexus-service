using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.LogitechHidpp;

namespace Nexus.Service.Devices.Handlers;

/// <summary>
/// A wired Logitech keyboard with key customization (the G915 X), listed under Devices with a
/// page for its BASE / FN / G-SHIFT key assignments. Lighting stays with OpenRGB; this only
/// talks HID++ 0x1B05 / 0x1B10 / 0x8101.
/// </summary>
public sealed class LogitechKeyboardHandler : IDeviceHandler
{
    public const string HandlerId = "logitech-keyboard";

    private readonly LogitechKeyboardService? _keyboard;

    public LogitechKeyboardHandler(LogitechKeyboardService? keyboard = null)
    {
        _keyboard = keyboard;
    }

    public string Id => HandlerId;
    public string Name => $"Logitech {_keyboard?.PresentModel() ?? "G915 X"}";
    public string Category => "keyboard";
    public IReadOnlyList<UsbId> Identifiers => Array.Empty<UsbId>();
    public bool SupportsNexusControl => false;

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices) => _keyboard?.PresentModel() is not null;

    public string GetFirmwareVersion() => "";
}
