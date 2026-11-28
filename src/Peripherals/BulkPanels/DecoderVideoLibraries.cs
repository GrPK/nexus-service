namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>The HydroShift II OLED Curved's video library: the glass's portrait framebuffer turns the opposite way when the head is flipped.</summary>
public sealed class HydroShift2CurveMedia : DecoderVideoMedia
{
    public static readonly DecoderVideoProfile Profile = new(
        HydroShift2CurveLcdDriver.Id,
        PreviewWidth: HydroShift2CurveProtocol.Width / 2,
        PreviewHeight: HydroShift2CurveProtocol.Height / 2,
        ThumbScale: "320:151",
        MountFilter: (rotation, mirror) => HydroShift2CurveProtocol.MountFilter(rotation == 180, mirror),
        VariantFile: (rotation, mirror) => $"glass-{(rotation == 180 ? "flip" : "up")}{(mirror ? "-mirror" : "")}.h264");

    public HydroShift2CurveMedia(string? root = null)
        : base(Profile, root)
    {
    }
}

/// <summary>
/// The Universal Screen 8.8's video library. The glass decodes into its 480x1920 portrait framebuffer, so a
/// landscape mount (rotation 0 or 180) is turned a quarter into it, the way pushed frames are. A clip keeps a
/// 4:1 crop for landscape mounts and a 1:4 one for portrait mounts (90, 270), and its previews and
/// thumbnails follow the mount.
/// </summary>
public sealed class UniversalScreen88Media : DecoderVideoMedia
{
    public static readonly DecoderVideoProfile Profile = new(
        UniversalScreen88Driver.Id,
        PreviewWidth: UniversalScreen88Protocol.Height / 2,
        PreviewHeight: UniversalScreen88Protocol.Width / 2,
        ThumbScale: "320:80",
        MountFilter: UniversalScreen88Protocol.VideoMountFilter,
        // ".v2": encodes from before the crop per orientation could stretch a 4:1 crop into a portrait mount, so none is reused.
        VariantFile: (rotation, mirror) => $"glass-r{UniversalScreen88Protocol.VideoMount(rotation)}{(mirror ? "-mirror" : "")}.v2.h264",
        PortraitMount: rotation => UniversalScreen88Protocol.VideoMount(rotation) == 90);

    public UniversalScreen88Media(string? root = null)
        : base(Profile, root)
    {
    }
}
