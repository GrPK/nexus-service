using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Lighting;

/// <summary>
/// Streams the Universal Screen 8.8 bezel LEDs: engine output in canvas mode, or one of L-Connect's
/// bezel effects (<see cref="UniversalScreen88LedEffects"/>) frame by frame at L-Connect's pace. The
/// board holds a frame until the next, so only changed frames go out.
/// </summary>
public sealed class UniversalScreen88LedLightingFrameWriter : BackgroundService
{
    private const int TickMs = 33;

    // A sleep can wake late, so the coarse wait ends this far before the deadline and a spin takes the rest.
    private const long CoarseWaitLatenessMs = 1;

    private readonly LightingEngine _engine;
    private readonly UniversalScreen88LedBoard _board;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private readonly FeatureGates _gates;
    private readonly LianLiMergeService? _merge;
    private readonly DeviceStructure _structure = UniversalScreen88LedLightingDeviceProvider.BuildStructure();
    private readonly byte[] _rgb = new byte[UniversalScreen88LedProtocol.LedCount * 3];
    private RgbColor[][] _segBuf = Array.Empty<RgbColor[]>();

    private string? _effectSignature;
    private string? _effectMode;
    private List<byte[]> _effectFrames = new();
    private int _effectPeriodMs = TickMs;
    private int _frameIndex;

