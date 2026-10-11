using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Lighting;
using Xunit;

namespace Nexus.Service.Tests.Lighting;

public class LcdFadeTests
{
    private static readonly TimeSpan Down = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan Up = TimeSpan.FromMilliseconds(900);

    private static List<int> Sample(LcdFade fade, int totalMs)
    {
        var levels = new List<int>();
        for (var t = 0; t <= totalMs; t += 40)
        {
            levels.Add(fade.LevelAt(1_000_000 + t));
        }
        return levels;
    }

    [Theory]
    [InlineData(100, 0)]
    [InlineData(60, 0)]
    [InlineData(7, 0)]
    [InlineData(0, 100)]
    [InlineData(0, 33)]
    [InlineData(25, 80)]
    public void Endpoints_are_exact_and_the_ramp_is_monotonic(int from, int to)
    {
        var fade = new LcdFade(from, to, Down, 1_000_000);

        var levels = Sample(fade, 1600);

        Assert.Equal(from, levels[0]);
        Assert.Equal(to, levels[^1]);
        Assert.Equal(from, fade.LevelAt(1_000_000));
        Assert.Equal(to, fade.LevelAt(1_000_000 + 1500));
        Assert.Equal(from < to ? levels.OrderBy(l => l) : levels.OrderByDescending(l => l), levels);
        Assert.All(levels, l => Assert.InRange(l, Math.Min(from, to), Math.Max(from, to)));
    }

    [Fact]
    public void The_curve_follows_perceived_lightness_not_percent()
    {
        var fade = new LcdFade(100, 0, Down, 1_000_000);

        // Half way through the time is half way in lightness, which is far below half the percent.
        var half = fade.LevelAt(1_000_000 + 750);

        Assert.InRange(half, 15, 30);
        // And the top of the ramp moves faster in percent than the bottom does.
        var early = 100 - fade.LevelAt(1_000_000 + 200);
        var late = fade.LevelAt(1_000_000 + 1100) - fade.LevelAt(1_000_000 + 1300);
        Assert.True(early > late, $"early {early} late {late}");
    }

    [Fact]
    public void Cheap_devices_take_one_step_per_sample_interval()
    {
        var down = new LcdFade(100, 0, Down, 1_000_000);
        var up = new LcdFade(0, 100, Up, 1_000_000);

        Assert.InRange(Sample(down, 1500).Distinct().Count(), 30, 38);
        Assert.InRange(Sample(up, 900).Distinct().Count(), 18, 23);
    }

    [Fact]
    public void An_explicit_step_count_is_honoured()
    {
        var fade = new LcdFade(100, 0, Down, 1_000_000, steps: 8);

        var levels = Sample(fade, 1520);

        // Eight pre-final levels plus the end.
        Assert.Equal(9, levels.Distinct().Count());
        Assert.Equal(0, levels[^1]);
    }
}
