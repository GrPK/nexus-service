using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.AsiahorseUmbra;

namespace Nexus.Service.Devices.Handlers;

public sealed class AsiahorseUmbraHandler : IDeviceHandler
{
    private readonly UmbraHub _hub;

    public AsiahorseUmbraHandler(UmbraHub hub)
    {
        _hub = hub;
        Identifiers = new[] { new UsbId(UmbraProtocol.VendorId, UmbraProtocol.ProductId) };
    }

    public string Id => UmbraConnectionWorker.HandlerId;
    public string Name => "Asiahorse Umbra V2";
    public string Category => "controller";

    public IReadOnlyList<UsbId> Identifiers { get; }

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices) =>
        _hub.IsConnected
        || detectedDevices.Any(d => d.VendorId == UmbraProtocol.VendorId && d.ProductId == UmbraProtocol.ProductId);

    public string GetFirmwareVersion() => _hub.Snapshot.Firmware;
}
