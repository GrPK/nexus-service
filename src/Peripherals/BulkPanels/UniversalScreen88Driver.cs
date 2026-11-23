using System;
using System.Collections.Generic;
using System.Diagnostics;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.JpegPanels;
using Nexus.Service.Peripherals.PixelFormats;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>
/// The Lian Li Universal Screen 8.8 glass, run as a full widget panel: 1920x480 when mounted
/// landscape, 480x1920 portrait. Its 60-LED bezel is a separate USB function (0416:8050) that
/// the bundled OpenRGB drives. In its own Windows display mode (L-Connect's Secondary Screen,
/// 1A86:AD21) the glass is a USB monitor and this driver never sees it.
/// </summary>
public sealed class UniversalScreen88Driver : IBulkPanelDriver, IDecoderVideoDriver
{
    public const string Id = "lianli-screen88";

    private const int ReplyTimeoutMs = 1000;

    /// <summary>The panel render rate; the glass acks frames faster than this (measured), so the link is never the ceiling.</summary>
    private const int FrameRate = 30;
    private const int JpegQuality = 85;

    /// <summary>Larger than one 512-byte reply, so the zero-length packet that ends it completes the same read.</summary>
    private readonly byte[] _reply = new byte[1024];

    private readonly Stopwatch _clock = new();
    private uint _lastTimestamp;
    private BgraJpegEncoder? _jpeg;
    private byte[] _native = Array.Empty<byte>();
    private Func<bool> _wantsPortrait = () => false;
    private Func<bool> _wantsHalfTurn = () => false;
    private volatile bool _portrait;
    private volatile bool _halfTurn;
    private volatile bool _handshakeWanted;
    private volatile bool _videoOwnsGlass;
    private volatile bool _retired;
    private volatile bool _sessionUp;
    private bool _writeFailureLogged;

    public string HandlerId => Id;
    public string Name => "Lian Li Universal Screen 8.8";
    public int VendorId => UniversalScreen88Protocol.VendorId;
    public IReadOnlyList<int> ProductIds { get; } = new[] { UniversalScreen88Protocol.ProductId };

    /// <summary>A 4:1 strip carries a page of widgets like a promoted monitor, not one cooler tile.</summary>
    public string Surface => Models.Panel.PanelSurfaces.Monitor;

    public int Fps => FrameRate;
    public byte WritePipeId => 0x01;
    public byte ReadPipeId => 0x81;
    public bool NeedsHidChannel => false;
    public bool SupportsBrightness => true;
    public bool SupportsSecondaryMonitor => true;
    public bool SupportsPortrait => true;

    /// <summary>
    /// The Rotate values 2 and 3 (the half turns L-Connect sends) flip the pushed picture too, not only the
    /// firmware's own screens: with the frame also flipped, a unit mounted at 180 showed the same as 0.
    /// </summary>
    public bool TurnsHalfItself => true;
    public bool RenegotiateOnRejectedFrames => true;

    /// <summary>The glass's real density (1920x480 on an 8.8 in diagonal); the monitor surface's desk-monitor estimate doubles the grid's rows.</summary>
    public double? Dpi => 225;

    /// <summary>Firmware string read at connect, or null before it.</summary>
    public string? Firmware { get; private set; }

    /// <summary>
    /// The mounting the session must run at differs from the one it negotiated, or a setting the handshake
    /// sends (Offline Mode, the Windows display switch) changed.
    /// </summary>
    public bool GeometryStale => _sessionUp && (_handshakeWanted || _wantsPortrait() != _portrait || _wantsHalfTurn() != _halfTurn);

    /// <summary>Reads the panel record's portrait setting at each handshake.</summary>
    public void BindPortrait(Func<bool> source) => _wantsPortrait = source;

    /// <summary>Reads whether the panel record turns the glass a half turn, which the glass then applies to all it shows.</summary>
    public void BindHalfTurn(Func<bool> source) => _wantsHalfTurn = source;

    /// <summary>The user's Offline Mode, or null while never set in Nexus (the glass's own flag is then left as found).</summary>
    public Func<bool?>? OfflineModeSource { get; set; }

    /// <summary>
    /// The user's choice for the glass's own Windows display mode: true moves a glass found in panel mode
    /// into it (as L-Connect's Secondary Screen toggle), false or null leaves it in panel mode. Coming back
    /// from that mode is <see cref="DesktopModeExit"/>'s job, and only when the user asked for it.
    /// </summary>
    public Func<bool?>? WindowsDisplaySource { get; set; }

