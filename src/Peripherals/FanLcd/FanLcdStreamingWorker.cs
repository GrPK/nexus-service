using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Devices;
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.FanLcd;

/// <summary>
/// Streams each attached fan screen's configured content, for one <see cref="IFanLcdScreens"/>
/// family. Runs one independent loop per attached serial, since each screen's animation has
/// its own frame cadence and a single shared loop cannot pace three screens with different
/// GIF/video delays at once. A screen in panel mode is left to its streamed-panel session.
/// </summary>
public sealed class FanLcdStreamingWorker : BackgroundService
{
    private const int DiscoveryPollMs = 2000;
    private const int IdlePollMs = 1000;
    /// <summary>~1 fps: a sensor/clock frame only needs to look live, not smooth.</summary>
    private const int SensorClockPollMs = 1000;

    private readonly IFanLcdScreens _screens;
    private readonly Slv3LcdMediaLibrary _library;
    private readonly Slv3LcdSensorReader _sensorReader;
    private readonly IConfigStore _store;
    private readonly DeviceControlGate _gate;
    private readonly Dictionary<string, (CancellationTokenSource Cts, Task Loop)> _running = new(StringComparer.OrdinalIgnoreCase);

    // Cancelled loops still unwinding; a serial restarts only once its old loop's hand-back has
    // run, or that hand-back would take the screen from the new loop.
    private readonly Dictionary<string, Task> _stopping = new(StringComparer.OrdinalIgnoreCase);

    public FanLcdStreamingWorker(IFanLcdScreens screens, Slv3LcdMediaLibrary library, Slv3LcdSensorReader sensorReader, IConfigStore store, DeviceControlGate gate)
    {
        _screens = screens;
        _library = library;
        _sensorReader = sensorReader;
        _store = store;
        _gate = gate;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (!_gate.IsEnabled(_screens.HandlerId))
                    {
                        StopAll();
                    }
                    else
                    {
                        ReconcileScreenLoops(stoppingToken);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ServiceLog.Error($"[{_screens.LogTag}] streaming worker error: {ex.Message}");
                }

                await Task.Delay(DiscoveryPollMs, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            // The delay above throws on the same cancellation that ends this
            // loop, so StopAll must run here rather than after the loop to
            // guarantee every per-screen CancellationTokenSource is disposed.
            StopAll();
        }
    }

    private void ReconcileScreenLoops(CancellationToken stoppingToken)
    {
        foreach (var (serial, unwinding) in new List<KeyValuePair<string, Task>>(_stopping))
        {
            if (unwinding.IsCompleted)
            {
                _stopping.Remove(serial);
            }
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var serial in _screens.AttachedSerials())
        {
            if (string.IsNullOrWhiteSpace(serial))
            {
                continue;
            }
            seen.Add(serial);
            if (_running.ContainsKey(serial))
            {
                continue;
            }
            if (_stopping.ContainsKey(serial))
            {
                continue;
            }
            var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _running[serial] = (cts, RunScreenLoopAsync(serial, cts.Token));
        }

        foreach (var serial in new List<string>(_running.Keys))
        {
            if (!seen.Contains(serial))
            {
                StopScreen(serial);
            }
        }
    }

    private void StopScreen(string serial)
    {
        if (!_running.Remove(serial, out var loop))
        {
            return;
        }
        loop.Cts.Cancel();
        loop.Cts.Dispose();
        _stopping[serial] = loop.Loop;
    }

    private void StopAll()
    {
        foreach (var serial in new List<string>(_running.Keys))
        {
            StopScreen(serial);
        }
    }

