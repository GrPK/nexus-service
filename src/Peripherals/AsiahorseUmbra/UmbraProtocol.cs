using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.AsiahorseUmbra;

// Byte layout bench-derived from the vendor app and USBPcap captures; see UmbraProtocolTests for the captured frames.
// Frames are 64 bytes with no report id; the hub prepends the zero report id on write.
// Never sent: FA (factory reset), F9 (save effect), FF (bootloader).
internal static class UmbraProtocol
{
    public const int VendorId = 0x1A86;
    public const int ProductId = 0xFE04;
    public const int FrameLength = 64;
    public const int PortCount = 10;
    public const int MaxLedCount = 120;
    public const int MaxOrder = 10;
    public const int StreamLedsPerPacket = 20;

    public const byte CmdInfo = 0x00;
    public const byte CmdReadPorts = 0x01;
    public const byte CmdWritePort = 0x02;
    public const byte CmdEffect = 0x03;
    public const byte CmdLightsOff = 0x06;
    public const byte CmdFanSync = 0x08;
    public const byte CmdBootMode = 0x0B;
    public const byte CmdSelfCheck = 0x0C;
    public const byte CmdAuthority = 0xFD;
    public const byte CmdFanDuty = 0xFB;
    public const byte CmdRestart = 0xFC;
    public const byte StreamHeader = 0x88;

    public const int EffectBlockLength = 52;
    public const int ColorSlots = 4;

    private const byte Magic0 = 0x52;
    private const byte Magic1 = 0x42;
    private const int HeaderLength = 5;

    private const int InfoModelOffset = 5;
    private const int InfoDeviceIdOffset = 9;
    private const int InfoDeviceIdLength = 8;
    private const int InfoFirmwareOffset = 17;
    private const int InfoBootModeOffset = 23;
    private const int InfoSelfCheckOffset = 24;
    private const int PortRecordsOffset = 6;
    private const int PortRecordLength = 5;
    private const int PortCapOffset = 56;

    // Fixed Mode boot effect: a merge-group frame sent right after "0B 01".
    private static readonly byte[] FixedBootBlock =
    {
        0x0A, 0x01, 0x01, 0x99,
        0xFF, 0xFF, 0xFF, 0x00, 0xFF, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0x00,
    };

    public static byte[] Frame(byte cmd, params byte[] args)
    {
        var length = HeaderLength + args.Length + 1;
        if (length > FrameLength)
        {
            throw new ArgumentException("Arguments exceed the frame.", nameof(args));
        }
        var frame = new byte[FrameLength];
        frame[0] = Magic0;
        frame[1] = Magic1;
        frame[2] = (byte)length;
        frame[3] = 0x00;
        frame[4] = cmd;
        Array.Copy(args, 0, frame, HeaderLength, args.Length);
        frame[length - 1] = Sum(frame, length - 1);
        return frame;
    }

    private static byte Sum(byte[] data, int count)
    {
        var sum = 0;
        for (var i = 0; i < count; i++)
        {
            sum += data[i];
        }
        return (byte)sum;
    }

    // Output report for the HID layer: zero report id, then the 64-byte frame.
    public static byte[] ToReport(byte[] frame)
    {
        var report = new byte[frame.Length + 1];
        Array.Copy(frame, 0, report, 1, frame.Length);
        return report;
    }

    // Windows delivers the zero report id as a leading byte; macOS and Linux do not.
    public static ReadOnlySpan<byte> Normalize(ReadOnlySpan<byte> reply)
    {
        if (reply.Length > 2 && reply[0] == 0x00 && reply[1] == Magic0 && reply[2] == Magic1)
        {
            return reply[1..];
        }
        return reply;
    }

    public static bool IsReplyTo(ReadOnlySpan<byte> reply, byte cmd) =>
        reply.Length >= HeaderLength && reply[0] == Magic0 && reply[1] == Magic1 && reply[4] == cmd;

    public static byte[] EncodeInfoRequest() => Frame(CmdInfo);

    public static byte[] EncodePortConfigRequest() => Frame(CmdReadPorts, 0xFF);

    public static byte[] EncodeSetPort(int port, UmbraPortField field, int value) =>
        Frame(CmdWritePort, (byte)port, (byte)field, (byte)value);

    public static byte[] EncodeLightsOff() => Frame(CmdLightsOff);

    // true: the controller drives the LEDs; false: it passes the ARGB input header through.
    public static byte[] EncodeAuthority(bool controllerDrives) =>
        Frame(CmdAuthority, controllerDrives ? (byte)0x01 : (byte)0x00);

    // true: fans follow the motherboard PWM header; false: the controller PWM runs them.
    public static byte[] EncodeFanSync(bool followMotherboard) =>
        Frame(CmdFanSync, followMotherboard ? (byte)0x00 : (byte)0x01);

    public static byte[] EncodeFanDuty(int percent) =>
        Frame(CmdFanDuty, (byte)Math.Clamp(percent, 0, 100));

    public static byte[] EncodeSelfCheck(bool on) =>
        Frame(CmdSelfCheck, on ? (byte)0xFF : (byte)0x01);

    public static byte[] EncodeRestart() => Frame(CmdRestart);

    public static byte[] EncodeBootMemory() => Frame(CmdBootMode, 0xFF);

    public static byte[][] EncodeBootFixed() =>
        new[] { Frame(CmdBootMode, 0x01), EncodeGroup(0xFE, 0xFE, FixedBootBlock) };

