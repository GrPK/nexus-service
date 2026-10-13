using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.AsiahorseUmbra;

public enum UmbraRestartResult
{
    Reconnected,
    Busy,
    NoDevice,
    TimedOut,
}

public sealed class UmbraHub : IDisposable
{
    private const int ConnectReadTimeoutMs = 1000;
    private const int RequestReadTimeoutMs = 300;
    // A request skips late replies to earlier timed-out requests, up to this many reads.
    private const int MaxReplyReads = 8;

    private enum ReplyStatus
    {
        Ok,
        Timeout,
        Gone,
    }

    private readonly object _lock = new();
    private IHidDevice? _device;
    private bool _disposed;
    private TaskCompletionSource<bool>? _reconnect;
    private int _rejectedModel = -1;

    private volatile bool _isConnected;
    private volatile bool _isRestarting;
    private volatile UmbraSnapshot _snapshot = UmbraSnapshot.Empty;
    private long _connectEpoch;

    public bool IsConnected => _isConnected;

    // True from the restart write until the device re-enumerates and reconnects.
    public bool IsRestarting => _isRestarting;

    // Increments on every successful connect so writers re-assert state after a reconnect.
    public long ConnectEpoch => Interlocked.Read(ref _connectEpoch);

    public UmbraSnapshot Snapshot => _snapshot;

    public void Attach(IHidDevice device)
    {
        lock (_lock)
        {
            _device?.Dispose();
            _device = device;
        }
    }

    // Reads the device info and the port config; called from the worker thread after Attach.
    public bool Connect()
    {
        lock (_lock)
        {
            if (_device is null)
            {
                return false;
            }
            if (Request(UmbraProtocol.EncodeInfoRequest(), UmbraProtocol.CmdInfo, ConnectReadTimeoutMs, out var infoReply) != ReplyStatus.Ok)
            {
                return false;
            }
            var info = UmbraProtocol.DecodeInfo(infoReply);
            if (info is null)
            {
                return false;
            }
            if (info.ModelCode != UmbraProtocol.ModelCode)
            {
                if (_rejectedModel != info.ModelCode)
                {
                    _rejectedModel = info.ModelCode;
                    ServiceLog.Warn($"[asiahorse-umbra] unsupported model code {info.ModelCode}, not connecting");
                }
                return false;
            }
            if (Request(UmbraProtocol.EncodePortConfigRequest(), UmbraProtocol.CmdReadPorts, ConnectReadTimeoutMs, out var portReply) != ReplyStatus.Ok)
            {
                return false;
            }
            var config = UmbraProtocol.DecodePortConfig(portReply);
            if (config is null)
            {
                return false;
            }
            _rejectedModel = -1;
            _snapshot = new UmbraSnapshot(info.DeviceId, info.Firmware, info.BootMemory, info.SelfCheck, config.Ports, config.BrightnessCap, -1);
            _isConnected = true;
            _isRestarting = false;
            Interlocked.Increment(ref _connectEpoch);
            var waiter = _reconnect;
            _reconnect = null;
            waiter?.TrySetResult(true);
            return true;
        }
    }

    // False only when the device appears gone; a read timeout is not a disconnect.
    public bool PollInfo()
    {
        lock (_lock)
        {
            if (_device is null)
            {
                return false;
            }
            var status = Request(UmbraProtocol.EncodeInfoRequest(), UmbraProtocol.CmdInfo, RequestReadTimeoutMs, out var reply);
            if (status == ReplyStatus.Gone)
            {
                return false;
            }
            if (status == ReplyStatus.Ok)
            {
                var info = UmbraProtocol.DecodeInfo(reply);
                if (info is not null)
                {
                    _snapshot = _snapshot.With(bootMemory: info.BootMemory, selfCheck: info.SelfCheck);
                }
            }
            return true;
        }
    }

    public bool SendFrame(byte[] frame)
    {
        lock (_lock)
        {
            return _device is not null && _device.Write(UmbraProtocol.ToReport(frame));
        }
    }

    // Holds the lock across the whole set so a command cannot land between two packets of one frame.
    public bool SendFrames(params byte[][] frames)
    {
        lock (_lock)
        {
            if (_device is null)
            {
                return false;
            }
            foreach (var frame in frames)
            {
                if (!_device.Write(UmbraProtocol.ToReport(frame)))
                {
                    return false;
                }
            }
            return true;
        }
    }

    public bool SetFanSync(bool followMotherboard) => SendFrame(UmbraProtocol.EncodeFanSync(followMotherboard));

    public bool SetFanDuty(int percent)
    {
        var duty = Math.Clamp(percent, 0, 100);
        lock (_lock)
        {
            if (_device is null || !_device.Write(UmbraProtocol.ToReport(UmbraProtocol.EncodeFanDuty(duty))))
            {
                return false;
            }
            _snapshot = _snapshot.With(fanDuty: duty);
            return true;
        }
    }

