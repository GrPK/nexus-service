using System;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>
/// A glass that plays H.264 on its own decoder (lcd207's PlayH264 family): the HydroShift II OLED Curved
/// and the Universal Screen 8.8. <see cref="DecoderVideoPlayer"/> drives it through the panel's hub.
/// </summary>
public interface IDecoderVideoDriver
{
    string HandlerId { get; }

    /// <summary>While set, the glass plays a video on its own decoder and streamed frames are dropped.</summary>
    bool VideoOwnsGlass { get; set; }

    /// <summary>A display that lost power in sleep is waiting on its handshake; no video until it runs.</summary>
    bool ResumePending { get; }

    /// <summary>The backlight percent last set, restored after a screen saver dimmed it.</summary>
    int Brightness { get; }

    /// <summary>Readies the decoder for a video and returns the chunk size the glass takes, 0 when it did not answer.</summary>
    int BeginVideo(IBulkUsbPipe pipe, int frameRate);

    /// <summary>Queues one chunk of video; returns the blocks the glass now holds, or null when it did not answer.</summary>
    int? SendVideoChunk(IBulkUsbPipe pipe, ReadOnlySpan<byte> chunk, bool last, uint sessionTick);

    int? QueryVideoBuffer(IBulkUsbPipe pipe);

    /// <summary>Stops a video and readies the glass for pushed frames again.</summary>
    void EndVideo(IBulkUsbPipe pipe);

    /// <summary>Sets the backlight without changing the remembered <see cref="Brightness"/>.</summary>
    bool SetBacklight(IBulkUsbPipe pipe, int percent);

    /// <summary>Sends the Offline Mode now (the mode, then the sync-only SetClock and StopClock).</summary>
    bool ApplyOfflineMode(IBulkUsbPipe pipe, bool on);

    /// <summary>Set at shutdown once the glass is let go: no reconnect may take it back.</summary>
    void Retire();
}
