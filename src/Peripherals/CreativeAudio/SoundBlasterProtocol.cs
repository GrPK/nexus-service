using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Nexus.Service.Peripherals.CreativeAudio;

/// <summary>
/// Creative's control protocol for the Sound Blaster G8 / G6 USB DACs: 64-byte HID output reports
/// on the vendor interface (MI_04), each <c>5A [command] [length] [payload]</c>, answered by input
/// reports of the same shape. Command numbers as Creative App names them (CDCRawCommand); the
/// DSP effects are "Malcolm" parameters addressed by (family, id) and carried as little-endian f32:
/// family 0x96 playback (SBX and EQ, MalcolmPlaybackManagerCommand), 0x95 recording
/// (MalcolmVoiceInputManagerCommand), 0x97 decoder. Read from a G8 (firmware 1.0.240911.1010)
/// with read requests; the write shapes match Creative App's and the public G6 notes.
/// </summary>
public static class SoundBlasterProtocol
{
    public const int VendorId = 0x041E;
    public const byte Magic = 0x5A;
    /// <summary>Report length with the report id byte in front.</summary>
    public const int ReportLength = 65;

    // Commands (CDCRawCommand).
    public const byte Ack = 0x02;
    public const byte FirmwareString = 0x07;
    public const byte DeviceInformation = 0x09;
    public const byte GetParameter = 0x11;
    public const byte SetParameter = 0x12;
    public const byte GlobalProfile = 0x26;
    public const byte OutputSelect = 0x2C;
    public const byte MicGain = 0x3C;
    public const byte DacFilter = 0x6C;

    // Parameter families.
    public const byte Recording = 0x95;
    public const byte Playback = 0x96;
    public const byte Decoder = 0x97;

    // Global profile bits (command 0x26).
    public const int ProfileSbx = 0x01;
    public const int ProfileScout = 0x02;
    public const int ProfileVoiceClarity = 0x04;

    // Output targets (MultiplexOutput).
    public const byte OutputSpeakers = 0x02;
    public const byte OutputHeadphones = 0x04;

    /// <summary>The mic gain control id the device lists (the only one).</summary>
    public const byte MicBoostControl = 0x02;

    /// <summary>A report: report id 0, the magic byte, the command, the payload length and the payload.</summary>
    public static byte[] Frame(byte command, params byte[] payload)
    {
        if (payload.Length > ReportLength - 4)
        {
            throw new ArgumentException("payload too long", nameof(payload));
        }
        var report = new byte[ReportLength];
        report[1] = Magic;
        report[2] = command;
        report[3] = (byte)payload.Length;
        payload.CopyTo(report, 4);
        return report;
    }

    public static byte[] ReadParameter(byte family, byte id) => Frame(GetParameter, 0x01, family, id);

