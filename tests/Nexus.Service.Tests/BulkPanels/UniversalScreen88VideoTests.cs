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

    [Theory]
    [InlineData(0, false, "scale=1920:480:flags=lanczos,transpose=1")]
    [InlineData(180, false, "scale=1920:480:flags=lanczos,transpose=1")]
    [InlineData(90, false, "scale=480:1920:flags=lanczos")]
    [InlineData(270, false, "scale=480:1920:flags=lanczos")]
    [InlineData(0, true, "scale=1920:480:flags=lanczos,hflip,transpose=1")]
    [InlineData(180, true, "scale=1920:480:flags=lanczos,hflip,transpose=1")]
    [InlineData(90, true, "scale=480:1920:flags=lanczos,hflip")]
    [InlineData(270, true, "scale=480:1920:flags=lanczos,hflip")]
    public void Clips_are_laid_into_the_portrait_framebuffer_like_pushed_frames(int rotation, bool mirror, string filter) =>
        Assert.Equal(filter, UniversalScreen88Protocol.VideoMountFilter(rotation, mirror));

    [Fact]
    public void The_curve_keeps_its_variant_files_and_the_screen_names_its_by_rotation()
    {
        Assert.Equal("glass-up.h264", HydroShift2CurveMedia.Profile.VariantFile(0, false));
        Assert.Equal("glass-flip-mirror.h264", HydroShift2CurveMedia.Profile.VariantFile(180, true));
        Assert.Equal(HydroShift2CurveProtocol.MountFilter(true, false), HydroShift2CurveMedia.Profile.MountFilter(180, false));
        Assert.Equal("glass-r90.h264", UniversalScreen88Media.Profile.VariantFile(90, false));
        Assert.Equal("glass-r0-mirror.h264", UniversalScreen88Media.Profile.VariantFile(0, true));
        Assert.Equal("glass-r0.h264", UniversalScreen88Media.Profile.VariantFile(180, false));
        Assert.Equal("glass-r90-mirror.h264", UniversalScreen88Media.Profile.VariantFile(270, true));
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
