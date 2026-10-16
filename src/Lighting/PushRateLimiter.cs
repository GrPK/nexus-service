using System.Collections.Concurrent;

namespace Nexus.Service.Lighting;

/// <summary>User-selectable per-device update caps. Null means no cap.</summary>
public static class DeviceRateCap
{
    public static readonly int[] AllowedHz = { 30, 20, 15, 10, 5, 1 };

    public static bool IsValid(int? hz) => hz is null || Array.IndexOf(AllowedHz, hz.Value) >= 0;
}

/// <summary>
/// Per-key minimum gap between pushes. Holds only the last-pass stamp: the
/// caller keeps the frame, and a dropped frame stays unsent in the caller's
/// dedupe baseline, so its next tick offers it again once the gap has elapsed.
/// </summary>
public sealed class PushRateLimiter<TKey> where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, long> _lastPassMs = new();

    /// <summary>Slightly under the period at <paramref name="capHz"/>, so timer jitter never undershoots the cap.</summary>
    public static int GapMs(int capHz) => 900 / Math.Max(1, capHz);

    /// <summary>True when a push may go out at <paramref name="nowMs"/>; records the pass.</summary>
    public bool TryPass(TKey key, int capHz, long nowMs)
    {
        if (capHz <= 0)
        {
            return true;
        }
        if (_lastPassMs.TryGetValue(key, out var last) && nowMs - last < GapMs(capHz))
        {
            return false;
        }
        _lastPassMs[key] = nowMs;
        return true;
    }

    public void Reset() => _lastPassMs.Clear();
}
