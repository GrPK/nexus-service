using System;
using System.IO;
using System.Linq;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Routes;
using Xunit;
using GlassPipe = Nexus.Service.Tests.BulkPanels.UniversalScreen88Tests.GlassPipe;

namespace Nexus.Service.Tests.BulkPanels;

/// <summary>Video on the 8.8's own decoder, as L-Connect's lcd207 PlayH264 sends it.</summary>
public class UniversalScreen88VideoTests
{
    [Fact]
    public void A_video_starts_with_the_decoder_stopped_the_clock_down_and_the_rate_set()
    {
        var driver = new UniversalScreen88Driver();
        var pipe = new GlassPipe();
        driver.Connect(pipe, null);
        pipe.Clear();

        Assert.Equal(UniversalScreen88Protocol.H264Block, driver.BeginVideo(pipe, 30));

        Assert.Equal(new[] { UniversalScreen88Protocol.CommandStopPlay, UniversalScreen88Protocol.CommandStopClock, UniversalScreen88Protocol.CommandFrameRate }, pipe.Commands);
        Assert.Equal(30, pipe.Params[2][0]);
    }

    [Fact]
    public void A_chunk_carries_its_length_and_last_flag_then_the_stream()
    {
        var driver = new UniversalScreen88Driver();
        var pipe = new GlassPipe();
        driver.Connect(pipe, null);
        pipe.Clear();
        var chunk = Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray();

        Assert.Equal(0, driver.SendVideoChunk(pipe, chunk, last: true, sessionTick: 7));

        Assert.Equal(UniversalScreen88Protocol.CommandStartPlay, pipe.Commands.Single());
        Assert.Equal(new byte[] { 0, 0, 0x03, 0xE8, 1 }, pipe.Params[0][..5]);
        Assert.Equal(chunk, pipe.Payloads.Single());
    }

    [Fact]
    public void Buffered_blocks_are_read_from_the_reply()
    {
        Assert.Equal(4, UniversalScreen88Protocol.DecodeBufferedBlocks(new byte[] { 0x79, 0, 0, 0, 0, 0, 0, 0, 4 }));
        Assert.Equal(0, UniversalScreen88Protocol.DecodeBufferedBlocks(new byte[] { 0x79 }));
    }

    [Fact]
    public void Ending_a_video_readies_the_glass_for_frames_again()
    {
        var driver = new UniversalScreen88Driver();
        var pipe = new GlassPipe();
        driver.Connect(pipe, null);
        pipe.Clear();

        driver.EndVideo(pipe);

        Assert.Equal(new[] { UniversalScreen88Protocol.CommandStopPlay, UniversalScreen88Protocol.CommandStopClock, UniversalScreen88Protocol.CommandPushPng }, pipe.Commands);
    }

    [Fact]
    public void Streamed_frames_are_dropped_while_a_video_owns_the_glass()
    {
        var driver = new UniversalScreen88Driver();
        var pipe = new GlassPipe();
        driver.Connect(pipe, null);
        pipe.Clear();

        driver.VideoOwnsGlass = true;
        Assert.True(driver.SendFrame(pipe, null, new byte[1920 * 480 * 4]));

        Assert.Empty(pipe.Commands);
    }

    private const string CoverLandscape = "scale=1920:480:force_original_aspect_ratio=increase:flags=lanczos,crop=1920:480";
    private const string CoverPortrait = "scale=480:1920:force_original_aspect_ratio=increase:flags=lanczos,crop=480:1920";

    [Theory]
    [InlineData(0, false, CoverLandscape + ",transpose=1")]
    [InlineData(180, false, CoverLandscape + ",transpose=1")]
    [InlineData(90, false, CoverPortrait)]
    [InlineData(270, false, CoverPortrait)]
    [InlineData(0, true, CoverLandscape + ",hflip,transpose=1")]
    [InlineData(180, true, CoverLandscape + ",hflip,transpose=1")]
    [InlineData(90, true, CoverPortrait + ",hflip")]
    [InlineData(270, true, CoverPortrait + ",hflip")]
    public void Clips_are_laid_into_the_portrait_framebuffer_like_pushed_frames(int rotation, bool mirror, string filter) =>
        Assert.Equal(filter, UniversalScreen88Protocol.VideoMountFilter(rotation, mirror));

