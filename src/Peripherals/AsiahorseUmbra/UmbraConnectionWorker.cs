using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Cooling;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.AsiahorseUmbra;

public sealed class UmbraConnectionWorker : BackgroundService
{
    public const string HandlerId = "asiahorse-umbra";

    private const int ConnectPollMs = 5000;
    private const int InfoPollMs = 2000;
    // While a restart is pending the device drops within a fraction of a second; poll fast to notice.
    private const int RestartPollMs = 250;
    private const int MaxConsecutiveFailures = 3;

    private readonly IHidEnumerator _hid;
    private readonly UmbraHub _hub;
    private readonly AsiahorseUmbraLightingDeviceProvider _lighting;
    private readonly AsiahorseUmbraCoolingProvider _cooling;
    private readonly DeviceControlGate _gate;
    private readonly HardwarePresence _presence;

    public UmbraConnectionWorker(IHidEnumerator hid, UmbraHub hub, AsiahorseUmbraLightingDeviceProvider lighting, AsiahorseUmbraCoolingProvider cooling, DeviceControlGate gate, HardwarePresence presence)
    {
        _hid = hid;
        _hub = hub;
        _lighting = lighting;
        _cooling = cooling;
        _gate = gate;
        _presence = presence;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_gate.IsEnabled(HandlerId) || !_presence.UsbPresent(UmbraProtocol.VendorId, UmbraProtocol.ProductId))
                {
                    await Task.Delay(_hub.IsRestarting ? RestartPollMs : ConnectPollMs, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var device = FindAndOpen();
                if (device is null)
                {
                    await Task.Delay(_hub.IsRestarting ? RestartPollMs : ConnectPollMs, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                _hub.Attach(device);
                var started = false;
                try
                {
                    if (_hub.Connect())
                    {
                        started = true;
                        ServiceLog.Info("[asiahorse-umbra] connected");
                        _cooling.OnConnected();
                        _lighting.OnHubStateUpdated();
                        await PollAsync(stoppingToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _hub.Detach();
                    if (started)
                    {
                        ServiceLog.Info("[asiahorse-umbra] disconnected");
                    }
                    _lighting.OnHubStateUpdated();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ServiceLog.Error($"[asiahorse-umbra] worker error: {ex.Message}");
                await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested && _gate.IsEnabled(HandlerId))
        {
            if (_hub.PollInfo())
            {
                failures = 0;
                _cooling.ReassertControl();
            }
            else
            {
                failures++;
                if (failures >= MaxConsecutiveFailures || _hub.IsRestarting)
                {
                    return;
                }
            }
            await Task.Delay(_hub.IsRestarting ? RestartPollMs : InfoPollMs, ct).ConfigureAwait(false);
        }
    }

    private IHidDevice? FindAndOpen()
    {
        HidDeviceInfo? best = null;
        foreach (var info in _hid.Find(UmbraProtocol.VendorId, UmbraProtocol.ProductId))
        {
            if (info.OutputReportByteLength < UmbraProtocol.FrameLength || info.InputReportByteLength <= 0)
            {
                continue;
            }
            if (best is null || info.InputReportByteLength > best.InputReportByteLength)
            {
                best = info;
            }
        }
        // forInput=true for overlapped I/O so Read honors its timeout on Windows.
        return best is null ? null : _hid.Open(best.Path, forInput: true);
    }
}
