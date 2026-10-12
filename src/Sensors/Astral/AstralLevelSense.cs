using System;
using System.Collections.Generic;

namespace Nexus.Service.Sensors.Astral;

/// <summary>
/// GPU Tweak III's "Level Sense" for one ROG Astral card, decoded from GPU Tweak III.exe and
/// ExpanModule.dll: the angles are smoothed (new = 0.5 * old + 0.5 * sample, the first sample
/// seeds it), a calibration takes <see cref="CalibrationSamples"/> smoothed readings and keeps the
/// most frequent value per axis (equal within 1e-5, the smallest on a tie), and the card's level is
/// how far X and Y moved from the calibration. Like GPU Tweak, that comparison ignores the sign: a
/// reading on the other side of zero is compared by magnitude. Not thread-safe; the owner locks.
/// </summary>
internal sealed class AstralLevelSense
{
    /// <summary>GPU Tweak's default "GSensor: Calibrate time": one reading per second for five seconds.</summary>
    public const int CalibrationSamples = 5;

    private const float FilterWeight = 0.5f;
    private const float ModeTolerance = 1e-5f;

    private AstralGSensorParser.Angles? _smoothed;
    private List<float>? _pendingX;
    private List<float>? _pendingY;

    public readonly record struct Calibration(float X, float Y);

    /// <summary>The calibration deviations are measured against; null until one completes or is loaded.</summary>
    public Calibration? Current { get; private set; }

    public bool IsCalibrating => _pendingX is not null;

    /// <summary>The smoothed angles of the latest reading; null before the first one.</summary>
    public AstralGSensorParser.Angles? Smoothed => _smoothed;

    public void Load(Calibration calibration) => Current = calibration;

    /// <summary>Starts a calibration from the next <see cref="CalibrationSamples"/> readings.</summary>
    public void StartCalibration()
    {
        _pendingX = new List<float>(CalibrationSamples);
        _pendingY = new List<float>(CalibrationSamples);
    }

    /// <summary>Adds a reading; returns the calibration this reading completed, else null.</summary>
    public Calibration? Add(AstralGSensorParser.Angles sample)
    {
        _smoothed = _smoothed is { } prev
            ? new AstralGSensorParser.Angles(
                Smooth(prev.X, sample.X), Smooth(prev.Y, sample.Y), Smooth(prev.Z, sample.Z))
            : sample;

        if (_pendingX is null || _pendingY is null)
        {
            return null;
        }
        _pendingX.Add(_smoothed.Value.X);
        _pendingY.Add(_smoothed.Value.Y);
        if (_pendingX.Count < CalibrationSamples)
        {
            return null;
        }

        Current = new Calibration(Mode(_pendingX), Mode(_pendingY));
        _pendingX = null;
        _pendingY = null;
        return Current;
    }

    /// <summary>How far X and Y moved from the calibration, in degrees; null before the first
    /// reading or calibration. A recalibration keeps measuring against the previous one until it
    /// completes.</summary>
    public (float X, float Y)? Deviation =>
        _smoothed is { } now && Current is { } cal
            ? (Delta(now.X, cal.X), Delta(now.Y, cal.Y))
            : null;

    private static float Smooth(float previous, float sample) =>
        (1 - FilterWeight) * previous + FilterWeight * sample;

    // GPU Tweak: the difference when both share a sign, else the sum, so the result is the change
    // in magnitude.
    private static float Delta(float current, float calibrated) =>
        current * calibrated >= 0 ? current - calibrated : current + calibrated;

    internal static float Mode(IReadOnlyList<float> samples)
    {
        var values = new List<float>();
        var counts = new List<int>();
        foreach (var sample in samples)
        {
            var i = values.FindIndex(v => Math.Abs(v - sample) <= ModeTolerance);
            if (i >= 0)
            {
                counts[i]++;
                continue;
            }
            values.Add(sample);
            counts.Add(1);
        }

        var best = float.NaN;
        var bestCount = 0;
        for (var i = 0; i < values.Count; i++)
        {
            if (counts[i] > bestCount || (counts[i] == bestCount && values[i] < best))
            {
                best = values[i];
                bestCount = counts[i];
            }
        }
        return best;
    }
}
