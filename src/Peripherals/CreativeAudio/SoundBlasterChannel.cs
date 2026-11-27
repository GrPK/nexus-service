using System;
using System.Linq;
using Nexus.Service.Peripherals.Hid;

namespace Nexus.Service.Peripherals.CreativeAudio;

/// <summary>A Sound Blaster model Nexus knows the control interface of.</summary>
public sealed record SoundBlasterModel(int ProductId, string Name);

/// <summary>One request and its answer on a Sound Blaster's control interface.</summary>
public interface ISoundBlasterChannel
{
    /// <summary>The connected model, or null when none is connected.</summary>
    SoundBlasterModel? Present();

    /// <summary>
    /// Sends <paramref name="request"/> and returns the first report <paramref name="accept"/> takes,
    /// or null after the timeout. Every report read on the way, the answer included, goes to
    /// <paramref name="seen"/> (the device pushes parameter values and profile bits after a change).
    /// </summary>
    byte[]? Exchange(byte[] request, Func<byte[], bool> accept, Action<byte[]>? seen = null);
}

/// <summary>
/// The control interface of a connected Sound Blaster: the vendor-defined HID collection (interface 4,
/// 64-byte reports with report id 0) of a G8 or G6. Opened once and kept; reopened after a failure.
/// Creative App can keep its own handle at the same time: each handle gets its own copy of the input reports.
/// </summary>
public sealed class SoundBlasterChannel : ISoundBlasterChannel, IDisposable
{
    public static readonly SoundBlasterModel[] Models =
    {
        new(0x329B, "Sound Blaster G8"),
        new(0x3256, "Sound Blaster X G6"),
    };

    private const int ReplyTimeoutMs = 400;
    private readonly IHidEnumerator _hid;
    private readonly object _lock = new();
    private string? _path;
    private IHidDevice? _device;

    public SoundBlasterChannel(IHidEnumerator hid)
    {
        _hid = hid;
    }

    public SoundBlasterModel? Present()
    {
        lock (_lock)
        {
            var (model, info) = Locate();
            if (info?.Path != _path)
            {
                Close();
                _path = info?.Path;
            }
            return model;
        }
    }

    public byte[]? Exchange(byte[] request, Func<byte[], bool> accept, Action<byte[]>? seen = null)
    {
        lock (_lock)
        {
            if (_path is null && (_path = Locate().Info?.Path) is null)
            {
                return null;
            }
            _device ??= _hid.OpenQuiet(_path, forInput: true);
            if (_device is null)
            {
                return null;
            }
            var buffer = new byte[SoundBlasterProtocol.ReportLength];
            for (var i = 0; i < 64 && _device.Read(buffer, 0) > 0; i++)
            {
                seen?.Invoke(buffer.ToArray());
            }
            if (!_device.Write(request))
            {
                Close();
                return null;
            }
            var deadline = Environment.TickCount64 + ReplyTimeoutMs;
            while (true)
            {
                var left = (int)(deadline - Environment.TickCount64);
                if (left <= 0)
                {
                    return null;
                }
                var read = _device.Read(buffer, left);
                if (read < 0)
                {
                    Close();
                    return null;
                }
                if (read == 0)
                {
                    continue;
                }
                var report = buffer.AsSpan(0, read).ToArray();
                seen?.Invoke(report);
                if (accept(report))
                {
                    return report;
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            Close();
        }
    }

    private (SoundBlasterModel? Model, HidDeviceInfo? Info) Locate()
    {
        foreach (var model in Models)
        {
            var info = _hid.Find(SoundBlasterProtocol.VendorId, model.ProductId)
                .FirstOrDefault(i => i.UsagePage >= 0xFF00
                    && (i.OutputReportByteLength == 0 || i.OutputReportByteLength >= SoundBlasterProtocol.ReportLength));
            if (info is not null)
            {
                return (model, info);
            }
        }
        return (null, null);
    }

    private void Close()
    {
        _device?.Dispose();
        _device = null;
    }
}
