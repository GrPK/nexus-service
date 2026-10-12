using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nexus.Service.Peripherals.JpegPanels;

namespace Nexus.Service.Panel.Streams;

/// <summary>Presents each attached TL LCD screen as its own streamed panel, keyed by <see cref="TlLcdScreen.Key"/>.</summary>
public sealed class TlLcdPanelDiscovery : IStreamedPanelDiscovery
{
    private readonly TlLcdPanels _panels;

    public TlLcdPanelDiscovery(TlLcdPanels panels)
    {
        _panels = panels;
    }

    public string HandlerId => JpegPanelModel.TlLcd.HandlerId;

    public IReadOnlyList<StreamedPanelDeviceInfo> Discover() =>
        _panels.Attached
            .Select(s => new StreamedPanelDeviceInfo
            {
                Serial = s.Key,
                Profile = JpegPanelDiscovery.ProfileFor(s.Hub.Model, s.DisplayName),
            })
            .ToArray();

    public IStreamedPanelTransport CreateTransport(StreamedPanelDeviceInfo info) =>
        _panels.ByKey(info.Serial) is { } screen
            ? new JpegPanelStreamTransport(screen.Hub, info.Serial)
            : throw new IOException($"{JpegPanelModel.TlLcd.Name} {info.Serial} not connected");
}
