using System;

namespace Nexus.Service.Lighting.Capture;

/// <summary>
/// One screen capture shared by every consumer: the global Mirror effect and the
/// mirror-pin effect each hold a handle, the first started handle opens the
/// underlying source and the last stopped one closes it. Without this, one
/// consumer's Stop (a mode switch away from Mirror) would end the capture the
/// other still reads.
/// </summary>
public sealed class SharedScreenFrameSource
{
    private readonly IScreenFrameSource _inner;
    private readonly object _gate = new();
    private int _refs;
    private string _monitor = "";
    private int _w, _h;

    public SharedScreenFrameSource(IScreenFrameSource inner) => _inner = inner;

    /// <summary>Number of started handles; zero means the capture is closed.</summary>
    public int ActiveHandles { get { lock (_gate) return _refs; } }

    public IScreenFrameSource CreateHandle() => new Handle(this);

    private void StartShared(string monitorId, int width, int height)
    {
        lock (_gate)
        {
            if (_refs == 0)
            {
                _inner.Start(monitorId, width, height);
            }
            else if (!string.Equals(monitorId, _monitor, StringComparison.Ordinal) || width != _w || height != _h)
            {
                // The newest consumer asked for a different screen: follow it.
                _inner.Stop();
                _inner.Start(monitorId, width, height);
            }
            _refs++;
            _monitor = monitorId;
            _w = width;
            _h = height;
        }
    }

    private void StopShared()
    {
        lock (_gate)
        {
            if (_refs == 0) return;
            if (--_refs == 0)
            {
                _inner.Stop();
            }
        }
    }

    private sealed class Handle : IScreenFrameSource
    {
        private readonly SharedScreenFrameSource _owner;
        private bool _started;

        public Handle(SharedScreenFrameSource owner) => _owner = owner;

        public void Start(string monitorId, int width, int height)
        {
            if (_started) return;
            _owner.StartShared(monitorId, width, height);
            _started = true;
        }

        public void Stop()
        {
            if (!_started) return;
            _started = false;
            _owner.StopShared();
        }

        public byte[]? TryAcquireFrame(out int width, out int height) => _owner._inner.TryAcquireFrame(out width, out height);

        public void Reselect() => _owner._inner.Reselect();
    }
}
