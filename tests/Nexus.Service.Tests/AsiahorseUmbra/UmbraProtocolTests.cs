using System;
using Nexus.Service.Peripherals.AsiahorseUmbra;
using Xunit;

namespace Nexus.Service.Tests.AsiahorseUmbra;

// Sync, async and stream vectors are wire captures from the Y70 bench; the other frames are built from the documented header and checksum.
public class UmbraProtocolTests
{
    // Hex of the meaningful prefix, zero padded to 64 bytes.
    private static byte[] Padded(string hex)
    {
        var parts = hex.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var frame = new byte[64];
        for (var i = 0; i < parts.Length; i++)
        {
            frame[i] = Convert.ToByte(parts[i], 16);
        }
        return frame;
    }

    // Prefix plus a trailing byte at an index inside the zero padding.
    private static byte[] Padded(string hex, int tailIndex, byte tail)
    {
        var frame = Padded(hex);
        frame[tailIndex] = tail;
        return frame;
    }

    private static readonly byte[] DefaultColors =
    {
        0xFF, 0x00, 0x00, 0x00, 0xFF, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0x00,
    };

    [Fact]
    public void SyncRainbowAtDefaults_matches_the_captured_frame()
    {
        var block = new UmbraEffectBlock(0x01, 0x03, 0x01, 0x99, DefaultColors);

        var frame = UmbraProtocol.EncodeSyncEffect(block);

        Assert.Equal(Padded("52 42 3c 00 03 ff ff 01 03 01 99 ff 00 00 00 ff 00 00 00 ff ff ff 00", 59, 0x6A), frame);
    }

    [Fact]
    public void AsyncRainbowOnPort10_matches_the_captured_frame()
    {
        var block = new UmbraEffectBlock(0x01, 0x03, 0x01, 0x99, DefaultColors);

        var frame = UmbraProtocol.EncodeAsyncEffect(9, block);

        var prefix = "52 42 3c 00 03 fd 09 04 01 01 01 01 01 01 03 03 03 01 01 01 99 99 99"
            + " ff 00 00 00 ff 00 00 00 ff ff ff 00"
            + " ff 00 00 00 ff 00 00 00 ff ff ff 00"
            + " ff 00 00 00 ff 00 00 00 ff ff ff 00";
        Assert.Equal(Padded(prefix, 59, 0xAB), frame);
    }

    [Fact]
    public void MergeRainbow_uses_the_fe_fe_selector()
    {
        var block = new UmbraEffectBlock(0x00, 0x02, 0x01, 0x99, DefaultColors);

        var frame = UmbraProtocol.EncodeMergeEffect(block);

        Assert.Equal(Padded("52 42 3c 00 03 fe fe 00 02 01 99 ff 00 00 00 ff 00 00 00 ff ff ff 00", 59, 0x66), frame);
    }

    [Fact]
    public void StreamPacket_matches_the_captured_frame()
    {
        var rgb = new byte[18];
        for (var i = 0; i < 6; i++)
        {
            rgb[i * 3] = 0xFF;
            rgb[i * 3 + 2] = 0xB5;
        }

        var packets = UmbraProtocol.EncodeStream(rgb);

        Assert.Single(packets);
        Assert.Equal(Padded("88 01 01 ff 00 b5 ff 00 b5 ff 00 b5 ff 00 b5 ff 00 b5 ff 00 b5", 63, 0xC2), packets[0]);
    }

    [Fact]
    public void Stream_splits_into_packets_of_twenty_leds_with_one_based_indices()
    {
        var rgb = new byte[45 * 3];
        Array.Fill(rgb, (byte)1);

        var packets = UmbraProtocol.EncodeStream(rgb);

        Assert.Equal(3, packets.Length);
        for (var i = 0; i < packets.Length; i++)
        {
            Assert.Equal(0x88, packets[i][0]);
            Assert.Equal(3, packets[i][1]);
            Assert.Equal(i + 1, packets[i][2]);
        }
        Assert.Equal(1, packets[2][3 + 5 * 3 - 1]);
        Assert.Equal(0, packets[2][3 + 5 * 3]);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(20, 1)]
    [InlineData(21, 2)]
    [InlineData(1200, 60)]
    public void StreamPacketCount_rounds_up(int leds, int expected)
    {
        Assert.Equal(expected, UmbraProtocol.StreamPacketCount(leds));
    }

    [Fact]
    public void Commands_encode_with_length_and_checksum()
    {
        Assert.Equal(Padded("52 42 06 00 00 9a"), UmbraProtocol.EncodeInfoRequest());
        Assert.Equal(Padded("52 42 07 00 01 ff 9b"), UmbraProtocol.EncodePortConfigRequest());
        Assert.Equal(Padded("52 42 07 00 fd 01 99"), UmbraProtocol.EncodeAuthority(true));
        Assert.Equal(Padded("52 42 07 00 fd 00 98"), UmbraProtocol.EncodeAuthority(false));
        Assert.Equal(Padded("52 42 06 00 06 a0"), UmbraProtocol.EncodeLightsOff());
        Assert.Equal(Padded("52 42 06 00 fc 96"), UmbraProtocol.EncodeRestart());
    }

    [Fact]
    public void FanSync_on_is_zero_and_off_is_one()
    {
        Assert.Equal(Padded("52 42 07 00 08 00 a3"), UmbraProtocol.EncodeFanSync(true));
        Assert.Equal(Padded("52 42 07 00 08 01 a4"), UmbraProtocol.EncodeFanSync(false));
    }

