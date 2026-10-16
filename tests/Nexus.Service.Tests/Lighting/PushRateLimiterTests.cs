using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Rgb;

namespace Nexus.Service.Tests.Lighting;

public class PushRateLimiterTests
{
    private sealed class Harness
    {
        public long Now = 1000;
        public readonly List<int> Trailing = new();
        public readonly List<(int Delay, Action Work)> Scheduled = new();
        public readonly PushRateLimiter<int> Limiter;

        public Harness()
        {
            Limiter = new PushRateLimiter<int>(Trailing.Add, () => Now, (d, w) => Scheduled.Add((d, w)));
        }
    }

    [Theory]
    [InlineData(30, 30)]
    [InlineData(10, 90)]
    [InlineData(1, 900)]
    public void Gap_is_ninety_percent_of_the_period(int hz, int expectedMs)
    {
        Assert.Equal(expectedMs, PushRateLimiter<int>.GapMs(hz));
    }

    [Fact]
    public void Drops_inside_the_gap_and_passes_after_it()
    {
        var h = new Harness();
        Assert.True(h.Limiter.TryPass(0, 10));
        h.Now += 50;
        Assert.False(h.Limiter.TryPass(0, 10));
        h.Now += 40;
        Assert.True(h.Limiter.TryPass(0, 10));
    }

    [Fact]
    public void Dropped_frame_schedules_one_trailing_send_for_the_remaining_gap()
    {
        var h = new Harness();
        h.Limiter.TryPass(0, 10);
        h.Now += 30;
        Assert.False(h.Limiter.TryPass(0, 10));
        Assert.False(h.Limiter.TryPass(0, 10));
        var scheduled = Assert.Single(h.Scheduled);
        Assert.Equal(60, scheduled.Delay);

        h.Now += 60;
        scheduled.Work();
        Assert.Equal(new[] { 0 }, h.Trailing);

        // The trailing send spent the gap, so a frame right after it is dropped again.
        h.Now += 10;
        Assert.False(h.Limiter.TryPass(0, 10));
        Assert.Equal(2, h.Scheduled.Count);
    }

    [Fact]
    public void Keys_are_independent_and_zero_cap_never_drops()
    {
        var h = new Harness();
        Assert.True(h.Limiter.TryPass(0, 1));
        Assert.True(h.Limiter.TryPass(1, 1));
        Assert.True(h.Limiter.TryPass(0, 0));
        Assert.True(h.Limiter.TryPass(0, 0));
    }

    [Fact]
    public void Split_zones_sharing_a_push_take_the_lowest_cap()
    {
        var caps = new Dictionary<int, int>();
        RgbBridge.FoldCap(caps, 2, 20);
        RgbBridge.FoldCap(caps, 2, 5);
        RgbBridge.FoldCap(caps, 2, 15);
        RgbBridge.FoldCap(caps, 3, 30);
        Assert.Equal(5, caps[2]);
        Assert.Equal(30, caps[3]);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(30, true)]
    [InlineData(1, true)]
    [InlineData(25, false)]
    [InlineData(0, false)]
    [InlineData(60, false)]
    [InlineData(-5, false)]
    public void Only_listed_rates_are_valid(int? hz, bool valid)
    {
        Assert.Equal(valid, DeviceRateCap.IsValid(hz));
    }

    [Fact]
    public void Engine_default_interval_is_thirty_hz()
    {
        using var engine = new Nexus.Service.Lighting.Engine.LightingEngine();
        Assert.Equal(33, engine.FrameIntervalMs);
    }
}
