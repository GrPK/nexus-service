using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Effects;
using Nexus.Service.Sockets;

namespace Nexus.Service.Lighting.Capture;

/// <summary>
/// Streams the selected monitor's capture to preview clients while at least one is
/// connected, in the same frame layout as the lighting output stream (zero devices),
/// so the page paints it exactly like a global Mirror frame. The capture is taken
/// through a shared-source handle, so it is never doubled when Mirror is global or
/// pinned, and it is released with the last client. Flip and the color post-process
/// apply; the reactive glow does not.
/// </summary>
public sealed class ScreenPreviewStreamer : IDisposable
{
    public const int CanvasWidth = 160;
    public const int CanvasHeight = 90;
    public const int FrameIntervalMs = 100;

    private readonly WebSocketHub _hub;
    private readonly Func<IScreenFrameSource?> _openHandle;
    private readonly Func<string> _monitor;
    private readonly PostProcessState _postProcess;
    private readonly object _gate = new();
    private readonly CanvasBuffer _canvas = new(CanvasWidth, CanvasHeight);
    private readonly byte[] _frame = new byte[1 + 4 + CanvasWidth * CanvasHeight * 3 + 1];
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private IScreenFrameSource? _handle;
    private string _handleMonitor = "";

    public ScreenPreviewStreamer(WebSocketHub hub, Func<IScreenFrameSource?> openHandle, Func<string> monitor, PostProcessState postProcess)
    {
        _hub = hub;
        _openHandle = openHandle;
        _monitor = monitor;
        _postProcess = postProcess;
        _hub.OnFirstClient += Start;
        _hub.OnAllClientsGone += Stop;
    }

    /// <summary>True while a capture handle is held.</summary>
    public bool Capturing { get { lock (_gate) return _handle is not null; } }

    internal void Start()
    {
        lock (_gate)
        {
            if (_cts is not null) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _loop = Task.Run(() => RunAsync(token));
        }
    }

    internal void Stop()
    {
        Task? loop;
        lock (_gate)
        {
            _cts?.Cancel();
            _cts = null;
            loop = _loop;
            _loop = null;
        }
        // The loop releases the handle on exit; waiting keeps Stop then Start from overlapping two loops.
        try { loop?.Wait(1000); } catch { /* cancelled */ }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(FrameIntervalMs));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                Tick();
            }
        }
        catch (OperationCanceledException) { }
        finally { ReleaseHandle(); }
    }

    internal void Tick()
    {
        var monitor = _monitor();
        IScreenFrameSource? handle;
        lock (_gate)
        {
            if (_cts is null) return;
            if (_handle is not null && !string.Equals(monitor, _handleMonitor, StringComparison.Ordinal))
            {
                _handle.Stop();
                _handle = null;
            }
            if (_handle is null)
            {
                _handle = _openHandle();
                if (_handle is null) return;
                _handleMonitor = monitor;
                _handle.Start(monitor, CanvasWidth, CanvasHeight);
            }
            handle = _handle;
        }
        var src = handle.TryAcquireFrame(out var w, out var h);
        if (src is null || w <= 0 || h <= 0) return;
        ScreenMirrorEffect.BlitToCanvas(_canvas, src, w, h);
        _canvas.ApplyFlip(_postProcess.FlipX, _postProcess.FlipY);
        _canvas.ApplyPostProcess(_postProcess);
        _frame[0] = 0x03;
        _frame[1] = (byte)(CanvasWidth & 0xFF);
        _frame[2] = (byte)(CanvasWidth >> 8);
        _frame[3] = (byte)(CanvasHeight & 0xFF);
        _frame[4] = (byte)(CanvasHeight >> 8);
        _canvas.Pixels.CopyTo(_frame.AsSpan(5));
        _frame[^1] = 0;
        _ = _hub.BroadcastBinaryAsync(new ReadOnlyMemory<byte>(_frame));
    }

    private void ReleaseHandle()
    {
        lock (_gate)
        {
            _handle?.Stop();
            _handle = null;
        }
    }

    public void Dispose()
    {
        _hub.OnFirstClient -= Start;
        _hub.OnAllClientsGone -= Stop;
        Stop();
    }
}
