using System.Collections.Concurrent;

namespace Nexus.Service.Lighting;

/// <summary>User-selectable per-device update caps. Null means no cap.</summary>
public static class DeviceRateCap
{
    public static readonly int[] AllowedHz = { 30, 20, 15, 10, 5, 1 };

    public static bool IsValid(int? hz) => hz is null || Array.IndexOf(AllowedHz, hz.Value) >= 0;
}

/// <summary>
/// Per-key minimum gap between pushes, with a trailing callback so the latest
/// frame still goes out once the gap expires. Holds no frame data: the caller
/// re-reads its own newest buffer in <c>onTrailing</c>, so nothing queues.
/// </summary>
public sealed class PushRateLimiter<TKey> where TKey : notnull
{
    private sealed class State
    {
        public long LastMs = long.MinValue / 2;
        public bool TrailingScheduled;
    }

    private readonly Func<long> _nowMs;
    private readonly Action<int, Action> _schedule;
    private readonly Action<TKey> _onTrailing;
    private readonly ConcurrentDictionary<TKey, State> _states = new();

    public PushRateLimiter(Action<TKey> onTrailing, Func<long>? nowMs = null, Action<int, Action>? schedule = null)
    {
        _onTrailing = onTrailing;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
        _schedule = schedule ?? ((delayMs, work) => _ = Task.Delay(delayMs).ContinueWith(_ => work(), TaskScheduler.Default));
    }

    /// <summary>90% of the period at <paramref name="capHz"/>, so timer jitter never undershoots the cap.</summary>
    public static int GapMs(int capHz) => 900 / Math.Max(1, capHz);

    /// <summary>True when a push may go out now. False schedules one trailing callback for when the gap expires.</summary>
    public bool TryPass(TKey key, int capHz)
    {
        if (capHz <= 0)
        {
            return true;
        }
        var state = _states.GetOrAdd(key, _ => new State());
        var gap = GapMs(capHz);
        int wait;
        lock (state)
        {
            var now = _nowMs();
            var since = now - state.LastMs;
            if (since >= gap)
            {
                state.LastMs = now;
                return true;
            }
            if (state.TrailingScheduled)
            {
                return false;
            }
            state.TrailingScheduled = true;
            wait = (int)(gap - since);
        }
        _schedule(wait, () =>
        {
            lock (state)
            {
                state.TrailingScheduled = false;
                state.LastMs = _nowMs();
            }
            _onTrailing(key);
        });
        return false;
    }
}
