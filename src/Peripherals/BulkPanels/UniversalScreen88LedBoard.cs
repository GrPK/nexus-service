using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>
/// Drives the Universal Screen 8.8's bezel LEDs (0416:8050) over WinUSB, under the glass's Nexus
/// Control toggle. Writes run on this loop only, so a slow write delays this ring and nothing else;
/// before, the bundled OpenRGB drove it and a stalled write held up every OpenRGB device.
/// </summary>
public sealed class UniversalScreen88LedBoard : BackgroundService
{
    private const int TickMs = 33;
    private const int ConnectRetryMs = 5000;
    private const int GateCheckMs = 2000;
    private const int ReplyTimeoutMs = 250;

    private static readonly int[] ProductIds = { UniversalScreen88LedProtocol.ProductId };

    private readonly IBulkUsbPipeFactory _pipes;
    private readonly DeviceControlGate _gate;
    private readonly HardwarePresence _presence;
    private readonly byte[] _reply = new byte[1024];
    private readonly object _lock = new();
    /// <summary>Released when a new frame is set, so it goes out at the effect's pace rather than the next tick.</summary>
    private readonly SemaphoreSlim _ledsWake = new(0, 1);

    private IBulkUsbPipe? _pipe;
    private volatile bool _connected;
    private byte[]? _leds;
    private bool _ledsDirty;

    public UniversalScreen88LedBoard(IBulkUsbPipeFactory pipes, DeviceControlGate gate, HardwarePresence presence)
    {
        _pipes = pipes;
        _gate = gate;
        _presence = presence;
    }

    public bool IsAvailable => _connected;

    /// <summary>Raised from the loop when <see cref="IsAvailable"/> flips.</summary>
    public event Action? AvailabilityChanged;

    /// <summary>Firmware "major_minor" read at connect, or null.</summary>
    public string? Firmware { get; private set; }

    /// <summary>Sets the bezel to one frame of packed RGB; the board holds it until the next.</summary>
    public void SetLeds(ReadOnlySpan<byte> rgb)
    {
        lock (_lock)
        {
            if (_leds is not null && rgb.SequenceEqual(_leds)) return;
            _leds = rgb.ToArray();
            _ledsDirty = true;
        }
        try
        {
            if (_ledsWake.CurrentCount == 0) _ledsWake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Another frame already woke the loop; it sends the newest one.
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            long lastGateAt = 0;
            while (!stoppingToken.IsCancellationRequested)
            {
                var now = Environment.TickCount64;
                if (!_connected || now - lastGateAt >= GateCheckMs)
                {
                    lastGateAt = now;
                    if (!_gate.IsEnabled(UniversalScreen88Driver.Id)
                        || !_presence.UsbPresent(UniversalScreen88LedProtocol.VendorId, ProductIds))
                    {
                        Disconnect();
                        await Task.Delay(ConnectRetryMs, stoppingToken).ConfigureAwait(false);
                        continue;
                    }
                }
                // OpenRGB may still hold the board until its detector is turned off on its next start.
                if (!_connected && !TryConnect())
                {
                    await Task.Delay(ConnectRetryMs, stoppingToken).ConfigureAwait(false);
                    continue;
                }
                try
                {
                    SendLedsIfDirty();
                }
                catch (Exception ex)
                {
                    ServiceLog.Warn($"[{UniversalScreen88Driver.Id}] LED board tick failed: {ex.Message}");
                }
                await _ledsWake.WaitAsync(TickMs, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Disconnect();
        }
    }

    internal bool TryConnect()
    {
        var pipe = _pipes.Open(UniversalScreen88LedProtocol.VendorId, UniversalScreen88LedProtocol.ProductId,
            UniversalScreen88LedProtocol.WritePipe, UniversalScreen88LedProtocol.ReadPipe);
        if (pipe is null)
        {
            return false;
        }
        _pipe = pipe;
        // Stale replies a previous owner left would answer the version query.
        for (int i = 0; i < 8 && pipe.Read(_reply, 1) > 0; i++) { }
        if (!pipe.Write(UniversalScreen88LedProtocol.EncodeVersionQuery()))
        {
            ServiceLog.Warn($"[{UniversalScreen88Driver.Id}] LED board refused its version query");
            pipe.Dispose();
            _pipe = null;
            return false;
        }
        var got = pipe.Read(_reply, ReplyTimeoutMs);
        Firmware = got > 0 ? UniversalScreen88LedProtocol.DecodeVersion(_reply.AsSpan(0, got)) : null;
        lock (_lock)
        {
            _ledsDirty = _leds is not null;
        }
        _connected = true;
        ServiceLog.Info($"[{UniversalScreen88Driver.Id}] LED board connected, firmware {Firmware ?? "unknown"}");
        RaiseAvailabilityChanged();
        return true;
    }

    internal void SendLedsIfDirty()
    {
        byte[] leds;
        lock (_lock)
        {
            if (!_ledsDirty || _leds is null) return;
            leds = _leds;
            _ledsDirty = false;
        }
        var pipe = _pipe;
        if (pipe is null) return;
        // L-Connect writes the three chunks and reads nothing back.
        foreach (var chunk in UniversalScreen88LedProtocol.EncodeLedFrame(leds))
        {
            if (!pipe.Write(chunk))
            {
                lock (_lock)
                {
                    _ledsDirty |= ReferenceEquals(_leds, leds);
                }
                Disconnect();
                return;
            }
        }
    }

    private void Disconnect()
    {
        if (_pipe is null)
        {
            return;
        }
        _pipe.Dispose();
        _pipe = null;
        if (_connected)
        {
            _connected = false;
            ServiceLog.Info($"[{UniversalScreen88Driver.Id}] LED board disconnected");
            RaiseAvailabilityChanged();
        }
    }

    private void RaiseAvailabilityChanged()
    {
        try
        {
            AvailabilityChanged?.Invoke();
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[{UniversalScreen88Driver.Id}] LED availability listener failed: {ex.Message}");
        }
    }
}