    // Writes one port setting, then re-reads the config and publishes the device's values.
    internal bool WritePort(int port, UmbraPortField field, int value)
    {
        if (port < 0 || port >= UmbraProtocol.PortCount)
        {
            return false;
        }
        lock (_lock)
        {
            if (_device is null || !_device.Write(UmbraProtocol.ToReport(UmbraProtocol.EncodeSetPort(port, field, value))))
            {
                return false;
            }
            return RefreshPortsLocked();
        }
    }

    public bool RefreshPorts()
    {
        lock (_lock)
        {
            return _device is not null && RefreshPortsLocked();
        }
    }

    private bool RefreshPortsLocked()
    {
        if (Request(UmbraProtocol.EncodePortConfigRequest(), UmbraProtocol.CmdReadPorts, RequestReadTimeoutMs, out var reply) != ReplyStatus.Ok)
        {
            return false;
        }
        var config = UmbraProtocol.DecodePortConfig(reply);
        if (config is null)
        {
            return false;
        }
        _snapshot = _snapshot.With(ports: config.Ports);
        return true;
    }

    public bool SetBootMemory() => WriteThenRefreshInfo(UmbraProtocol.EncodeBootMemory());

    public bool SetBootFixed() => WriteThenRefreshInfo(UmbraProtocol.EncodeBootFixed());

    public bool SetSelfCheck(bool on) => WriteThenRefreshInfo(UmbraProtocol.EncodeSelfCheck(on));

    private bool WriteThenRefreshInfo(params byte[][] frames)
    {
        lock (_lock)
        {
            if (_device is null)
            {
                return false;
            }
            foreach (var frame in frames)
            {
                if (!_device.Write(UmbraProtocol.ToReport(frame)))
                {
                    return false;
                }
            }
            return PollInfo();
        }
    }

    // Sends the restart (the vendor "Auto Detect LEDs") and completes when the re-enumerated device reconnects.
    public async Task<UmbraRestartResult> RestartAsync(TimeSpan timeout, CancellationToken ct)
    {
        var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            if (_isRestarting)
            {
                return UmbraRestartResult.Busy;
            }
            if (_device is null || !_device.Write(UmbraProtocol.ToReport(UmbraProtocol.EncodeRestart())))
            {
                return UmbraRestartResult.NoDevice;
            }
            _reconnect = waiter;
            _isRestarting = true;
        }
        // The restarting state outlives a cancelled caller: it ends on reconnect or when the timeout expires.
        var expiry = waiter.Task.WaitAsync(timeout);
        _ = expiry.ContinueWith(t =>
        {
            if (!t.IsFaulted)
            {
                return;
            }
            lock (_lock)
            {
                if (ReferenceEquals(_reconnect, waiter))
                {
                    _reconnect = null;
                    _isRestarting = false;
                }
            }
        }, TaskScheduler.Default);
        try
        {
            await expiry.WaitAsync(ct).ConfigureAwait(false);
            return UmbraRestartResult.Reconnected;
        }
        catch (TimeoutException)
        {
            return UmbraRestartResult.TimedOut;
        }
    }

    // The controller holds the last streamed frame forever, so a release first replays a firmware effect.
    // Written and closed under one lock so no stream packet can land after the hand-back.
    public void ReleaseWithHandBack(byte[][] frames)
    {
        lock (_lock)
        {
            if (_device is not null)
            {
                foreach (var frame in frames)
                {
                    if (!_device.Write(UmbraProtocol.ToReport(frame)))
                    {
                        break;
                    }
                }
            }
            _isConnected = false;
            _snapshot = UmbraSnapshot.Empty;
            _device?.Dispose();
            _device = null;
        }
    }

    public void Detach()
    {
        lock (_lock)
        {
            _isConnected = false;
            _snapshot = UmbraSnapshot.Empty;
            _device?.Dispose();
            _device = null;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _isConnected = false;
            _snapshot = UmbraSnapshot.Empty;
            _device?.Dispose();
            _device = null;
        }
    }

    private ReplyStatus Request(byte[] frame, byte cmd, int timeoutMs, out byte[] reply)
    {
        reply = Array.Empty<byte>();
        if (!_device!.Write(UmbraProtocol.ToReport(frame)))
        {
            return ReplyStatus.Gone;
        }
        var buf = new byte[UmbraProtocol.FrameLength + 1];
        for (var i = 0; i < MaxReplyReads; i++)
        {
            var n = _device.Read(buf, timeoutMs);
            if (n < 0)
            {
                return ReplyStatus.Gone;
            }
            if (n == 0)
            {
                return ReplyStatus.Timeout;
            }
            var normalized = UmbraProtocol.Normalize(buf.AsSpan(0, n));
            if (UmbraProtocol.IsReplyTo(normalized, cmd))
            {
                reply = normalized.ToArray();
                return ReplyStatus.Ok;
            }
        }
        return ReplyStatus.Timeout;
    }
}
