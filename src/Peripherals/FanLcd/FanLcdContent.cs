using Nexus.Service.Persistence;

namespace Nexus.Service.Peripherals.FanLcd;

public static class FanLcdContent
{
    /// <summary>
    /// The content worker's mode while the screen's panel session owns it. Also the content type
    /// the first panel-mode builds stored, before <see cref="LianLiWirelessScreenSettings.NexusPanel"/>.
    /// </summary>
    public const string Panel = "panel";

    /// <summary>Both families' square frame edge, in pixels, behind round glass.</summary>
    public const int Size = 400;

    public static bool IsPanel(LianLiWirelessScreenSettings screen) => screen.NexusPanel || screen.ContentType == Panel;

    /// <summary>The content shown while the panel is off; a stored legacy panel content reads as off.</summary>
    public static string ContentOf(LianLiWirelessScreenSettings screen) => screen.ContentType == Panel ? "off" : screen.ContentType;

    /// <summary>What the content worker runs: <see cref="Panel"/> while the panel owns the screen, else the content.</summary>
    public static string ModeOf(LianLiWirelessScreenSettings screen) => IsPanel(screen) ? Panel : ContentOf(screen);
}
