using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Lighting.Engine.Gpu;
using Xunit;

namespace Nexus.Service.Tests.Lighting.Gpu;

/// <summary>A lost context frees its thread, releases every parked caller and re-creates through the normal init path. The init body is substituted: a real GL init is host-dependent.</summary>
public class GpuContextLossTests
{
    private static GpuContext Started(out int[] inits)
    {
        var count = new int[1];
        inits = count;
        var gpu = new GpuContext(160, 90, null, () => count[0]++);
        lock (gpu.Lock)
        {
            gpu.EnsureInitializedLocked();
        }
        Assert.True(gpu.WaitForInit(TimeSpan.FromSeconds(5)));
        return gpu;
    }

    [Fact]
    public void Loss_raises_the_event_after_the_thread_exits_and_a_rearm_brings_a_new_generation()
    {
        var gpu = Started(out var inits);
        var generation = gpu.Generation;
        using var raised = new ManualResetEventSlim(false);
        gpu.ContextLost += _ => raised.Set();

        gpu.ReportContextLost("test");

        Assert.True(raised.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(gpu.Available);
        Assert.True(gpu.Failed);
        Assert.True(gpu.RearmAfterLatch());
        lock (gpu.Lock)
        {
            gpu.EnsureInitializedLocked();
        }
        Assert.True(gpu.WaitForInit(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, inits[0]);
        Assert.Equal(generation + 1, gpu.Generation);
        gpu.Dispose();
    }

    [Fact]
    public void Invoke_after_a_loss_returns_without_running_the_work_or_blocking()
    {
        var gpu = Started(out _);
        gpu.ReportContextLost("test");
        var ran = false;

        var call = Task.Run(() => gpu.Invoke(() => ran = true));

        Assert.True(call.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(ran);
        gpu.Dispose();
    }

    [Fact]
    public void Callers_racing_a_loss_are_never_left_parked()
    {
        for (var round = 0; round < 20; round++)
        {
            var gpu = Started(out _);
            var callers = new Task[4];
            for (var i = 0; i < callers.Length; i++)
            {
                callers[i] = Task.Run(() =>
                {
                    for (var n = 0; n < 200; n++)
                    {
                        gpu.Invoke(() => { });
                    }
                });
            }

            gpu.ReportContextLost("race");

            Assert.True(Task.WaitAll(callers, TimeSpan.FromSeconds(10)), $"a caller stayed parked in round {round}");
            gpu.Dispose();
        }
    }
}