    /// <summary>The glass was last moved to its Windows display mode by this driver.</summary>
    public bool SwitchedToWindowsDisplay { get; private set; }

    /// <summary>Asks the worker to run the handshake again on the open pipe, so a changed setting goes out.</summary>
    public void RequestHandshake() => _handshakeWanted = true;

    /// <summary>Set at shutdown once the glass is let go: no reconnect may take it back.</summary>
    public void Retire() => _retired = true;

    /// <summary>Milliseconds since connect, clamped to strictly increase: the firmware drops a repeated stamp.</summary>
    private uint NextTimestamp()
    {
        var now = (uint)_clock.ElapsedMilliseconds;
        _lastTimestamp = now > _lastTimestamp ? now : _lastTimestamp + 1;
        return _lastTimestamp;
    }

    /// <summary>
    /// L-Connect's apply sequence. StopClock takes down the firmware's own screen, and the
    /// empty PNG clears an overlay layer another host left composited over every JPEG.
    /// Re-run on an open pipe when only the mounting changed.
    /// </summary>
    public (int Width, int Height)? Connect(IBulkUsbPipe pipe, IHidDevice? hid)
    {
        _sessionUp = false;
        if (_retired)
        {
            return null;
        }
        _clock.Restart();
        _lastTimestamp = 0;

        var version = Exchange(pipe, UniversalScreen88Protocol.CommandGetVersion, UniversalScreen88Protocol.EncodeCommand(
            UniversalScreen88Protocol.CommandGetVersion, ReadOnlySpan<byte>.Empty, NextTimestamp()));
        if (version is null)
        {
            ServiceLog.Warn($"[{HandlerId}] no reply to GetVersion");
            return null;
        }
        Firmware = UniversalScreen88Protocol.DecodeVersion(version);
        _handshakeWanted = false;

        if (WindowsDisplaySource?.Invoke() == true)
        {
            // L-Connect's SetDesktopMode: StopPlay, SwitchToDesktop, then a reboot into the USB display identity.
            Command(pipe, UniversalScreen88Protocol.CommandStopPlay, ReadOnlySpan<byte>.Empty);
            var switched = Command(pipe, UniversalScreen88Protocol.CommandSwitchToDesktop, ReadOnlySpan<byte>.Empty);
            if (switched)
            {
                pipe.Write(UniversalScreen88Protocol.EncodeCommand(
                    UniversalScreen88Protocol.CommandReboot, ReadOnlySpan<byte>.Empty, NextTimestamp()));
                SwitchedToWindowsDisplay = true;
                ServiceLog.Info($"[{HandlerId}] moved to its Windows display mode, as set; rebooting into it");
                return null;
            }
            ServiceLog.Warn($"[{HandlerId}] the glass refused the switch to its Windows display mode; staying a panel");
        }
        SwitchedToWindowsDisplay = false;

        var portrait = _wantsPortrait();
        var halfTurn = _wantsHalfTurn();
        Command(pipe, UniversalScreen88Protocol.CommandStopPlay, ReadOnlySpan<byte>.Empty);
        Command(pipe, UniversalScreen88Protocol.CommandRotate, stackalloc byte[]
        {
            UniversalScreen88Protocol.RotateValue(portrait, halfTurn, UniversalScreen88Protocol.FirmwareVersion(Firmware)),
        });
        // L-Connect re-sends its Offline Mode on connect; Nexus does so only for a mode the user set here,
        // and the sync-only SetClock below leaves the flag alone.
        if (OfflineModeSource?.Invoke() is { } offline)
        {
            SetOfflineMode(pipe, offline);
        }
        Exchange(pipe, UniversalScreen88Protocol.CommandSetClock,
            UniversalScreen88Protocol.EncodeSyncClock(DateTime.Now, NextTimestamp()));
        Command(pipe, UniversalScreen88Protocol.CommandStopClock, stackalloc byte[] { 0 });
        Exchange(pipe, UniversalScreen88Protocol.CommandPushPng, UniversalScreen88Protocol.EncodeImage(
            UniversalScreen88Protocol.CommandPushPng, UniversalScreen88Protocol.EmptyOverlayPng(), NextTimestamp()));

        _jpeg?.Dispose();
        _jpeg = new BgraJpegEncoder(UniversalScreen88Protocol.Width, UniversalScreen88Protocol.Height, JpegQuality);
        _native = portrait ? Array.Empty<byte>() : new byte[UniversalScreen88Protocol.Width * UniversalScreen88Protocol.Height * 4];
        _portrait = portrait;
        _halfTurn = halfTurn;
        ServiceLog.Info($"[{HandlerId}] firmware {Firmware ?? "unknown"}, {(portrait ? "portrait" : "landscape")}");
        _sessionUp = true;
        return portrait
            ? (UniversalScreen88Protocol.Width, UniversalScreen88Protocol.Height)
            : (UniversalScreen88Protocol.Height, UniversalScreen88Protocol.Width);
    }