    [Theory]
    [InlineData(20, "52 42 07 00 fb 14 aa")]
    [InlineData(100, "52 42 07 00 fb 64 fa")]
    [InlineData(250, "52 42 07 00 fb 64 fa")]
    [InlineData(-5, "52 42 07 00 fb 00 96")]
    public void FanDuty_clamps_to_a_percentage(int percent, string expected)
    {
        Assert.Equal(Padded(expected), UmbraProtocol.EncodeFanDuty(percent));
    }

    [Fact]
    public void SetPort_writes_port_field_value()
    {
        Assert.Equal(Padded("52 42 09 00 02 09 00 06 ae"), UmbraProtocol.EncodeSetPort(9, UmbraPortField.LedCount, 6));
        Assert.Equal(Padded("52 42 09 00 02 00 02 00 a1"), UmbraProtocol.EncodeSetPort(0, UmbraPortField.Direction, 0));
        Assert.Equal(Padded("52 42 09 00 02 02 03 05 a9"), UmbraProtocol.EncodeSetPort(2, UmbraPortField.Order, 5));
    }

    [Fact]
    public void BootMode_and_selfcheck_frames()
    {
        Assert.Equal(Padded("52 42 07 00 0b ff a5"), UmbraProtocol.EncodeBootMemory());
        Assert.Equal(Padded("52 42 07 00 0c ff a6"), UmbraProtocol.EncodeSelfCheck(true));
        Assert.Equal(Padded("52 42 07 00 0c 01 a8"), UmbraProtocol.EncodeSelfCheck(false));
    }

    [Fact]
    public void BootFixed_is_the_mode_frame_followed_by_the_fixed_merge_frame()
    {
        var frames = UmbraProtocol.EncodeBootFixed();

        Assert.Equal(2, frames.Length);
        Assert.Equal(Padded("52 42 07 00 0b 01 a7"), frames[0]);
        Assert.Equal(Padded("52 42 3c 00 03 fe fe 0a 01 01 99 ff ff ff 00 ff 00 00 00 ff ff ff 00", 59, 0x6D), frames[1]);
    }

    [Fact]
    public void ToReport_prepends_the_zero_report_id()
    {
        var report = UmbraProtocol.ToReport(UmbraProtocol.EncodeInfoRequest());

        Assert.Equal(65, report.Length);
        Assert.Equal(0x00, report[0]);
        Assert.Equal(0x52, report[1]);
    }

    [Fact]
    public void NeverEncodesFactoryResetSaveOrBootloader()
    {
        foreach (var frame in new[]
        {
            UmbraProtocol.EncodeInfoRequest(), UmbraProtocol.EncodeRestart(), UmbraProtocol.EncodeLightsOff(),
            UmbraProtocol.EncodeBootMemory(), UmbraProtocol.EncodeBootFixed()[0], UmbraProtocol.EncodeBootFixed()[1],
        })
        {
            Assert.DoesNotContain(frame[4], new byte[] { 0xFA, 0xF9, 0xFF });
        }
    }

    [Fact]
    public void DecodeInfo_reads_model_id_firmware_and_boot_flags()
    {
        var info = UmbraProtocol.DecodeInfo(UmbraDeviceFake.InfoReply(bootMemory: true, selfCheck: true));

        Assert.NotNull(info);
        Assert.Equal(5, info!.ModelCode);
        Assert.Equal("CDAB2F9648BC44FE", info.DeviceId);
        Assert.Equal("1.0.6", info.Firmware);
        Assert.True(info.BootMemory);
        Assert.True(info.SelfCheck);
    }

    [Fact]
    public void DecodeInfo_reports_fixed_boot_and_selfcheck_off()
    {
        var info = UmbraProtocol.DecodeInfo(UmbraDeviceFake.InfoReply(bootMemory: false, selfCheck: false));

        Assert.False(info!.BootMemory);
        Assert.False(info.SelfCheck);
    }

    [Fact]
    public void DecodeInfo_rejects_a_reply_to_another_command()
    {
        Assert.Null(UmbraProtocol.DecodeInfo(UmbraDeviceFake.PortReply(EmptyPorts())));
    }

    private static int[][] EmptyPorts()
    {
        var ports = new int[10][];
        for (var p = 0; p < 10; p++)
        {
            ports[p] = new[] { 0, 0, 1, p, 0 };
        }
        return ports;
    }

    [Fact]
    public void DecodePortConfig_reads_the_captured_y70_layout()
    {
        var ports = EmptyPorts();
        ports[9] = new[] { 6, 0, 1, 9, 0 };

        var config = UmbraProtocol.DecodePortConfig(UmbraDeviceFake.PortReply(ports, cap: 0x02FD));

        Assert.NotNull(config);
        Assert.Equal(10, config!.Ports.Length);
        Assert.Equal(0, config.Ports[0].LedCount);
        Assert.Equal(3, config.Ports[3].Order);
        Assert.Equal(6, config.Ports[9].LedCount);
        Assert.True(config.Ports[9].Forward);
        Assert.Equal(9, config.Ports[9].Order);
        Assert.Equal(765, config.BrightnessCap);
    }

    [Fact]
    public void DecodePortConfig_reads_direction_reverse()
    {
        var ports = EmptyPorts();
        ports[4] = new[] { 12, 0, 0, 4, 9 };

        var config = UmbraProtocol.DecodePortConfig(UmbraDeviceFake.PortReply(ports));

        Assert.False(config!.Ports[4].Forward);
        Assert.Equal(9, config.Ports[4].Type);
    }

    [Fact]
    public void Normalize_strips_the_windows_leading_report_id()
    {
        var info = UmbraDeviceFake.InfoReply();
        var prefixed = new byte[info.Length + 1];
        info.CopyTo(prefixed, 1);

        Assert.Equal(info, UmbraProtocol.Normalize(prefixed).ToArray());
        Assert.Equal(info, UmbraProtocol.Normalize(info).ToArray());
    }
}
