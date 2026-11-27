using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.CreativeAudio;

namespace Nexus.Service.Devices.Handlers;

/// <summary>
/// A Creative Sound Blaster USB DAC (the G8, the G6) listed under Devices with a page of its own:
/// Creative App's acoustic engine, EQ, Scout Mode, microphone, output and DAC filter. The settings
/// live in the device and go over its vendor HID interface; Nexus Control has nothing to switch.
/// </summary>
public sealed class SoundBlasterHandler : IDeviceHandler
{
    public const string HandlerId = "creative-soundblaster";

    private readonly SoundBlasterService? _device;

    public SoundBlasterHandler(SoundBlasterService? device = null)
    {
        _device = device;
    }

    public string Id => HandlerId;
    public string Name => $"Creative {_device?.PresentModel() ?? "Sound Blaster"}";
    public string Category => "speaker";
    public IReadOnlyList<UsbId> Identifiers => Array.Empty<UsbId>();
    public bool SupportsNexusControl => false;

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices) => _device?.PresentModel() is not null;

    public string GetFirmwareVersion() => "";
}
