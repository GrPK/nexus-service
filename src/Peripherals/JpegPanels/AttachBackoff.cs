using System;

namespace Nexus.Service.Peripherals.JpegPanels;

/// <summary>
/// Spaces out attach cycles for a panel that keeps failing, and decides which failures are
/// worth a log line, so a device that never recovers neither hammers the bus nor floods the log.
/// </summary>
public sealed class AttachBackoff
{
    public const long BaseDelayMs = 5_000;
    public const long MaxDelayMs = 300_000;

    /// <summary>A connection that stays up this long counts as healthy and clears the streak.</summary>
    public const long HealthyAfterMs = 30_000;

    /// <summary>Gap between repeat summaries once the first failure has been logged.</summary>
    public const long SummaryIntervalMs = 600_000;

    private readonly Func<long> _clock;
    private long _nextAttemptMs;
    private long _lastLogMs;
    private int _unloggedFailures;

    public AttachBackoff(Func<long> clock) => _clock = clock;

    public int Failures { get; private set; }

    /// <summary>Time to wait before the next attempt; zero when no failure is pending.</summary>
    public long DelayMs => Failures == 0 ? 0 : Math.Max(0, _nextAttemptMs - _clock());

    /// <summary>
    /// Records a failed cycle and grows the delay. Returns the number of failures to report
    /// when this one should be logged (the first, then one summary per interval), else 0.
    /// </summary>
    public int NoteFailure()
    {
        long now = _clock();
        Failures++;
        _unloggedFailures++;
        int shift = Math.Min(Failures - 1, 16);
        _nextAttemptMs = now + Math.Min(BaseDelayMs << shift, MaxDelayMs);
        if (Failures > 1 && now - _lastLogMs < SummaryIntervalMs)
        {
            return 0;
        }
        _lastLogMs = now;
        int report = _unloggedFailures;
        _unloggedFailures = 0;
        return report;
    }

    public void NoteHealthy()
    {
        Failures = 0;
        _unloggedFailures = 0;
        _nextAttemptMs = 0;
    }
}
