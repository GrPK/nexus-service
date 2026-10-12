using System;
using System.Text;
using Nexus.Service.Peripherals.Hid;

namespace Nexus.Service.Peripherals.JpegPanels;

/// <summary>A screen's controller port and place on that port, -1 when unreported, and the serial L-Connect stamped on it.</summary>
public sealed record TlLcdIdentity(int Port, int Index, string? Serial);

/// <summary>
/// Control channel for one wired UNI FAN TL LCD screen (04FC:7393), transcribed from
/// L-Connect 3 (LConnectCore.Products.TLLCD) and never run against a unit. Commands use the
/// frame chunk's 11-byte header in 512-byte reports; replies are 64-byte reports echoing the
/// command. The firmware, serial-number and boot-media commands write flash and are never sent.
/// </summary>
public sealed class TlLcdHandshake : IJpegPanelHandshake
{
    /// <summary>Two replies: the firmware version string, then its build date.</summary>
    public const byte CmdProductInfo = 0x3D;

    /// <summary>Reply payload: 32-byte serial, port, screen index.</summary>
    public const byte CmdReadSerial = 0x3E;

    public const byte CmdLcdControl = 0x40;

    /// <summary>A frame with no per-chunk ack, which is how L-Connect streams video and sensor screens.</summary>
    public const byte CmdSyncJpeg = 0x46;

    /// <summary>LCDControlMode.LCDSetting: applies the rotation without changing what the glass shows.</summary>
    private const byte ModeLcdSetting = 0x05;

    // L-Connect's LCDControlSetting defaults: this packet matches its rotation packet byte for byte.
    private const byte SettingBrightness = 50;
    private const byte SettingVideoFps = 30;

    /// <summary>L-Connect's read timeout for this panel.</summary>
    private const int ReplyTimeoutMs = 200;

    /// <summary>Replies to skip while looking for the one a command asked for.</summary>
    private const int MaxStaleReplies = 4;

    private const int MaxDrainedReplies = 8;

    private const int HeaderLength = 11;
    private const int InputReportLength = 64;
    private const int SerialBytes = 32;
    private const int PortCount = 4;
    private const int ScreensPerPort = 3;

    private readonly byte[] _reply = new byte[InputReportLength];

    /// <summary>Set by <see cref="OnAttach"/>; ports and indexes are -1 until a screen answers.</summary>
    public TlLcdIdentity Identity { get; private set; } = new(-1, -1, null);

    public string? Firmware { get; private set; }

    /// <summary>A frame read found the handle dead; the worker detaches and re-opens the screen.</summary>
    public bool HandleDead { get; private set; }

    public bool OnAttach(IHidDevice device, int reportLength)
    {
        var report = new byte[reportLength];
        if (!Drain(device))
        {
            return false;
        }

        // Query commands only: nothing that changes what a stock-firmware screen shows.
        if (!WriteCommand(device, report, CmdReadSerial, ReadOnlySpan<byte>.Empty))
        {
            return false;
        }
        if (ReadReply(device, CmdReadSerial) is { } serialReply)
        {
            Identity = ParseIdentity(serialReply);
        }

        if (WriteCommand(device, report, CmdProductInfo, ReadOnlySpan<byte>.Empty)
            && ReadReply(device, CmdProductInfo) is { } version)
        {
            string text = ReplyText(version);
            string? date = ReadReply(device, CmdProductInfo) is { } dateReply ? ReplyText(dateReply) : null;
            Firmware = string.IsNullOrEmpty(date) ? text : $"{text} ({date})";
        }
        return true;
    }

    /// <summary>
    /// The display setting with <paramref name="rotation"/> (0-3, quarter turns). Its ack is left
    /// queued for the next frame's drain; L-Connect ignores a missing ack too.
    /// </summary>
    public static bool WriteDisplaySetting(IHidDevice device, byte[] report, byte rotation)
    {
        Span<byte> setting = stackalloc byte[11];
        setting.Clear();
        setting[0] = ModeLcdSetting;
        setting[4] = SettingBrightness;
        setting[5] = SettingVideoFps;
        setting[6] = (byte)(rotation & 0x03);
        return WriteCommand(device, report, CmdLcdControl, setting);
    }

    /// <summary>No application mode to leave; L-Connect re-applies its screens when it starts.</summary>
    public void OnDetach(IHidDevice device, int reportLength)
    {
    }

    /// <summary>Empties queued replies without waiting; false on a dead handle, which would burn the write timeout per chunk.</summary>
    public bool BeforeFrame(IHidDevice device, int reportLength, long nowMs)
    {
        if (Drain(device))
        {
            return true;
        }
        HandleDead = true;
        return false;
    }

    /// <summary>Parses a read-serial reply; ports and indexes outside the controller's range read as -1.</summary>
    public static TlLcdIdentity ParseIdentity(ReadOnlySpan<byte> reply)
    {
        var data = Payload(reply);
        if (data.Length < SerialBytes + 2)
        {
            return new TlLcdIdentity(-1, -1, null);
        }
        int port = data[SerialBytes];
        int index = data[SerialBytes + 1];
        string serial = Encoding.ASCII.GetString(data[..SerialBytes]).TrimEnd('\0').Trim();
        bool printable = serial.Length > 0;
        foreach (char c in serial)
        {
            printable &= c >= 0x20 && c < 0x7F;
        }
        return new TlLcdIdentity(
            port < PortCount ? port : -1,
            index < ScreensPerPort ? index : -1,
            printable ? serial : null);
    }

    private static string ReplyText(ReadOnlySpan<byte> reply) =>
        Encoding.ASCII.GetString(Payload(reply)).TrimEnd('\0').Trim();

    /// <summary>The reply's payload, clamped to what the report actually carries.</summary>
    private static ReadOnlySpan<byte> Payload(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < HeaderLength)
        {
            return ReadOnlySpan<byte>.Empty;
        }
        int length = Math.Min((reply[9] << 8) | reply[10], reply.Length - HeaderLength);
        return reply.Slice(HeaderLength, length);
    }

    private static bool WriteCommand(IHidDevice device, byte[] report, byte command, ReadOnlySpan<byte> payload)
    {
        JpegPanelProtocol.FillChunk(report, JpegPanelHeaderStyle.LianLiSequenced, command, payload, 0, 0);
        return device.Write(report);
    }

    /// <summary>Reads until the reply to <paramref name="command"/> arrives; null on timeout.</summary>
    private byte[]? ReadReply(IHidDevice device, byte command)
    {
        for (int attempt = 0; attempt < MaxStaleReplies; attempt++)
        {
            Array.Clear(_reply);
            int read = device.Read(_reply, ReplyTimeoutMs);
            if (read <= 0)
            {
                return null;
            }
            if (_reply[0] == 0x02 && _reply[1] == command)
            {
                return _reply[..read];
            }
        }
        return null;
    }

    private bool Drain(IHidDevice device)
    {
        for (int i = 0; i < MaxDrainedReplies; i++)
        {
            int read = device.Read(_reply, 0);
            if (read < 0)
            {
                return false;
            }
            if (read == 0)
            {
                break;
            }
        }
        return true;
    }
}
