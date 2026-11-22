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
/// Streams the HydroShift II OLED Curved edge LEDs: engine output in canvas mode, or one of
/// L-Connect's edge effects (<see cref="HydroShift2CurveEdgeEffects"/>) frame by frame at L-Connect's
/// pace. The board holds a frame until the next, so only changed frames go out.
/// </summary>
public sealed class HydroShift2CurveLightingFrameWriter : BackgroundService
{
    private const int TickMs = 33;

    // A sleep can wake late, so the coarse wait ends this far before the deadline and a spin takes the rest.
    private const long CoarseWaitLatenessMs = 1;

    private readonly LightingEngine _engine;
    private readonly HydroShift2CurveBoard _board;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private readonly FeatureGates _gates;
    private readonly DeviceStructure _structure = HydroShift2CurveLightingDeviceProvider.BuildStructure();
    private readonly byte[] _rgb = new byte[HydroShift2CurveProtocol.LedCount * 3];
    private RgbColor[][] _segBuf = Array.Empty<RgbColor[]>();

    private string? _effectSignature;
    private string? _effectMode;
    private List<byte[]> _effectFrames = new();
    private int _effectPeriodMs = TickMs;
    private int _frameIndex;

    public HydroShift2CurveLightingFrameWriter(
        LightingEngine engine, HydroShift2CurveBoard board, IConfigStore store, Np50IdentifyTracker identify, FeatureGates? gates = null)
    {
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
        // Effect frames are 20..48 ms apart, finer than the default timer's 15.6 ms steps can hold.
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
                    ServiceLog.Warn($"[{HydroShift2CurveLcdDriver.Id}] lighting tick failed: {ex.Message}");
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
        var ls = settings.Devices.HydroShift2CurveLighting;
        if (ls.Mode != HydroShift2CurveLightingSettings.CanvasMode && HydroShift2CurveEdgeEffects.Modes.Contains(ls.Mode))
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
        for (int i = 0; i < HydroShift2CurveProtocol.LedCount && i < leds.Length; i++)
        {
            _rgb[i * 3] = leds[i].R;
            _rgb[(i * 3) + 1] = leds[i].G;
            _rgb[(i * 3) + 2] = leds[i].B;
        }
        _board.SetLeds(_rgb);
    }

    private int TickEffect(NexusSettings settings, HydroShift2CurveLightingSettings ls)
    {
        var id = HydroShift2CurveLightingDeviceProvider.EdgeZoneId;
        // Uncontrolled: stop writing so the edge keeps whatever it last showed.
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
        var brightness = HydroShift2CurveEdgeEffects.BrightnessByte(HydroShift2CurveEdgeEffects.SliderPercent(ls.Brightness) * cap);
        var step = HydroShift2CurveEdgeEffects.SpeedStep(HydroShift2CurveEdgeEffects.SliderPercent(ls.Speed));
        var reverse = HydroShift2CurveEdgeEffects.HasDirection(ls.Mode) && ls.Direction == 1;
        var signature = $"{ls.Mode}|{brightness}|{step}|{reverse}|{string.Join(',', ls.Colors)}|{pref?.ColorOrder}";
        if (signature != _effectSignature)
        {
            _effectFrames = Render(ls, brightness, reverse, pref?.ColorOrder);
            _effectPeriodMs = _effectFrames.Count > 1 ? HydroShift2CurveEdgeEffects.FramePeriodMs(step) : TickMs;
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

    internal static List<byte[]> Render(HydroShift2CurveLightingSettings ls, int brightness, bool reverse, string? colorOrder)
    {
        var colors = new List<(byte R, byte G, byte B)>(ls.Colors.Count);
        foreach (var hex in ls.Colors)
        {
            if (hex.Length == 7 && hex[0] == '#' && uint.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, null, out var rgb))
            {
                colors.Add(((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            }
        }
        var frames = HydroShift2CurveEdgeEffects.Render(ls.Mode, colors, brightness, reverse);
        // Permute the rendered output so the effect's own colours (rainbow, twinkle, the defaults)
        // follow the order too; the renderer is channel-symmetric.
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
