using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.JpegPanels;

namespace Nexus.Service.Devices.Handlers;

/// <summary>
/// Device-list row for one JPEG-over-HID cooler LCD. One handler per
/// <see cref="JpegPanelModel"/>, so each model gets its own Nexus Control toggle and its
/// own Experimental badge rather than the family sharing one switch.
///
/// <see cref="HasPage"/> is false: the panel record is the device's page (the glass is
/// what the user configures), the same way a streamed Kraken panel claims its cooler row.
/// </summary>
public sealed class JpegPanelHandler : IDeviceHandler
{
    private readonly JpegPanelModel _model;
    private readonly Func<bool> _attached;

    public JpegPanelHandler(JpegPanelHub hub)
        : this(hub.Model, () => hub.IsConnected)
    {
    }

    /// <summary>One row for a model whose screens attach as several hubs, connected while any is.</summary>
    public JpegPanelHandler(JpegPanelModel model, Func<bool> attached)
    {
        _model = model;
        _attached = attached;
        Identifiers = model.ProductIds
            .Select(pid => new UsbId(model.VendorId, pid))
            .ToArray();
    }

    public string Id => _model.HandlerId;
    public string Name => _model.Name;
    public string Category => "cooler";
    public bool HasPage => false;

    public IReadOnlyList<UsbId> Identifiers { get; }

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices)
    {
        if (_attached())
        {
            return true;
        }
        var model = _model;
        return detectedDevices.Any(d =>
            d.VendorId == model.VendorId && model.ProductIds.Contains(d.ProductId));
    }

    /// <summary>These panels expose no firmware query of any kind.</summary>
    public string GetFirmwareVersion() => "";
}
