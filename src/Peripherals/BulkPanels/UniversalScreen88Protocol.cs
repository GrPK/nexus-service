using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Nexus.Service.Rendering;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>
/// The Lian Li Universal Screen 8.8 (1CBE:A088), a 480x1920 portrait framebuffer on a bulk pipe.
///
/// Every command is a 500-byte plaintext block DES-encrypted in CBC mode, then padded out
/// to a 512-byte packet with a two-byte magic tail. The key and the IV are both the ASCII
/// string "slv3tuzx". Every command is answered by a 512-byte reply echoing the command byte,
/// then a zero-length packet. Checked against L-Connect 3 (lcd207 WinUsb) captures on a
/// firmware lianli88_0001_0023 unit.
/// </summary>
public static class UniversalScreen88Protocol
{
    public const int VendorId = 0x1CBE;
    public const int ProductId = 0xA088;

    /// <summary>The glass in its own Windows display mode (lcd207 HIDUsb lcd88Hid), which L-Connect brings back with a HID write.</summary>
    public const int DesktopVendorId = 0x1A86;
    public const int DesktopProductId = 0xAD21;

    /// <summary>
    /// The HID interface the exit goes to, as a unit in its Windows display mode reports it: interface 1,
    /// usage page FF00, 513-byte reports both ways (id 0 plus 512), the OLED Curved's shape. L-Connect
    /// builds a 64-byte report, which its HID library pads to the interface's output length.
    /// </summary>
    public const int DesktopInterface = 1;
    public const int DesktopUsagePage = 0xFF00;
    public const int DesktopReportLength = 513;

    /// <summary>The bezel LED board, a USB function of its own beside the glass.</summary>
    public const int LedBoardVendorId = 0x0416;
    public const int LedBoardProductId = 0x8050;

    /// <summary>Native framebuffer: the glass shows a landscape frame only once it is turned a quarter clockwise into this.</summary>
    public const int Width = 480;
    public const int Height = 1920;

    /// <summary>Plaintext block size before encryption and padding.</summary>
    public const int PlainLength = 500;

    /// <summary>Encrypted block plus six zeros and the two-byte tail.</summary>
    public const int PacketLength = 512;

    /// <summary>Most a command can carry after the 8-byte preamble.</summary>
    public const int MaxParams = PlainLength - 8;

    public const byte CommandGetVersion = 0x0A;
    public const byte CommandReboot = 0x0B;
    public const byte CommandRotate = 0x0D;
    public const byte CommandBrightness = 0x0E;
    public const byte CommandSetClock = 0x33;
    public const byte CommandStopClock = 0x34;
    public const byte CommandPushJpeg = 0x65;
    public const byte CommandPushPng = 0x66;
    public const byte CommandStopPlay = 0x7B;

    /// <summary>Tells the decoder the frame rate of the video that follows (lcd207 SetFrameRate).</summary>
    public const byte CommandFrameRate = 0x0F;

    /// <summary>One chunk of an H.264 stream for the glass's own decoder; the reply's [8] is the blocks it holds.</summary>
    public const byte CommandStartPlay = 0x79;

    /// <summary>Asks how many video blocks the decoder still holds, in the reply's [8].</summary>
    public const byte CommandQueryBlock = 0x7A;

    /// <summary>L-Connect's fixed video chunk size for this glass (lcd207 WinUsb.PlayH264).</summary>
    public const int H264Block = 202752;

    /// <summary>A video chunk: StartPlay with the chunk length big-endian at params[0..4] and the last-chunk flag at [4], then the chunk, as one transfer.</summary>
    public static byte[] EncodeVideoChunk(ReadOnlySpan<byte> chunk, bool last, uint timestampMs)
    {
        Span<byte> parameters = stackalloc byte[5];
        parameters[0] = (byte)(chunk.Length >> 24);
        parameters[1] = (byte)(chunk.Length >> 16);
        parameters[2] = (byte)(chunk.Length >> 8);
        parameters[3] = (byte)chunk.Length;
        parameters[4] = (byte)(last ? 1 : 0);
        var header = EncodeCommand(CommandStartPlay, parameters, timestampMs);
        var packet = new byte[header.Length + chunk.Length];
        header.CopyTo(packet, 0);
        chunk.CopyTo(packet.AsSpan(header.Length));
        return packet;
    }

