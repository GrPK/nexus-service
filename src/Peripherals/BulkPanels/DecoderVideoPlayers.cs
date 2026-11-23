using System;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>Plays library videos on the HydroShift II OLED Curved's own decoder; the head only flips, so a mount is up or flipped.</summary>
public sealed class HydroShift2CurvePlayer : DecoderVideoPlayer
{
    public HydroShift2CurvePlayer(
        BulkPanelHub hub, HydroShift2CurveLcdDriver driver, HydroShift2CurveMedia media, IConfigStore store, PanelDeviceRegistry registry,
        Func<string, bool>? lockSleeping = null)
        : base(hub, driver, media, store, registry, s => ScreenOf(s.Devices.HydroShift2Curve), HydroShift2CurveLcdDriver.Id, lockSleeping)
    {
    }

    /// <summary>How the head is mounted, from its panel record; library videos are encoded to match.</summary>
    public (bool Flip180, bool Mirror) Mount() => MountOf(Record());

    internal static (bool Flip180, bool Mirror) MountOf(PanelDeviceRecord? record) =>
        record is null ? (false, false) : (record.Rotation == 180, record.Mirror == true);

    protected override int RotationOf(PanelDeviceRecord record) => record.Rotation == 180 ? 180 : 0;

    private static DecoderScreenSettings ScreenOf(HydroShift2CurveSettings c) =>
        new(c.ScreenMode, c.Video, c.ScreenSaverMinutes, c.ScreenSaverVideo, c.ScreenSaverBrightness);
}

/// <summary>Plays library videos on the Universal Screen 8.8's own decoder, encoded for the record's rotation (landscape or portrait, either way up).</summary>
public sealed class UniversalScreen88Player : DecoderVideoPlayer
{
    public UniversalScreen88Player(
        BulkPanelHub hub, UniversalScreen88Driver driver, UniversalScreen88Media media, IConfigStore store, PanelDeviceRegistry registry,
        Func<string, bool>? lockSleeping = null)
        : base(hub, driver, media, store, registry, s => ScreenOf(s.Devices.UniversalScreen88), UniversalScreen88Driver.Id, lockSleeping)
    {
    }

    private static DecoderScreenSettings ScreenOf(UniversalScreen88Settings g) =>
        new(g.ScreenMode, g.Video, g.ScreenSaverMinutes, g.ScreenSaverVideo, g.ScreenSaverBrightness);
}
