using System;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.JpegPanels;

/// <summary>
/// The HydroShift LCD 360R's three ARGB fans, driven per LED over the panel's own HID handle.
/// Every write goes through the hub's exchange, so it is serialized with frame chunks and the
/// session keepalive and cannot run before the attach (settle included) has finished.
/// Only the 360R carries lit fans: the 360S ships plain fans and the 360TL lights through its hub.
/// </summary>
public sealed class HydroShiftFanLeds
{
    public const int ProductId360R = 0x7399;

    /// <summary>Drains acks queued behind a write; short, because the hub lock is held and frames wait on it.</summary>
    private const int AckTimeoutMs = 2;

    private const int MaxAckReads = 4;

    private readonly JpegPanelHub _hub;

    // Touched under the hub lock (Write) and on the poller (Poll); the device reference is
    // what ties priming to one attach, so a re-attach primes again.
    private IHidDevice? _primedFor;
    private bool _connected;

    public HydroShiftFanLeds(JpegPanelHub hub)
    {
        _hub = hub;
    }

    public string DeviceId => _hub.Model.HandlerId;

    public bool IsConnected => _hub.IsConnected && _hub.AttachedProductId == ProductId360R;

    /// <summary>Raised from <see cref="Poll"/> when the 360R attaches or detaches.</summary>
    public event Action? ConnectionChanged;

    /// <summary>Edge-detects the connection for the lighting provider's device list.</summary>
    public void Poll()
    {
        var now = IsConnected;
        if (now == _connected)
        {
            return;
        }
        _connected = now;
        if (!now)
        {
            _hub.Exchange((_, _) => { _primedFor = null; return true; }, false);
        }
        ConnectionChanged?.Invoke();
    }

    /// <summary>
    /// Sends one colour per LED (RGB triples, fan order). False when no 360R is attached or a write
    /// fails; a failure drops the prime so the next write brings the LED path up again.
    /// </summary>
    public bool Write(ReadOnlySpan<byte> rgb)
    {
        // Spans cannot cross the exchange lambda, so the colours travel as a copy.
        var colors = rgb.ToArray();
        return _hub.Exchange((device, report) =>
        {
            if (device.ProductId != ProductId360R)
            {
                return false;
            }
            if (!ReferenceEquals(_primedFor, device))
            {
                for (int i = 0; i < LianLiAioProtocol.PrimePacketCount; i++)
                {
                    LianLiAioProtocol.FillFanLedPrime(report, i);
                    if (!device.Write(report))
                    {
                        return false;
                    }
                    Drain(device);
                }
                _primedFor = device;
                ServiceLog.Info($"[{DeviceId}] fan LED path primed");
            }
            LianLiAioProtocol.FillPerLed(report, colors);
            if (!device.Write(report))
            {
                _primedFor = null;
                return false;
            }
            Drain(device);
            return true;
        }, false);
    }

    // The frame path drains one ack per frame; a control write drains its own so the IN
    // endpoint cannot back up until the panel stops taking output reports.
    private static void Drain(IHidDevice device)
    {
        Span<byte> ack = stackalloc byte[64];
        for (int i = 0; i < MaxAckReads; i++)
        {
            if (device.Read(ack, AckTimeoutMs) <= 0)
            {
                return;
            }
        }
    }
}