    [Fact]
    public void The_curve_keeps_its_variant_files_and_the_screen_names_its_by_rotation()
    {
        Assert.Equal("glass-up.h264", HydroShift2CurveMedia.Profile.VariantFile(0, false));
        Assert.Equal("glass-flip-mirror.h264", HydroShift2CurveMedia.Profile.VariantFile(180, true));
        Assert.Equal(HydroShift2CurveProtocol.MountFilter(true, false), HydroShift2CurveMedia.Profile.MountFilter(180, false));
        Assert.False(HydroShift2CurveMedia.Profile.HasPortrait);
        // ".v2": the first encodes could stretch a 4:1 crop into a portrait mount, so none of them is reused.
        Assert.Equal("glass-r90.v2.h264", UniversalScreen88Media.Profile.VariantFile(90, false));
        Assert.Equal("glass-r0-mirror.v2.h264", UniversalScreen88Media.Profile.VariantFile(0, true));
        Assert.Equal("glass-r0.v2.h264", UniversalScreen88Media.Profile.VariantFile(180, false));
        Assert.Equal("glass-r90-mirror.v2.h264", UniversalScreen88Media.Profile.VariantFile(270, true));
    }

    // ── portrait mounts: a 1:4 crop of their own, never a stretched 4:1 one ──

    private const string Wide = "0,0.25,1,0.5,0,0";
    private const string Tall = "0.4,0,0.140625,1,0,0";
    private const string WideCrop = "crop=iw*1:ih*0.5:iw*0:ih*0.25";
    private const string TallCrop = "crop=iw*0.140625:ih*1:iw*0.4:ih*0";

