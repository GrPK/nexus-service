using System;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>
/// The Universal Screen 8.8's bezel LED board (0416:8050), a USB function separate from the glass
/// (1CBE:A088). 64-byte commands on a WinUSB bulk pipe: 0x10 asks the firmware version, 0x11 sets 20
/// LEDs from the one it names. The board has no effects of its own; L-Connect streams every frame.
/// </summary>
public static class UniversalScreen88LedProtocol
{
    /// <summary>
    /// The bezel LEDs' own Nexus Control id, apart from the glass's (<see cref="UniversalScreen88Driver.Id"/>):
    /// the screen also serves as a plain Windows monitor with the glass left to Windows, and the ring is
    /// lit either way.
    /// </summary>
    public const string HandlerId = "lianli-screen88-led";

    public const int VendorId = 0x0416;
    public const int ProductId = 0x8050;

    public const byte WritePipe = 0x01;
    public const byte ReadPipe = 0x81;

    public const int LedCount = UniversalScreen88LedEffects.LedCount;

    /// <summary>LEDs per frame chunk; each chunk names the LED it starts at.</summary>
    public const int LedsPerChunk = 20;

    public const byte Version = 0x10;
    public const byte LedFrame = 0x11;

    private const int PacketSize = 64;

    public static byte[] EncodeVersionQuery()
    {
        var packet = new byte[PacketSize];
        packet[0] = Version;
        return packet;
    }

    /// <summary>"major_minor" from a version reply, or null when the reply is not one.</summary>
    public static string? DecodeVersion(ReadOnlySpan<byte> reply) =>
        reply.Length >= 3 && reply[0] == Version && reply[1] > 0 ? $"{reply[1]}_{reply[2]}" : null;

    /// <summary>One frame of packed RGB as the three chunk commands it travels in, as L-Connect sends it.</summary>
    public static byte[][] EncodeLedFrame(ReadOnlySpan<byte> rgb)
    {
        if (rgb.Length != LedCount * 3)
        {
            throw new ArgumentException($"expected {LedCount * 3} bytes", nameof(rgb));
        }
        var chunks = new byte[LedCount / LedsPerChunk][];
        for (int c = 0; c < chunks.Length; c++)
        {
            int start = c * LedsPerChunk;
            var packet = new byte[PacketSize];
            packet[0] = LedFrame;
            packet[1] = (byte)start;
            rgb.Slice(start * 3, LedsPerChunk * 3).CopyTo(packet.AsSpan(4));
            chunks[c] = packet;
        }
        return chunks;
    }
}
