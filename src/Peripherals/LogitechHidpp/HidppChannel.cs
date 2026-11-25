using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.LogitechAudio;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.LogitechHidpp;

/// <summary>One HID++ 2.0 conversation: a feature function call and its reply parameters.</summary>
public interface IHidppChannel
{
    /// <summary>Whether the device's HID++ collection is present; bumps <see cref="Generation"/> on each reconnect.</summary>
    bool IsPresent();

    /// <summary>Moves on every time the device comes back, so callers can drop what they cached.</summary>
    int Generation { get; }

    /// <summary>The feature's index on this device, or null when the device does not have it.</summary>
    byte? FeatureIndex(ushort featureId);

    /// <summary>Calls <paramref name="function"/> of the feature at <paramref name="featureIndex"/>; the 16 reply parameter bytes, or null.</summary>
    byte[]? Call(byte featureIndex, byte function, ReadOnlySpan<byte> parameters);
}

/// <summary>
/// HID++ 2.0 over a wired Logitech device's long-report collection, the way the G560's
/// subwoofer channel talks: our own software id (OpenRGB uses another), stale reports drained
/// before each request, replies matched by feature index, function and software id. OpenRGB
/// keeps the same collection open for the lighting; input reports reach every open handle.
/// </summary>
public sealed class HidppChannel : IHidppChannel, IDisposable
{
    private const int ReplyTimeoutMs = 800;

    private readonly IHidEnumerator _hid;
    private readonly int _vendorId;
    private readonly int _productId;
    private readonly int _usagePage;
    private readonly int _usage;
    private readonly string _tag;
    private readonly object _lock = new();
    private readonly Dictionary<ushort, byte> _features = new();
    private string? _path;
    private IHidDevice? _device;
    private bool _wasPresent;

    public HidppChannel(IHidEnumerator hid, int vendorId, int productId, int usagePage, int usage, string tag)
    {
        _hid = hid;
        _vendorId = vendorId;
        _productId = productId;
        _usagePage = usagePage;
        _usage = usage;
        _tag = tag;
    }

    public int Generation { get; private set; }

    /// <summary>Serial number of the collection, as Windows reports it.</summary>
    public string? Serial { get; private set; }

    public bool IsPresent()
    {
        lock (_lock)
        {
            var info = Locate();
            if (info?.Path != _path)
            {
                Close();
                _path = info?.Path;
                _features.Clear();
            }
            Serial = info?.Serial;
            var present = info is not null;
            if (present && !_wasPresent)
            {
                Generation++;
            }
            _wasPresent = present;
            return present;
        }
    }

    public byte? FeatureIndex(ushort featureId)
    {
        lock (_lock)
        {
            if (_features.TryGetValue(featureId, out var cached))
            {
                return cached;
            }
            var reply = Transact(Hidpp20.GetFeatureRequest(featureId));
            if (reply is null)
            {
                return null;
            }
            var index = reply[4];
            if (index == 0)
            {
                return null;
            }
            _features[featureId] = index;
            return index;
        }
    }

    public byte[]? Call(byte featureIndex, byte function, ReadOnlySpan<byte> parameters)
    {
        lock (_lock)
        {
            var reply = Transact(Hidpp20.Request(featureIndex, function, parameters));
            return reply?.AsSpan(4, Hidpp20.LongReportLength - 4).ToArray();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            Close();
        }
    }

    private HidDeviceInfo? Locate() =>
        _hid.Find(_vendorId, _productId)
            .FirstOrDefault(i => i.UsagePage == _usagePage && i.Usage == _usage
                && (i.OutputReportByteLength == 0 || i.OutputReportByteLength >= Hidpp20.LongReportLength));

    // Caller holds _lock.
    private byte[]? Transact(byte[] request)
    {
        if (_path is null && (_path = Locate()?.Path) is null)
        {
            return null;
        }
        _device ??= _hid.OpenQuiet(_path, forInput: true);
        if (_device is null)
        {
            return null;
        }
        var buffer = new byte[64];
        for (var i = 0; i < 64 && _device.Read(buffer, 0) > 0; i++)
        {
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
                ServiceLog.Warn($"[{_tag}] HID++ feature index 0x{request[2]:X2} function {request[3] >> 4}: no reply");
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
            var report = buffer.AsSpan(0, read);
            switch (Hidpp20.Classify(request, report))
            {
                case Hidpp20.ReplyKind.Answer:
                    var answer = new byte[Hidpp20.LongReportLength];
                    report[..Math.Min(read, answer.Length)].CopyTo(answer);
                    return answer;
                case Hidpp20.ReplyKind.Error:
                    ServiceLog.Warn($"[{_tag}] HID++ feature index 0x{request[2]:X2} function {request[3] >> 4}: error {report[5]}");
                    return null;
            }
        }
    }

    private void Close()
    {
        _device?.Dispose();
        _device = null;
    }
}