    /// <summary>
    /// One screen's lifetime loop: reapplies brightness/rotation/content on
    /// change and streams the active content. A fresh call (attach after
    /// detach) starts with sentinel "applied" values, so a static image
    /// re-pushes automatically on reconnect without special-casing it.
    /// </summary>
    private async Task RunScreenLoopAsync(string serial, CancellationToken ct)
    {
        string? loadedMediaId = null;
        Slv3LcdImageData? loadedImage = null;
        var appliedBrightness = -1;
        var lockSlept = false;
        var appliedRotation = -1;
        var appliedContentType = "";
        var pushedStatic = false;
        // Animation phase resets on every (re)connect and content-type switch;
        // continuity across a reconnect is not required.
        var animationStartUtc = DateTime.UtcNow;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    _screens.PrepareFrames();

                    var settings = _screens.SettingsOf(_store.Load().Devices).TryGetValue(serial, out var s)
                        ? s
                        : new LianLiWirelessScreenSettings();

                    _screens.SyncBrightness(serial, settings, ref appliedBrightness, ref lockSlept);
                    if (settings.Rotation != appliedRotation)
                    {
                        _screens.SetRotation(serial, settings.Rotation);
                        appliedRotation = settings.Rotation;
                    }

                    if (settings.MediaId != loadedMediaId)
                    {
                        loadedMediaId = settings.MediaId;
                        loadedImage = loadedMediaId is null ? null : _library.LoadFrames(loadedMediaId);
                        pushedStatic = false;
                    }
                    if (settings.ContentType != appliedContentType)
                    {
                        appliedContentType = settings.ContentType;
                        pushedStatic = false;
                        animationStartUtc = DateTime.UtcNow;
                        // Taken back before this loop draws or releases, so a lingering panel
                        // session's frame cannot land after the switch.
                        _screens.SetPanelOwned(serial, settings.ContentType == FanLcdContent.Panel);
                        if (settings.ContentType == "off")
                        {
                            _screens.Release(serial);
                        }
                    }

                    switch (settings.ContentType)
                    {
                        case "image":
                        case "gif":
                        case "video":
                            if (loadedImage is null)
                            {
                                await Task.Delay(IdlePollMs, ct).ConfigureAwait(false);
                                continue;
                            }
                            if (loadedImage.Frames.Length == 1)
                            {
                                if (!pushedStatic)
                                {
                                    pushedStatic = _screens.PushImage(serial, loadedImage.Frames[0].JpegBytes);
                                }
                                await Task.Delay(IdlePollMs, ct).ConfigureAwait(false);
                                continue;
                            }
                            await StreamAnimatedAsync(serial, loadedImage, ct).ConfigureAwait(false);
                            break;

                        case "sensor":
                        {
                            var reading = _sensorReader.Read(settings.SensorSource, settings.TempUnit);
                            var jpeg = Slv3LcdSensorRenderer.Render(
                                settings.SensorStyle, reading.Value, reading.Min, reading.Max, reading.Label, reading.Unit,
                                settings.ColorA, settings.ColorB);
                            _screens.PushImage(serial, jpeg);
                            await Task.Delay(SensorClockPollMs, ct).ConfigureAwait(false);
                            break;
                        }

                        case "clock":
                        {
                            var jpeg = Slv3LcdClockRenderer.Render(settings.ClockFace, DateTime.Now, settings.ColorA, settings.ColorB);
                            _screens.PushImage(serial, jpeg);
                            await Task.Delay(SensorClockPollMs, ct).ConfigureAwait(false);
                            break;
                        }

                        case "animation":
                        {
                            var elapsed = (DateTime.UtcNow - animationStartUtc).TotalSeconds;
                            var jpeg = Slv3LcdAnimationRenderer.Render(settings.AnimationId, elapsed, settings.ColorA, settings.ColorB);
                            _screens.PushImage(serial, jpeg);
                            await Task.Delay(Slv3LcdAnimationRenderer.FrameIntervalMs, ct).ConfigureAwait(false);
                            break;
                        }

                        default:
                            await Task.Delay(IdlePollMs, ct).ConfigureAwait(false);
                            break;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    ServiceLog.Error($"[{_screens.LogTag}] stream error for {serial}: {ex.Message}");
                    await Task.Delay(IdlePollMs, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            // After this loop's last push, so nothing it sends can follow the hand-back.
            _screens.SetPanelOwned(serial, false);
            _screens.Release(serial);
        }
    }

    private async Task StreamAnimatedAsync(string serial, Slv3LcdImageData image, CancellationToken ct)
    {
        foreach (var frame in image.Frames)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }
            _screens.PushImage(serial, frame.JpegBytes);
            await Task.Delay(Math.Max(10, frame.DelayMs), ct).ConfigureAwait(false);
        }
    }
}
