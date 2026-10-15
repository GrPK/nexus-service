using Nexus.Service.Lighting.Engine;
using Xunit;

namespace Nexus.Service.Tests.Lighting;

public class WarpRenderBudgetTests
{
    [Theory]
    [InlineData(0.0, 33, 33)]
    [InlineData(0.69, 33, 33)]
    [InlineData(1.65, 33, 33)]
    [InlineData(2.5, 33, 50)]
    [InlineData(4.0, 33, 80)]
    [InlineData(9.2, 33, 100)]
    [InlineData(200.0, 33, 100)]
    [InlineData(2.5, 16, 50)]
    [InlineData(0.1, 16, 16)]
    public void IntervalMs_HoldsCostInBudgetWithinFloorAndFrameRate(double costMs, int frameMs, int expected)
    {
        Assert.Equal(expected, WarpRenderBudget.IntervalMs(costMs, frameMs));
    }

    [Fact]
    public void IntervalMs_NeverFasterThanTheFrameRateEvenWhenTheFloorIsSlower()
    {
        Assert.Equal(250, WarpRenderBudget.IntervalMs(50, 250));
    }

    [Fact]
    public void Smooth_SeedsThenBlends()
    {
        Assert.Equal(8.0, WarpRenderBudget.Smooth(0, false, 8.0));
        Assert.Equal(6.0, WarpRenderBudget.Smooth(8.0, true, 0.0));
    }

    [Fact]
    public void CheapSource_RendersEveryTick()
    {
        var b = new WarpRenderBudget();
        var src = new object();
        for (var t = 0L; t < 3000; t += 33)
        {
            Assert.True(b.ShouldRender(src, t, 33));
            b.Record(t, 0.7);
        }
    }

    [Fact]
    public void CostlySource_RendersAtTheFloorRate()
    {
        var b = new WarpRenderBudget();
        var src = new object();
        var renders = 0;
        for (var t = 0L; t < 10_000; t += 33)
        {
            if (b.ShouldRender(src, t, 33))
            {
                renders++;
                b.Record(t, 9.2);
            }
        }
        Assert.InRange(renders, 85, 105);
    }

    [Fact]
    public void NewSource_RendersImmediatelyAndRecoversWhenCheap()
    {
        var b = new WarpRenderBudget();
        var heavy = new object();
        Assert.True(b.ShouldRender(heavy, 0, 33));
        b.Record(0, 9.2);
        Assert.False(b.ShouldRender(heavy, 33, 33));
        Assert.True(b.ShouldRender(new object(), 66, 33));
    }

    [Fact]
    public void SourceThatGetsCheaper_ClimbsBackToFullRate()
    {
        var b = new WarpRenderBudget();
        var src = new object();
        var t = 0L;
        b.ShouldRender(src, t, 33);
        b.Record(t, 9.2);
        var last = 0L;
        for (; t < 20_000; t += 33)
        {
            if (b.ShouldRender(src, t, 33))
            {
                last = t;
                b.Record(t, 0.5);
            }
        }
        Assert.Equal(t - 33, last);
    }
}
