using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.JpegPanels;

/// <summary>Attaches a hub per TL LCD screen while Nexus Control is on, and detaches each screen whose HID interface leaves.</summary>
public sealed class TlLcdConnectionWorker : BackgroundService
{
    private const int PollMs = 3000;

    private readonly IHidEnumerator _hid;
    private readonly TlLcdPanels _panels;
    private readonly DeviceControlGate _gate;
    private readonly HardwarePresence _presence;

    /// <summary>Paths whose open or handshake failed, so a retry every poll does not repeat the log line.</summary>
    private readonly HashSet<string> _failureLogged = new(StringComparer.OrdinalIgnoreCase);

    public TlLcdConnectionWorker(IHidEnumerator hid, TlLcdPanels panels, DeviceControlGate gate, HardwarePresence presence)
    {
        _hid = hid;
        _panels = panels;
        _gate = gate;
        _presence = presence;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Sync();
                await Task.Delay(PollMs, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ServiceLog.Error($"[{JpegPanelModel.TlLcd.HandlerId}] worker error: {ex.Message}");
                await Task.Delay(PollMs, stoppingToken).ConfigureAwait(false);
            }
        }
        foreach (var screen in _panels.All)
        {
            DetachScreen(screen, "service stopping");
        }
    }

    internal void Sync()
    {
        var model = JpegPanelModel.TlLcd;
        if (!_gate.IsEnabled(model.HandlerId))
        {
            foreach (var screen in _panels.All)
            {
                DetachScreen(screen, "Nexus Control off");
            }
            return;
        }

        // A failed USB enumeration reads as an empty bus, so with screens attached the HID list decides.
        var present = new List<HidDeviceInfo>();
        if (_presence.UsbPresent(model.VendorId, model.ProductIds) || _panels.All.Count > 0)
        {
            foreach (var pid in model.ProductIds)
            {
                foreach (var info in _hid.Find(model.VendorId, pid))
                {
                    if (info.OutputReportByteLength >= model.ReportLength)
                    {
                        present.Add(info);
                    }
                }
            }
        }

        var presentPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var info in present)
        {
            presentPaths.Add(info.Path);
        }
        foreach (var screen in _panels.All)
        {
            if (!presentPaths.Contains(screen.Path))
            {
                DetachScreen(screen, "left the bus");
            }
            else if (screen.Handshake.HandleDead)
            {
                // Re-enumerated under the same path between polls: re-opened below.
                DetachScreen(screen, "handle lost");
            }
        }

        // Sorted so a path-hash key lands on the same screen across restarts (Windows paths follow the port).
        present.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        foreach (var info in present)
        {
            if (_panels.Contains(info.Path))
            {
                continue;
            }
            AttachScreen(info.Path);
        }
    }

    private void AttachScreen(string path)
    {
        var handlerId = JpegPanelModel.TlLcd.HandlerId;
        var device = _hid.Open(path, forInput: true);
        if (device == null)
        {
            if (_failureLogged.Add(path))
            {
                ServiceLog.Info($"[{handlerId}] could not open a screen's HID interface (is L-Connect running?): {path}");
            }
            return;
        }

        var handshake = new TlLcdHandshake();
        var hub = new JpegPanelHub(JpegPanelModel.TlLcd with { Handshake = handshake });
        if (!hub.Attach(device))
        {
            hub.Dispose();
            if (_failureLogged.Add(path))
            {
                ServiceLog.Warn($"[{handlerId}] screen refused the display setting; retrying each poll: {path}");
            }
            return;
        }
        _failureLogged.Remove(path);

        var identity = handshake.Identity;
        var screen = new TlLcdScreen(
            _panels.KeyFor(identity, path), TlLcdPanels.DisplayNameFor(identity), path, hub, handshake);
        _panels.Add(screen);
        ServiceLog.Info($"[{handlerId}] connected: {screen.DisplayName} key={screen.Key} "
            + $"port={identity.Port} index={identity.Index} serial={identity.Serial ?? "-"} fw={handshake.Firmware ?? "-"}");
    }

    private void DetachScreen(TlLcdScreen screen, string reason)
    {
        _panels.Remove(screen.Path);
        screen.Hub.Detach();
        screen.Hub.Dispose();
        ServiceLog.Info($"[{JpegPanelModel.TlLcd.HandlerId}] disconnected: {screen.DisplayName} ({reason})");
    }
}
