using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Platform;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>What GET /devices/lianli-screen88/settings reports that changes without a request.</summary>
public readonly record struct UniversalScreen88PushState(bool Connected, bool InWindowsDisplay, string? Playing);

/// <summary>Payload of the <c>lianli-screen88</c> topic: a revision plus the state that changed; subscribers re-read the settings route.</summary>
public sealed class UniversalScreen88ChangedFrame
{
    public long Revision { get; set; }
    /// <summary>The glass is attached as a panel under Nexus Control.</summary>
    public bool Connected { get; set; }
    /// <summary>The glass is in its own Windows display mode (1A86:AD21 present).</summary>
    public bool InWindowsDisplay { get; set; }
    /// <summary>Library item on the glass now, or null.</summary>
    public string? Playing { get; set; }
}

/// <summary>
/// Publishes the <c>lianli-screen88</c> topic when what the 8.8's settings route reports changes on its own:
/// the glass attaching or detaching as a panel, its Windows display identity arriving or leaving, or the clip
/// on its decoder changing. Those raise <see cref="Wake"/>; the check runs after a short settle, so a burst of
/// them (several PnP notifications for one replug) reads the state once, and a frame goes out only when the
/// state differs from the last one. While someone is subscribed a slow backstop check also runs, for a USB
/// change no notification reported.
/// </summary>
public sealed class UniversalScreen88Broadcaster : BackgroundService
{
    public const string Topic = "lianli-screen88";

    private readonly MultiplexHub _sockets;
    private readonly Func<UniversalScreen88PushState> _read;
    private readonly int _settleMs;
    private readonly int _backstopMs;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly object _lock = new();
    private UniversalScreen88PushState? _last;

    /// <param name="read">The state as the settings route reads it.</param>
    public UniversalScreen88Broadcaster(MultiplexHub sockets, Func<UniversalScreen88PushState> read, int settleMs = 250, int backstopMs = 2000)
    {
        _sockets = sockets;
        _read = read;
        _settleMs = settleMs;
        _backstopMs = backstopMs;
        _sockets.OnTopicFirstSubscriber += topic =>
        {
            if (topic == Topic)
            {
                Wake();
            }
        };
    }

    /// <summary>Something the topic reports may have changed; cheap and safe from any thread, a PnP callback included.</summary>
    public void Wake()
    {
        try
        {
            if (_wake.CurrentCount == 0)
            {
                _wake.Release();
            }
        }
        catch (SemaphoreFullException)
        {
            // Already woken; one check covers both.
        }
    }

    /// <summary>
    /// Reads the state and publishes it when it differs from the last one read. The first read only sets the
    /// baseline. True when it published.
    /// </summary>
    internal bool Check()
    {
        var now = _read();
        lock (_lock)
        {
            var previous = _last;
            _last = now;
            if (previous is null || previous.Value == now)
            {
                return false;
            }
        }
        if (!_sockets.TopicHasSubscribers(Topic))
        {
            return false;
        }
        var frame = new UniversalScreen88ChangedFrame
        {
            Revision = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Connected = now.Connected,
            InWindowsDisplay = now.InWindowsDisplay,
            Playing = now.Playing,
        };
        _ = _sockets.BroadcastTopicAsync(Topic, WsEnvelope.Build(Topic, frame, AppJsonContext.Default.UniversalScreen88ChangedFrame));
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            SafeCheck();
            while (!stoppingToken.IsCancellationRequested)
            {
                var timeout = _sockets.TopicHasSubscribers(Topic) ? _backstopMs : Timeout.Infinite;
                if (await _wake.WaitAsync(timeout, stoppingToken).ConfigureAwait(false) && _settleMs > 0)
                {
                    await Task.Delay(_settleMs, stoppingToken).ConfigureAwait(false);
                    // Wakes that came in while settling are covered by this check.
                    _wake.Wait(0, CancellationToken.None);
                }
                SafeCheck();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void SafeCheck()
    {
        try
        {
            Check();
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[{UniversalScreen88Driver.Id}] state push failed: {ex.Message}");
        }
    }
}