    public static byte[] EncodeSyncEffect(UmbraEffectBlock block) => EncodeGroup(0xFF, 0xFF, block);

    public static byte[] EncodeMergeEffect(UmbraEffectBlock block) => EncodeGroup(0xFE, 0xFE, block);

    private static byte[] EncodeGroup(byte a, byte b, UmbraEffectBlock block) => EncodeGroup(a, b, block.ToBytes());

    private static byte[] EncodeGroup(byte a, byte b, byte[] block)
    {
        var args = new byte[2 + EffectBlockLength];
        args[0] = a;
        args[1] = b;
        Array.Copy(block, 0, args, 2, block.Length);
        return Frame(CmdEffect, args);
    }

    // The firmware plays segment 5 for a generic fan; the other segments stay at 01 and the three slots repeat.
    public static byte[] EncodeAsyncEffect(int port, UmbraEffectBlock block)
    {
        var args = new byte[2 + EffectBlockLength];
        args[0] = 0xFD;
        args[1] = (byte)port;
        var i = 2;
        args[i++] = 0x04;
        for (var s = 0; s < 5; s++)
        {
            args[i++] = 0x01;
        }
        args[i++] = block.Effect;
        for (var r = 0; r < 3; r++)
        {
            args[i++] = block.Speed;
        }
        for (var r = 0; r < 3; r++)
        {
            args[i++] = block.Direction;
        }
        for (var r = 0; r < 3; r++)
        {
            args[i++] = block.Brightness;
        }
        for (var r = 0; r < 3; r++)
        {
            for (var c = 0; c < ColorSlots * 3; c++)
            {
                args[i++] = block.Colors[c];
            }
        }
        return Frame(CmdEffect, args);
    }

    public static int StreamPacketCount(int ledCount) =>
        (ledCount + StreamLedsPerPacket - 1) / StreamLedsPerPacket;

    // Raw 64-byte stream reports (not RB-framed): [88, total, index from 1, up to 60 RGB bytes, pad, sum of 0..62 at 63].
    public static byte[][] EncodeStream(ReadOnlySpan<byte> rgb)
    {
        var leds = rgb.Length / 3;
        var total = StreamPacketCount(leds);
        var packets = new byte[total][];
        for (var p = 0; p < total; p++)
        {
            var packet = new byte[FrameLength];
            packet[0] = StreamHeader;
            packet[1] = (byte)total;
            packet[2] = (byte)(p + 1);
            var offset = p * StreamLedsPerPacket * 3;
            var count = Math.Min(StreamLedsPerPacket * 3, leds * 3 - offset);
            rgb.Slice(offset, count).CopyTo(packet.AsSpan(3));
            packet[FrameLength - 1] = Sum(packet, FrameLength - 1);
            packets[p] = packet;
        }
        return packets;
    }

    public static UmbraInfo? DecodeInfo(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < InfoSelfCheckOffset + 1 || !IsReplyTo(reply, CmdInfo))
        {
            return null;
        }
        var model = (reply[InfoModelOffset] << 24) | (reply[InfoModelOffset + 1] << 16)
            | (reply[InfoModelOffset + 2] << 8) | reply[InfoModelOffset + 3];
        var id = Convert.ToHexString(reply.Slice(InfoDeviceIdOffset, InfoDeviceIdLength));
        var firmware = $"{reply[InfoFirmwareOffset]}.{reply[InfoFirmwareOffset + 1]}.{reply[InfoFirmwareOffset + 2]}";
        return new UmbraInfo(model, id, firmware, reply[InfoBootModeOffset] != 0x01, reply[InfoSelfCheckOffset] != 0x01);
    }

    public static UmbraPortConfig? DecodePortConfig(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < PortCapOffset + 2 || !IsReplyTo(reply, CmdReadPorts))
        {
            return null;
        }
        var ports = new UmbraPort[PortCount];
        for (var p = 0; p < PortCount; p++)
        {
            var o = PortRecordsOffset + PortRecordLength * p;
            ports[p] = new UmbraPort(reply[o], reply[o + 1], reply[o + 2] != 0, reply[o + 3], reply[o + 4]);
        }
        var cap = (reply[PortCapOffset] << 8) | reply[PortCapOffset + 1];
        return new UmbraPortConfig(ports, cap);
    }
}

internal enum UmbraPortField : byte
{
    LedCount = 0x00,
    Multiplier = 0x01,
    Direction = 0x02,
    Order = 0x03,
}

internal sealed record UmbraInfo(int ModelCode, string DeviceId, string Firmware, bool BootMemory, bool SelfCheck);

// Order is the firmware value, 0-based; the API shows it 1-based.
public sealed record UmbraPort(int LedCount, int Multiplier, bool Forward, int Order, int Type)
{
    public static readonly UmbraPort Empty = new(0, 0, true, 0, 0);
}

public sealed record UmbraPortConfig(UmbraPort[] Ports, int BrightnessCap);

// Wire form of one effect: byte ids already resolved, colors as four RGB triples.
internal readonly record struct UmbraEffectBlock(byte Effect, byte Speed, byte Direction, byte Brightness, byte[] Colors)
{
    public byte[] ToBytes()
    {
        var block = new byte[UmbraProtocol.EffectBlockLength];
        block[0] = Effect;
        block[1] = Speed;
        block[2] = Direction;
        block[3] = Brightness;
        Array.Copy(Colors, 0, block, 4, Math.Min(Colors.Length, UmbraProtocol.ColorSlots * 3));
        return block;
    }
}
