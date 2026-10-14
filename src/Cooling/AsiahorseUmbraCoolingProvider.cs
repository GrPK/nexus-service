using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Peripherals.AsiahorseUmbra;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Cooling;

/// <summary>One PWM output for every fan header, published only while fans do not follow the motherboard PWM header.</summary>
public sealed class AsiahorseUmbraCoolingProvider : IFanControlProvider, ICoolingProvider
{
    public const string FanChannelId = "asiahorse-umbra:fan";
    private const string DeviceId = "asiahorse-umbra";
    private const string DeviceName = "Asiahorse Umbra V2";

    private readonly UmbraHub _hub;
    private readonly IConfigStore _store;

    private readonly object _ctrlLock = new();
    private bool _fanSw;
    // Survives a reconnect, which resets the hub snapshot.
    private int _fanDuty = -1;

    public AsiahorseUmbraCoolingProvider(UmbraHub hub, IConfigStore store)
    {
        _hub = hub;
        _store = store;
    }

    public static bool IsUmbraId(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith(DeviceId + ":", StringComparison.Ordinal);

    private bool Publishes() => _hub.IsConnected && !_store.Load().Devices.AsiahorseUmbra.FanSyncMb;

    // IFanControlProvider

    public IReadOnlyList<FanChannel> GetFanChannels()
    {
        if (!Publishes())
        {
            return Array.Empty<FanChannel>();
        }
        bool sw;
        int duty;
        lock (_ctrlLock)
        {
            sw = _fanSw;
            duty = Math.Max(_fanDuty, 0);
        }
        return new[]
        {
            new FanChannel
            {
                Id = FanChannelId,
                Name = "Umbra fans",
                DutyPercent = sw ? duty : 0,
                Mode = sw ? FanModes.Manual : FanModes.Auto,
                Kind = FanKinds.Fan,
                RpmUnavailable = true,
                DeviceId = DeviceId,
                DeviceName = DeviceName,
                PortLabel = "Fan",
            },
        };
    }

    public IReadOnlyList<TemperatureSource> GetTemperatureSources() => Array.Empty<TemperatureSource>();

    public float? ReadTemperature(string sensorId) => null;

    public int SetFanSpeed(string channelId, int dutyPercent)
    {
        if (channelId != FanChannelId)
        {
            return dutyPercent;
        }
        var clamped = Math.Clamp(dutyPercent, 0, 100);
        ApplyFanWrite(clamped);
        return clamped;
    }

    public void DriveFanSpeed(string channelId, int dutyPercent)
    {
        if (channelId == FanChannelId)
        {
            ApplyFanWrite(Math.Clamp(dutyPercent, 0, 100));
        }
    }

    public void ReleaseFan(string channelId)
    {
        if (channelId != FanChannelId)
        {
            return;
        }
        lock (_ctrlLock)
        {
            _fanSw = false;
        }
    }

    public void ReleaseAll()
    {
        lock (_ctrlLock)
        {
            _fanSw = false;
        }
    }

    public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
        IReadOnlyList<string> fanIds,
        IProgress<FanCalibrationProgress> progress,
        CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());
    }

    // ICoolingProvider

    public IReadOnlyList<CoolingComponent> GetAll()
    {
        if (!Publishes())
        {
            return Array.Empty<CoolingComponent>();
        }
        return new[]
        {
            new CoolingComponent
            {
                Id = DeviceId,
                Name = DeviceName,
                Type = "PwmFanAndArgbHub",
                Devices = new List<CoolingDevice>
                {
                    new CoolingDevice
                    {
                        Id = FanChannelId,
                        Name = "Umbra fans",
                        Type = "Fan",
                        Pwm = CurrentDuty(),
                    },
                },
            },
        };
    }

    public int Duty => CurrentDuty();

    private int CurrentDuty()
    {
        lock (_ctrlLock)
        {
            return Math.Max(_fanDuty, 0);
        }
    }

    /// <summary>Writes the persisted fan mode: follow the motherboard PWM header, or run on the controller PWM.</summary>
    public bool ApplyFanSync()
    {
        if (!_hub.IsConnected)
        {
            return false;
        }
        var sync = _store.Load().Devices.AsiahorseUmbra.FanSyncMb;
        if (sync)
        {
            ReleaseAll();
        }
        return _hub.SetFanSync(sync);
    }

    /// <summary>Re-sends the fan mode and, under software control, the last duty; the worker calls it on connect.</summary>
    public void OnConnected()
    {
        if (!ApplyFanSync())
        {
            ServiceLog.Warn("[asiahorse-umbra-cooling] fan mode write failed on connect");
        }
        ReassertControl();
    }

    public void ReassertControl()
    {
        if (!Publishes())
        {
            return;
        }
        bool sw;
        int duty;
        lock (_ctrlLock)
        {
            sw = _fanSw;
            duty = _fanDuty;
        }
        if (sw && duty >= 0 && !_hub.SetFanDuty(duty))
        {
            ServiceLog.Warn($"[asiahorse-umbra-cooling] ReassertControl duty {duty} returned false");
        }
    }

    private void ApplyFanWrite(int duty)
    {
        if (!Publishes())
        {
            ServiceLog.Warn("[asiahorse-umbra-cooling] fan write dropped: no fan channel");
            return;
        }
        lock (_ctrlLock)
        {
            _fanSw = true;
            _fanDuty = duty;
        }
        if (!_hub.SetFanDuty(duty))
        {
            ServiceLog.Warn($"[asiahorse-umbra-cooling] SetFanDuty {duty} returned false");
        }
    }
}