    public bool SendFrame(IBulkUsbPipe pipe, IHidDevice? hid, ReadOnlySpan<byte> bgra)
    {
        if (VideoOwnsGlass)
        {
            return true;
        }
        if (_jpeg is null)
        {
            return false;
        }
        var native = bgra;
        if (!_portrait)
        {
            BgraQuarterTurn.RotateCw(bgra, UniversalScreen88Protocol.Height, UniversalScreen88Protocol.Width, _native);
            native = _native;
        }
        var packet = UniversalScreen88Protocol.EncodeImage(
            UniversalScreen88Protocol.CommandPushJpeg, _jpeg.Encode(native), NextTimestamp());
        int written = pipe.WriteCounted(WritePipeId, packet);
        if (written != packet.Length)
        {
            if (!_writeFailureLogged)
            {
                _writeFailureLogged = true;
                ServiceLog.Warn(written < 0
                    ? $"[{HandlerId}] frame write of {packet.Length} bytes returned an unknown count"
                    : $"[{HandlerId}] frame write took {written} of {packet.Length} bytes");
            }
            // A stalled endpoint refuses every later write until it is reset. The packet is
            // resent only when nothing went out; after a partial write the glass is resynced
            // by the handshake the transport reruns instead.
            bool halted = pipe.ClearHalt(WritePipeId);
            if (written != 0 || !halted || !pipe.Write(packet))
            {
                return false;
            }
        }
        _writeFailureLogged = false;
        ReadReply(pipe, UniversalScreen88Protocol.CommandPushJpeg);
        return true;
    }

    /// <summary>Sets the firmware flag that shows the date and time while the PC is off, as L-Connect's toggle does.</summary>
    public bool SetOfflineMode(IBulkUsbPipe pipe, bool on) =>
        Exchange(pipe, UniversalScreen88Protocol.CommandSetClock, UniversalScreen88Protocol.EncodeSetClock(
            DateTime.Now, NextTimestamp(), on ? UniversalScreen88Protocol.ClockOfflineOn : UniversalScreen88Protocol.ClockOfflineOff)) is not null;

    /// <summary>
    /// A live toggle: the mode, then the sync-only SetClock and StopClock the connect sequence ends with,
    /// so the firmware clock face can never come up over Nexus's output.
    /// </summary>
    public bool ApplyOfflineMode(IBulkUsbPipe pipe, bool on)
    {
        bool sent = SetOfflineMode(pipe, on);
        Exchange(pipe, UniversalScreen88Protocol.CommandSetClock,
            UniversalScreen88Protocol.EncodeSyncClock(DateTime.Now, NextTimestamp()));
        Command(pipe, UniversalScreen88Protocol.CommandStopClock, stackalloc byte[] { 0 });
        return sent;
    }

    /// <summary>The glass takes half the percent (L-Connect sends 50 for 100%).</summary>
    public bool SetBrightness(IBulkUsbPipe pipe, IHidDevice? hid, int percent)
    {
        Brightness = Math.Clamp(percent, 0, 100);
        return SetBacklight(pipe, Brightness);
    }

    /// <summary>The backlight percent last set, restored after a screen saver dimmed it.</summary>
    public int Brightness { get; private set; } = 100;

    /// <summary>Sets the backlight without changing the remembered <see cref="Brightness"/>.</summary>
    public bool SetBacklight(IBulkUsbPipe pipe, int percent) =>
        Command(pipe, UniversalScreen88Protocol.CommandBrightness, stackalloc byte[] { (byte)(Math.Clamp(percent, 0, 100) / 2) });

    /// <summary>While set, the glass plays a video on its own decoder and streamed frames are dropped.</summary>
    public bool VideoOwnsGlass { get => _videoOwnsGlass; set => _videoOwnsGlass = value; }

    /// <summary>This glass has no resume handshake of its own; the connection worker remakes a lost session.</summary>
    public bool ResumePending => false;

