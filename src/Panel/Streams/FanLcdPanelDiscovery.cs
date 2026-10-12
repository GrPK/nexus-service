using System.Collections.Generic;
using System.IO;
using Nexus.Service.Models.Panel;
using Nexus.Service.Peripherals.FanLcd;
using Nexus.Service.Persistence;

namespace Nexus.Service.Panel.Streams;

/// <summary>
/// Presents each attached fan screen with its Nexus panel switch on as its own streamed panel;
/// every other screen stays off the panel list, the way a display is a panel only while promoted.
/// </summary>
public sealed class FanLcdPanelDiscovery : IStreamedPanelDiscovery
{
    private readonly IFanLcdScreens _screens;
    private readonly IConfigStore _store;

    public FanLcdPanelDiscovery(IFanLcdScreens screens, IConfigStore store)
    {
        _screens = screens;
        _store = store;
    }

    public string HandlerId => _screens.HandlerId;

    /// <summary>The panel serial for a screen: family-prefixed, so two families' screen serials never collide.</summary>
    public static string PanelSerial(IFanLcdScreens screens, string screenSerial) => $"{screens.PanelFamily}:{screenSerial}";

    public IReadOnlyList<StreamedPanelDeviceInfo> Discover()
    {
        var settings = _screens.SettingsOf(_store.Load().Devices);
        var panels = new List<StreamedPanelDeviceInfo>();
        foreach (var serial in _screens.AttachedSerials())
        {
            if (!settings.TryGetValue(serial, out var screen) || !FanLcdContent.IsPanel(screen))
            {
                continue;
            }
            panels.Add(new StreamedPanelDeviceInfo
            {
                Serial = PanelSerial(_screens, serial),
                Profile = new StreamedPanelProfile
                {
                    Kind = _screens.PanelFamily,
                    DisplayName = _screens.PanelName(serial),
                    Surface = PanelSurfaces.LcdRound,
                    Family = _screens.PanelFamily,
                    CssWidth = FanLcdContent.Size,
                    CssHeight = FanLcdContent.Size,
                    Dpr = 1.0,
                    Fps = _screens.PanelFps,
                    Codec = StreamCodec.RawBgra,
                    MultiInstance = true,
                },
            });
        }
        return panels;
    }

    public IStreamedPanelTransport CreateTransport(StreamedPanelDeviceInfo info)
    {
        string prefix = _screens.PanelFamily + ":";
        if (!info.Serial.StartsWith(prefix, System.StringComparison.Ordinal))
        {
            throw new IOException($"not a {_screens.PanelFamily} panel: {info.Serial}");
        }
        return new FanLcdPanelTransport(_screens, info.Serial[prefix.Length..], info.Serial);
    }
}