    public UniversalScreen88LedLightingFrameWriter(
        LightingEngine engine, UniversalScreen88LedBoard board, IConfigStore store, Np50IdentifyTracker identify, FeatureGates? gates = null,
        LianLiMergeService? merge = null)
    {
        _merge = merge;
        _engine = engine;
        _board = board;
        _store = store;
        _identify = identify;
        _gates = gates ?? FeatureGates.AllEnabled;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Factory.StartNew(() => Run(stoppingToken), stoppingToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private void Run(CancellationToken ct)
    {
        // Effect frames are 20..71 ms apart, finer than the default timer's 15.6 ms steps can hold.
        HighResolutionTicker? ticker = OperatingSystem.IsWindows() ? HighResolutionTicker.TryCreate(ct) : null;
        try
        {
            long next = Stopwatch.GetTimestamp();
            while (!ct.IsCancellationRequested)
            {
                int periodMs;
                try
                {
                    periodMs = Tick();
                }
                catch (Exception ex)
                {
                    ServiceLog.Warn($"[{UniversalScreen88Driver.Id}] lighting tick failed: {ex.Message}");
                    periodMs = TickMs;
                }
                var period = periodMs * Stopwatch.Frequency / 1000;
                var now = Stopwatch.GetTimestamp();
                // Deadlines sit on a fixed grid; a whole period behind restarts it instead of bursting.
                next = now - next > period ? now + period : next + period;
                var waited = ticker is not null ? ticker.WaitUntil(next) : SleepThenSpin(ct, next);
                if (!waited) break;
            }
        }
        finally
        {
            ticker?.Dispose();
        }
    }

    /// <summary>Writes one frame and returns the ms until the next is due.</summary>
    internal int Tick()
    {
        if (!_gates.Lighting || !_board.IsAvailable)
        {
            DropEffect();
            return TickMs;
        }
        var settings = _store.Load();
        // Quick Sync on a merged effect: the bezel's stretch of the line drawn through every device.
        if (settings.Devices.LianLiSync.Enabled && _merge?.Current(settings) is { } merged
            && merged.Outputs.TryGetValue(LianLiMergeService.Screen88Id, out var share) && share.Frames is { Count: > 0 })
        {
            return TickMerged(settings, merged, share);
        }
        // Quick Sync stands in for the bezel's own look while it is on.
        var ls = LianLiSync.Screen88Lighting(settings);
        if (ls.Mode != UniversalScreen88LightingSettings.CanvasMode && UniversalScreen88LedEffects.Modes.Contains(ls.Mode))
        {
            return TickEffect(settings, ls);
        }
        DropEffect();
        TickCanvas(settings);
        return TickMs;
    }

    private void TickCanvas(NexusSettings settings)
    {
        var devices = _engine.Devices;
        if (devices.Length == 0) return;

        var uncontrolled = settings.Devices.UncontrolledLightingDevices;
        var zones = ZoneResolution.Resolve(_structure, settings);
        // Left alone entirely, the board keeps whatever another app last sent.
        if (ZoneResolution.IsFullyUncontrolled(zones, uncontrolled)) return;

        SegmentFrameComposer.EnsureBuffers(_structure, ref _segBuf);
        SegmentFrameComposer.Compose(
            _structure, zones, devices, settings.Devices.DisabledLightingDevices, uncontrolled,
            settings.Devices.LightingDevicePrefs, MasterBrightness.Resolve(settings), 1.0,
            DateTime.UtcNow.Ticks, _identify, _segBuf, _engine.Live);

        var leds = _segBuf[0];
        for (int i = 0; i < UniversalScreen88LedProtocol.LedCount && i < leds.Length; i++)
        {
            _rgb[i * 3] = leds[i].R;
            _rgb[(i * 3) + 1] = leds[i].G;
            _rgb[(i * 3) + 2] = leds[i].B;
        }
        _board.SetLeds(_rgb);
    }

    private int TickEffect(NexusSettings settings, UniversalScreen88LightingSettings ls)
    {
        var id = UniversalScreen88LedLightingDeviceProvider.RingZoneId;
        // Uncontrolled: stop writing so the ring keeps whatever it last showed.
        if (settings.Devices.UncontrolledLightingDevices.Contains(id))
        {
            DropEffect();
            return TickMs;
        }
        if (_identify.TryGetActive(id, DateTime.UtcNow.Ticks, out _))
        {
            DropEffect();
            Array.Fill(_rgb, (byte)255);
            _board.SetLeds(_rgb);
            return TickMs;
        }
        if (settings.Devices.DisabledLightingDevices.Contains(id))
        {
            DropEffect();
            Array.Clear(_rgb);
            _board.SetLeds(_rgb);
            return TickMs;
        }

        settings.Devices.LightingDevicePrefs.TryGetValue(id, out var pref);
        // The card and master brightness (and the blackouts that zero it) scale the effect's own
        // brightness slider before L-Connect's mapping, so at full they leave it as L-Connect plays it.
        var cap = MasterBrightness.Resolve(settings).Combine(Math.Clamp(pref?.Brightness ?? 100, 0, 100) / 100.0);
        var brightness = UniversalScreen88LedEffects.BrightnessByte(UniversalScreen88LedEffects.SliderPercent(ls.Brightness) * cap);
        var step = UniversalScreen88LedEffects.SpeedStep(UniversalScreen88LedEffects.SliderPercent(ls.Speed));
        var reverse = UniversalScreen88LedEffects.HasDirection(ls.Mode) && ls.Direction == 1;
        var signature = $"{ls.Mode}|{brightness}|{step}|{reverse}|{string.Join(',', ls.Colors)}|{pref?.ColorOrder}";
        if (signature != _effectSignature)
        {
            // Twinkle draws new random colours on every render, as L-Connect does on every apply.
            _effectFrames = Render(ls, brightness, reverse, pref?.ColorOrder);
            _effectPeriodMs = _effectFrames.Count > 1 ? UniversalScreen88LedEffects.FramePeriodMs(ls.Mode, step) : TickMs;
            // A brightness or colour change keeps the loop's place; a new mode starts it over.
            if (ls.Mode != _effectMode || _frameIndex >= _effectFrames.Count)
            {
                _frameIndex = 0;
            }
            _effectSignature = signature;
            _effectMode = ls.Mode;
        }

        _board.SetLeds(_effectFrames[_frameIndex]);
        _frameIndex = (_frameIndex + 1) % _effectFrames.Count;
        return _effectPeriodMs;
    }

    internal static List<byte[]> Render(UniversalScreen88LightingSettings ls, int brightness, bool reverse, string? colorOrder)
    {
        var colors = new List<(byte R, byte G, byte B)>(ls.Colors.Count);
        foreach (var hex in ls.Colors)
        {
            if (hex.Length == 7 && hex[0] == '#' && uint.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, null, out var rgb))
            {
                colors.Add(((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            }
        }
        var frames = UniversalScreen88LedEffects.Render(ls.Mode, colors, brightness, reverse);
        // Permute the rendered output so the effect's own colours follow the order too; the renderer is channel-symmetric.
        if (DeviceColorAdjust.NormalizeOrder(colorOrder) is not (null or "RGB"))
        {
            foreach (var frame in frames)
            {
                for (var i = 0; i + 2 < frame.Length; i += 3)
                {
                    DeviceColorAdjust.ReorderChannels(colorOrder!, ref frame[i], ref frame[i + 1], ref frame[i + 2]);
                }
            }
        }
        return frames;
    }

    /// <summary>
    /// Plays the bezel's stretch of Quick Sync's merged line on the clock every merged USB device shares,
    /// so they step together.
    /// </summary>
    private int TickMerged(NexusSettings settings, LianLiMergeService.Snapshot merged, LianLiMerge.Output share)
    {
        var id = UniversalScreen88LedLightingDeviceProvider.RingZoneId;
        if (settings.Devices.UncontrolledLightingDevices.Contains(id))
        {
            DropEffect();
            return TickMs;
        }
        if (_identify.TryGetActive(id, DateTime.UtcNow.Ticks, out _))
        {
            DropEffect();
            Array.Fill(_rgb, (byte)255);
            _board.SetLeds(_rgb);
            return TickMs;
        }
        if (settings.Devices.DisabledLightingDevices.Contains(id))
        {
            DropEffect();
            Array.Clear(_rgb);
            _board.SetLeds(_rgb);
            return TickMs;
        }

        settings.Devices.LightingDevicePrefs.TryGetValue(id, out var pref);
        // At full card and master brightness the frames go out as L-Connect draws them.
        var cap = MasterBrightness.Resolve(settings).Combine(Math.Clamp(pref?.Brightness ?? 100, 0, 100) / 100.0);
        var signature = "merge|" + merged.Signature + "|" + cap.ToString("R", CultureInfo.InvariantCulture) + "|" + pref?.ColorOrder;
        if (signature != _effectSignature)
        {
            _effectFrames = LianLiMergeService.Adjust(share.Frames!, cap, pref?.ColorOrder);
            _effectSignature = signature;
            _effectMode = null;
        }
        var (index, waitMs) = LianLiMergeService.FrameAt(merged.EpochMs, _merge!.NowMs, share.PeriodMs, _effectFrames.Count, TickMs);
        _board.SetLeds(_effectFrames[index]);
        return waitMs;
    }

    private void DropEffect()
    {
        _effectSignature = null;
        _effectMode = null;
        _frameIndex = 0;
    }

    private static bool SleepThenSpin(CancellationToken ct, long deadline)
    {
        var coarse = Stopwatch.Frequency * CoarseWaitLatenessMs / 1000;
        while (true)
        {
            if (ct.IsCancellationRequested)
            {
                return false;
            }
            var remaining = deadline - Stopwatch.GetTimestamp();
            if (remaining <= 0)
            {
                return true;
            }
            if (remaining > coarse)
            {
                ct.WaitHandle.WaitOne((int)((remaining - coarse) * 1000 / Stopwatch.Frequency));
            }
            else
            {
                Thread.SpinWait(20);
            }
        }
    }
}
