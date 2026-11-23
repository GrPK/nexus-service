using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.BulkPanels;

namespace Nexus.Service.Devices.Handlers;

/// <summary>
/// The Universal Screen 8.8's bezel LED board (0416:8050), switched on its own: the glass can stay a plain
/// Windows monitor while Nexus lights the ring, and with this off OpenRGB keeps the ring as before.
/// </summary>
public sealed class UniversalScreen88LedHandler : IDeviceHandler
{
    private readonly UniversalScreen88LedBoard _board;

    public UniversalScreen88LedHandler(UniversalScreen88LedBoard board)
    {
        _board = board;
        Identifiers = new[] { new UsbId(UniversalScreen88LedProtocol.VendorId, UniversalScreen88LedProtocol.ProductId) };
    }

    public string Id => UniversalScreen88LedProtocol.HandlerId;
    public string Name => "Lian Li Universal Screen 8.8 LEDs";
    public string Category => "controller";

    /// <summary>Its controls are on the Lighting page and the screen's own page.</summary>
    public bool HasPage => false;

    public IReadOnlyList<UsbId> Identifiers { get; }

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices) =>
        _board.IsAvailable
        || detectedDevices.Any(d => d.VendorId == UniversalScreen88LedProtocol.VendorId && d.ProductId == UniversalScreen88LedProtocol.ProductId);

    public string GetFirmwareVersion() => _board.Firmware ?? "";
}