    /// <summary>
    /// The ffmpeg filter that lays a clip into the 480x1920 framebuffer for a mount given as the panel
    /// record's rotation: landscape (0, 180) a quarter turn clockwise like pushed frames, portrait (90, 270)
    /// untouched. The glass turns the half itself (the driver's Rotate), so the clip never does. Mirror flips
    /// the clip before it is turned.
    /// </summary>
    public static string VideoMountFilter(int rotation, bool mirror) => VideoMount(rotation) == 90
        ? $"scale={Width}:{Height}:flags=lanczos{(mirror ? ",hflip" : "")}"
        : $"scale={Height}:{Width}:flags=lanczos,{(mirror ? "hflip," : "")}transpose=1";

    /// <summary>The mount a clip is encoded for: 0 landscape or 90 portrait, as the glass turns the half itself.</summary>
    public static int VideoMount(int rotation) => (((rotation % 360) + 360) % 360) % 180;

    /// <summary>The blocks the decoder holds, from a StartPlay or QueryBlock reply.</summary>
    public static int DecodeBufferedBlocks(ReadOnlySpan<byte> reply) => reply.Length > 8 ? reply[8] : 0;

    /// <summary>
    /// Moves the glass to its own Windows display mode (the "LIANLI USB Secondary Display", 1A86:AD21),
    /// answered with the command byte; L-Connect reboots the glass right after it.
    /// </summary>
    public const byte CommandSwitchToDesktop = 0x96;

    /// <summary>Rotate values L-Connect sends on firmware 1.2+. They turn the firmware's own screens only, never a pushed JPEG (camera-checked).</summary>
    public const byte RotatePortrait = 0;
    public const byte RotateLandscape = 1;

    /// <summary>The half-turned values L-Connect adds on firmware 1.2+ (before 1.2 only portrait 0 and 2 exist).</summary>
    public const byte RotatePortraitHalfTurn = 2;
    public const byte RotateLandscapeHalfTurn = 3;

    /// <summary>SetClock mode that syncs the clock without changing whether the firmware clock face is enabled.</summary>
    private const byte ClockSyncOnly = 2;

    /// <summary>SetClock modes L-Connect's Offline Mode toggle sends: the firmware shows date and time while the PC is off.</summary>
    public const byte ClockOfflineOn = 1;
    public const byte ClockOfflineOff = 0;

    /// <summary>The firmware version L-Connect reads from "lianli88_NNNN_BBBB": build BBBB at 18 or more is (BBBB - 8) as major.minor tenths.</summary>
    public static Version? FirmwareVersion(string? raw)
    {
        if (raw is null || raw.Length != 18 || !raw.StartsWith("lianli88_", StringComparison.Ordinal) || raw[13] != '_'
            || !int.TryParse(raw.AsSpan(9, 4), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _)
            || !int.TryParse(raw.AsSpan(14, 4), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var build)
            || build < 18)
        {
            return null;
        }
        var tenths = build - 8;
        return new Version(tenths / 10, tenths % 10);
    }

    /// <summary>
    /// The Rotate value L-Connect sends for a mounting: on firmware 1.2+ portrait 0/2 and landscape 1/3 (the
    /// second of each half-turned); before 1.2 only 0 and 2. An unknown firmware is treated as 1.2+.
    /// </summary>
    public static byte RotateValue(bool portrait, bool halfTurn, Version? firmware)
    {
        if (firmware is not null && firmware < new Version(1, 2))
        {
            return halfTurn ? RotatePortraitHalfTurn : RotatePortrait;
        }
        return portrait
            ? (halfTurn ? RotatePortraitHalfTurn : RotatePortrait)
            : (halfTurn ? RotateLandscapeHalfTurn : RotateLandscape);
    }

    /// <summary>SetClock with an explicit mode: <see cref="ClockOfflineOn"/> or <see cref="ClockOfflineOff"/>.</summary>
    public static byte[] EncodeSetClock(DateTime now, uint timestampMs, byte mode) => EncodeCommand(CommandSetClock, new byte[]
    {
        (byte)(now.Year >> 8), (byte)now.Year, (byte)now.Month, (byte)now.Day,
        (byte)now.Hour, (byte)now.Minute, (byte)now.Second, mode,
    }, timestampMs);

    /// <summary>ASCII "slv3tuzx", used as both the DES key and the CBC IV.</summary>
    private static readonly byte[] Key = Encoding.ASCII.GetBytes("slv3tuzx");