    /// <summary>L-Connect's video start (lcd207 PlayH264): the decoder stopped, the clock face down, the rate set. The chunk size is fixed.</summary>
    public int BeginVideo(IBulkUsbPipe pipe, int frameRate)
    {
        Command(pipe, UniversalScreen88Protocol.CommandStopPlay, ReadOnlySpan<byte>.Empty);
        Command(pipe, UniversalScreen88Protocol.CommandStopClock, stackalloc byte[] { 0 });
        return Command(pipe, UniversalScreen88Protocol.CommandFrameRate, stackalloc byte[] { (byte)frameRate })
            ? UniversalScreen88Protocol.H264Block
            : 0;
    }

    /// <summary>Queues one chunk of video; returns the blocks the glass now holds, or null when it did not answer. This glass takes no session tick.</summary>
    public int? SendVideoChunk(IBulkUsbPipe pipe, ReadOnlySpan<byte> chunk, bool last, uint sessionTick)
    {
        var reply = Exchange(pipe, UniversalScreen88Protocol.CommandStartPlay,
            UniversalScreen88Protocol.EncodeVideoChunk(chunk, last, NextTimestamp()));
        return reply is null ? null : UniversalScreen88Protocol.DecodeBufferedBlocks(reply);
    }

    public int? QueryVideoBuffer(IBulkUsbPipe pipe)
    {
        var reply = Exchange(pipe, UniversalScreen88Protocol.CommandQueryBlock, UniversalScreen88Protocol.EncodeCommand(
            UniversalScreen88Protocol.CommandQueryBlock, ReadOnlySpan<byte>.Empty, NextTimestamp()));
        return reply is null ? null : UniversalScreen88Protocol.DecodeBufferedBlocks(reply);
    }

    /// <summary>Stops a video and readies the glass for pushed frames again: the decoder and clock face down, the overlay layer emptied as at connect.</summary>
    public void EndVideo(IBulkUsbPipe pipe)
    {
        Command(pipe, UniversalScreen88Protocol.CommandStopPlay, ReadOnlySpan<byte>.Empty);
        Command(pipe, UniversalScreen88Protocol.CommandStopClock, stackalloc byte[] { 0 });
        Exchange(pipe, UniversalScreen88Protocol.CommandPushPng, UniversalScreen88Protocol.EncodeImage(
            UniversalScreen88Protocol.CommandPushPng, UniversalScreen88Protocol.EmptyOverlayPng(), NextTimestamp()));
    }

    /// <summary>
    /// Hands the glass back to its own boot screen, which only a reboot brings back; it would
    /// otherwise hold the last frame, stale readings and all. A handshake that never completed
    /// sends no reboot, so a glass still coming back from one is not sent round again.
    /// </summary>
    public void Disconnect(IBulkUsbPipe pipe, IHidDevice? hid)
    {
        if (_retired && !_sessionUp)
        {
            return;
        }
        pipe.Write(UniversalScreen88Protocol.EncodeCommand(
            UniversalScreen88Protocol.CommandStopPlay, ReadOnlySpan<byte>.Empty, NextTimestamp()));
        if (_sessionUp && OfflineModeSource?.Invoke() == true)
        {
            // As the Curve: a reboot would reset the glass clock the offline face shows, so the flag goes out instead.
            pipe.Write(UniversalScreen88Protocol.EncodeSetClock(DateTime.Now, NextTimestamp(), UniversalScreen88Protocol.ClockOfflineOn));
        }
        else if (_sessionUp)
        {
            pipe.Write(UniversalScreen88Protocol.EncodeCommand(
                UniversalScreen88Protocol.CommandReboot, ReadOnlySpan<byte>.Empty, NextTimestamp()));
        }
        _sessionUp = false;
        _jpeg?.Dispose();
        _jpeg = null;
    }

    private bool Command(IBulkUsbPipe pipe, byte command, ReadOnlySpan<byte> parameters) =>
        Exchange(pipe, command, UniversalScreen88Protocol.EncodeCommand(command, parameters, NextTimestamp())) is not null;

    /// <summary>Writes one command and returns its reply, or null when the write failed or nothing answered.</summary>
    private byte[]? Exchange(IBulkUsbPipe pipe, byte command, byte[] packet) =>
        pipe.Write(packet) ? ReadReply(pipe, command) : null;

    /// <summary>The reply to <paramref name="command"/>, skipping stale replies from commands that timed out.</summary>
    private byte[]? ReadReply(IBulkUsbPipe pipe, byte command)
    {
        var deadline = Environment.TickCount64 + ReplyTimeoutMs;
        while (true)
        {
            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0)
            {
                return null;
            }
            int read = pipe.Read(_reply, (int)remaining);
            if (read < 0)
            {
                return null;
            }
            if (read > 0 && _reply[0] == command)
            {
                return _reply.AsSpan(0, read).ToArray();
            }
        }
    }
}