    public static byte[] WriteParameter(byte family, byte id, float value)
    {
        var payload = new byte[7];
        payload[0] = 0x01;
        payload[1] = family;
        payload[2] = id;
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(3), value);
        return Frame(SetParameter, payload);
    }

    public static byte[] ReadProfile() => Frame(GlobalProfile, 0x08, 0xFF, 0xFF);

    /// <summary>SBX master (<paramref name="which"/> 1) or Scout Mode (2) on or off.</summary>
    public static byte[] WriteProfile(byte which, bool on) => Frame(GlobalProfile, 0x07, which, 0x00, (byte)(on ? 1 : 0), 0x00);

    public static byte[] ReadOutput() => Frame(OutputSelect, 0x01);
    public static byte[] ListOutputs() => Frame(OutputSelect, 0x02);
    public static byte[] WriteOutput(byte output) => Frame(OutputSelect, 0x00, output, 0x00, 0x00, 0x00);

    public static byte[] ReadFilter() => Frame(DacFilter, 0x01);
    public static byte[] ListFilters() => Frame(DacFilter, 0x02);
    public static byte[] WriteFilter(byte filter) => Frame(DacFilter, 0x00, filter, 0x00);

    public static byte[] ReadMicBoost() => Frame(MicGain, 0x01, 0x00);
    public static byte[] WriteMicBoost(byte db) => Frame(MicGain, 0x00, 0x00, MicBoostControl, db);

    public static byte[] ReadFirmware() => Frame(FirmwareString, 0x02);
    public static byte[] ReadSerial() => Frame(DeviceInformation, 0x03);

    /// <summary>The command of an input report, or null when it is not one of this protocol's.</summary>
    public static byte? CommandOf(ReadOnlySpan<byte> report) =>
        report.Length >= 4 && report[1] == Magic ? report[2] : null;

    /// <summary>An acknowledgement: the command it answers and its status (0 = done).</summary>
    public static bool TryParseAck(ReadOnlySpan<byte> report, out byte command, out byte status)
    {
        command = 0;
        status = 0;
        if (CommandOf(report) != Ack || report.Length < 6)
        {
            return false;
        }
        command = report[4];
        status = report[5];
        return true;
    }

    /// <summary>A parameter value: <c>5A 11 08 01 00 [family] [id] [f32]</c> (asked for, or pushed after a change).</summary>
    public static bool TryParseParameter(ReadOnlySpan<byte> report, out byte family, out byte id, out float value)
    {
        family = 0;
        id = 0;
        value = 0;
        if (CommandOf(report) != GetParameter || report.Length < 12 || report[3] < 8)
        {
            return false;
        }
        family = report[6];
        id = report[7];
        value = BinaryPrimitives.ReadSingleLittleEndian(report.Slice(8, 4));
        return true;
    }

    /// <summary>The global profile bits from a <c>5A 26 0B 08 FF FF [bits]</c> reply or push.</summary>
    public static int? ParseProfile(ReadOnlySpan<byte> report) =>
        CommandOf(report) == GlobalProfile && report.Length > 7 && report[4] == 0x08 ? report[7] : null;

    /// <summary>The selected output from <c>5A 2C 05 01 [output]</c>.</summary>
    public static byte? ParseOutput(ReadOnlySpan<byte> report) =>
        CommandOf(report) == OutputSelect && report.Length > 5 && report[4] == 0x01 ? report[5] : null;

    /// <summary>The outputs listed by <c>5A 2C 0A 02 [0x80 | count] 02 00 00 00 04 00 00 00</c>: one u32 each after the count byte.</summary>
    public static IReadOnlyList<byte> ParseOutputList(ReadOnlySpan<byte> report)
    {
        var list = new List<byte>();
        if (CommandOf(report) != OutputSelect || report.Length < 6 || report[4] != 0x02)
        {
            return list;
        }
        var count = report[5] & 0x7F;
        for (var i = 0; i < count && 6 + i * 4 < report.Length; i++)
        {
            list.Add(report[6 + i * 4]);
        }
        return list;
    }

    /// <summary>The selected DAC filter from <c>5A 6C 03 01 [filter] 00</c>.</summary>
    public static byte? ParseFilter(ReadOnlySpan<byte> report) =>
        CommandOf(report) == DacFilter && report.Length > 5 && report[4] == 0x01 ? report[5] : null;

    /// <summary>The DAC filters listed by <c>5A 6C 0E 02 [count] [flags] 00 [id 00]...</c>.</summary>
    public static IReadOnlyList<byte> ParseFilterList(ReadOnlySpan<byte> report)
    {
        var list = new List<byte>();
        if (CommandOf(report) != DacFilter || report.Length < 8 || report[4] != 0x02)
        {
            return list;
        }
        var count = report[5];
        for (var i = 0; i < count && 8 + i * 2 < report.Length; i++)
        {
            list.Add(report[8 + i * 2]);
        }
        return list;
    }

    /// <summary>The mic boost in dB from <c>5A 3C 04 01 00 02 [dB]</c>.</summary>
    public static byte? ParseMicBoost(ReadOnlySpan<byte> report) =>
        CommandOf(report) == MicGain && report.Length > 7 && report[4] == 0x01 && report[6] == MicBoostControl ? report[7] : null;

    /// <summary>The ASCII text of a firmware (<c>5A 07 [len] text</c>) or serial (<c>5A 09 [len] 03 text</c>) reply.</summary>
    public static string? ParseText(ReadOnlySpan<byte> report, int skip)
    {
        var command = CommandOf(report);
        if (command is null || report.Length < 4)
        {
            return null;
        }
        var start = 4 + skip;
        var end = Math.Min(report.Length, 4 + report[3]);
        if (start >= end)
        {
            return null;
        }
        var text = report[start..end];
        var nul = text.IndexOf((byte)0);
        return Encoding.ASCII.GetString(nul >= 0 ? text[..nul] : text).Trim();
    }
}