    /// <summary>Closes every packet; the firmware rejects a block without it.</summary>
    private static ReadOnlySpan<byte> Tail => new byte[] { 0xA1, 0x1A };

    private static byte[]? _emptyOverlay;

    /// <summary>Builds one 512-byte packet; <paramref name="timestampMs"/> (plaintext [4..8], LE) must strictly increase within a session.</summary>
    public static byte[] EncodeCommand(byte command, ReadOnlySpan<byte> parameters, uint timestampMs)
    {
        if (parameters.Length > MaxParams)
        {
            throw new ArgumentException($"at most {MaxParams} parameter bytes", nameof(parameters));
        }
        var plain = new byte[PlainLength];
        plain[0] = command;
        plain[2] = 0x1A;
        plain[3] = 0x6D;
        plain[4] = (byte)(timestampMs & 0xFF);
        plain[5] = (byte)((timestampMs >> 8) & 0xFF);
        plain[6] = (byte)((timestampMs >> 16) & 0xFF);
        plain[7] = (byte)((timestampMs >> 24) & 0xFF);
        parameters.CopyTo(plain.AsSpan(8));

        var encrypted = Encrypt(plain);
        var packet = new byte[PacketLength];
        encrypted.CopyTo(packet.AsSpan());
        // Six zeros between the ciphertext and the tail; the array is already zeroed.
        Tail.CopyTo(packet.AsSpan(PacketLength - Tail.Length));
        return packet;
    }

    /// <summary>Header carrying the image length big-endian at params[0..4], then the image, as one transfer.</summary>
    public static byte[] EncodeImage(byte command, ReadOnlySpan<byte> image, uint timestampMs)
    {
        Span<byte> length = stackalloc byte[4];
        length[0] = (byte)(image.Length >> 24);
        length[1] = (byte)(image.Length >> 16);
        length[2] = (byte)(image.Length >> 8);
        length[3] = (byte)image.Length;
        var header = EncodeCommand(command, length, timestampMs);
        var packet = new byte[header.Length + image.Length];
        header.CopyTo(packet, 0);
        image.CopyTo(packet.AsSpan(header.Length));
        return packet;
    }

    /// <summary>Year (u16 BE), month, day, hour, minute, second, then the clock mode.</summary>
    public static byte[] EncodeSyncClock(DateTime now, uint timestampMs) => EncodeCommand(CommandSetClock, new byte[]
    {
        (byte)(now.Year >> 8), (byte)now.Year, (byte)now.Month, (byte)now.Day,
        (byte)now.Hour, (byte)now.Minute, (byte)now.Second, ClockSyncOnly,
    }, timestampMs);

    /// <summary>Firmware string from a GetVersion reply (ASCII at [8..40]), or null.</summary>
    public static string? DecodeVersion(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 40 || reply[0] != CommandGetVersion)
        {
            return null;
        }
        var text = reply.Slice(8, 32);
        int end = text.IndexOf((byte)0);
        var version = Encoding.ASCII.GetString(end < 0 ? text : text[..end]);
        return version.Length > 0 ? version : null;
    }

    /// <summary>
    /// A fully transparent framebuffer-sized PNG. The firmware composites a PNG layer over
    /// every JPEG and keeps whatever was last pushed there (L-Connect's sensor overlay), so
    /// one of these goes out at connect.
    /// </summary>
    public static byte[] EmptyOverlayPng()
    {
        if (_emptyOverlay is { } cached)
        {
            return cached;
        }
        using var image = RenderKit.NewImage(Width, Height);
        return _emptyOverlay = RenderKit.EncodePng(image);
    }

    /// <summary>
    /// DES-CBC with PKCS#7 padding, key and IV both the ASCII key. 500 bytes in, 504 out.
    /// DES is long broken as a cipher; it is used here only because it is what the panel's
    /// firmware implements, and the key is public.
    /// </summary>
    public static byte[] Encrypt(ReadOnlySpan<byte> plain)
    {
#pragma warning disable CA5351 // Broken cryptographic algorithm - dictated by the device firmware.
        using var des = DES.Create();
#pragma warning restore CA5351
        des.Key = Key;
        des.IV = Key;
        des.Mode = CipherMode.CBC;
        des.Padding = PaddingMode.PKCS7;
        using var encryptor = des.CreateEncryptor();
        var input = plain.ToArray();
        return encryptor.TransformFinalBlock(input, 0, input.Length);
    }
}