    private static (UniversalScreen88Media Media, string Root) Library(params string[] meta)
    {
        var root = Path.Combine(Path.GetTempPath(), "nexus-screen88-media-" + Guid.NewGuid().ToString("N"));
        var item = Path.Combine(root, "clip-1");
        Directory.CreateDirectory(item);
        File.WriteAllBytes(Path.Combine(item, "source.mp4"), new byte[] { 1 });
        File.WriteAllLines(Path.Combine(item, "meta.txt"), meta);
        return (new UniversalScreen88Media(root), root);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(180)]
    public void A_landscape_mount_shows_the_landscape_crop(int rotation)
    {
        var (media, root) = Library("Clip", Wide, "10", Tall);
        try
        {
            Assert.Equal($"{WideCrop},{CoverLandscape},transpose=1", media.VariantFilter("clip-1", rotation, false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(90)]
    [InlineData(270)]
    public void A_portrait_mount_shows_the_portrait_crop(int rotation)
    {
        var (media, root) = Library("Clip", Wide, "10", Tall);
        try
        {
            Assert.Equal($"{TallCrop},{CoverPortrait}", media.VariantFilter("clip-1", rotation, false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_clip_cropped_4_to_1_before_portrait_crops_existed_shows_its_centre_on_a_portrait_mount()
    {
        // Three meta lines, as the first 8.8 build wrote them: its 4:1 crop is not stretched into the tall glass.
        var (media, root) = Library("Clip", Wide, "10");
        try
        {
            Assert.Equal(CoverPortrait, media.VariantFilter("clip-1", 270, false));
            Assert.Equal($"{WideCrop},{CoverLandscape},transpose=1", media.VariantFilter("clip-1", 0, false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_mount_without_its_own_crop_keeps_the_turn_and_mirror_of_the_other()
    {
        var (media, root) = Library("Clip", "0,0,1,1,90,1", "10", "");
        try
        {
            Assert.Equal($"hflip,transpose=1,{CoverPortrait},hflip", media.VariantFilter("clip-1", 90, true));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        (media, root) = Library("Clip", "", "10", "0,0,1,1,270,0");
        try
        {
            Assert.Equal($"transpose=2,{CoverLandscape},transpose=1", media.VariantFilter("clip-1", 0, false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Previews_and_thumbnails_follow_the_orientation_and_are_never_stretched()
    {
        var (media, root) = Library("Clip", Wide, "10", Tall);
        try
        {
            Assert.Equal($"{WideCrop},scale=960:240:force_original_aspect_ratio=increase:flags=lanczos,crop=960:240", media.PreviewFilter("clip-1", portrait: false));
            Assert.Equal($"{TallCrop},scale=240:960:force_original_aspect_ratio=increase:flags=lanczos,crop=240:960", media.PreviewFilter("clip-1", portrait: true));
            Assert.Equal($"{WideCrop},scale=320:80:force_original_aspect_ratio=increase:flags=lanczos,crop=320:80", media.ThumbFilter("clip-1", portrait: false));
            Assert.Equal($"{TallCrop},scale=80:320:force_original_aspect_ratio=increase:flags=lanczos,crop=80:320", media.ThumbFilter("clip-1", portrait: true));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void The_curve_library_filters_are_unchanged()
    {
        var root = Path.Combine(Path.GetTempPath(), "nexus-curve-media-" + Guid.NewGuid().ToString("N"));
        var item = Path.Combine(root, "clip-1");
        Directory.CreateDirectory(item);
        File.WriteAllLines(Path.Combine(item, "meta.txt"), new[] { "Clip", Wide, "10", Tall });
        try
        {
            var media = new HydroShift2CurveMedia(root);
            // One crop; a fourth line is not the Curve's, and portrait is never asked of it.
            Assert.Equal($"{WideCrop},{HydroShift2CurveProtocol.MountFilter(true, false)}", media.VariantFilter("clip-1", 180, false));
            Assert.Equal($"{WideCrop},scale={HydroShift2CurveProtocol.Width / 2}:{HydroShift2CurveProtocol.Height / 2}:flags=lanczos", media.PreviewFilter("clip-1", portrait: true));
            Assert.Equal($"{WideCrop},scale=320:151", media.ThumbFilter("clip-1", portrait: false));
            File.WriteAllLines(Path.Combine(item, "meta.txt"), new[] { "Clip", "", "10" });
            Assert.Equal($"crop=iw*1:ih*1:iw*0:ih*0,scale=320:151", media.ThumbFilter("clip-1", portrait: false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(0, 1920, 480, false)]
    [InlineData(90, 480, 1920, true)]
    [InlineData(180, 1920, 480, false)]
    [InlineData(270, 480, 1920, true)]
    public void The_picture_a_clip_has_follows_the_record_rotation(int rotation, int width, int height, bool portrait)
    {
        Assert.Equal((width, height), UniversalScreen88Protocol.VideoPicture(rotation));
        Assert.Equal(portrait, UniversalScreen88Media.Profile.IsPortrait(rotation));
    }

    [Fact]
    public void Upload_crops_are_optional_but_must_parse_when_given()
    {
        Assert.True(DevicesRoutes.TryCropField("", out var none));
        Assert.Null(none);
        Assert.True(DevicesRoutes.TryCropField(Tall, out var tall));
        Assert.Equal(0.140625, tall!.Value.W);
        Assert.False(DevicesRoutes.TryCropField("0,0,2,1", out _));
        Assert.False(DevicesRoutes.TryCropField("nope", out _));
    }

    [Fact]
    public void Lconnect_videos_list_the_original_mp4_of_each_template()
    {
        var folder = Path.Combine(Path.GetTempPath(), "nexus-lc-videos-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllBytes(Path.Combine(folder, "88001_09103640.mp4"), new byte[3]);
            File.WriteAllBytes(Path.Combine(folder, "88001_09103640.h264"), new byte[3]);
            File.WriteAllBytes(Path.Combine(folder, "880010.mp410053044.mp4"), new byte[5]);
            File.WriteAllBytes(Path.Combine(folder, "custom.mp4"), new byte[1]);

            var videos = LConnectVideos.List(folder);

            Assert.Equal(new[] { "880010.mp410053044.mp4", "88001_09103640.mp4", "custom.mp4" }, videos.Select(v => v.File));
            Assert.Equal(new[] { "L-Connect 880010", "L-Connect 88001", "L-Connect custom" }, videos.Select(v => v.Label));
            Assert.Equal(5, videos[0].SizeBytes);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_missing_lconnect_folder_lists_nothing() =>
        Assert.Empty(LConnectVideos.List(Path.Combine(Path.GetTempPath(), "nexus-no-such-" + Guid.NewGuid().ToString("N"))));
}
