using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Lighting.Engine.Gpu;
using Xunit;

namespace Nexus.Service.Tests.Lighting.Gpu;

/// <summary>Models the cold lighting page: many background compiles in flight against a steady stream of live jobs. The init body is substituted; no GL runs.</summary>
public class GpuContextInvokeStepTests
{
    private static GpuContext Started()
    {
        var gpu = new GpuContext(160, 90, null, () => { });
        lock (gpu.Lock)
        {
            gpu.EnsureInitializedLocked();
        }
        Assert.True(gpu.WaitForInit(TimeSpan.FromSeconds(5)));
        return gpu;
    }

    [Fact]
    public void A_background_step_is_requeued_until_it_finishes_and_the_caller_waits()
    {
        using var gpu = Started();
        var calls = 0;
        var readyAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 20;

        var sw = Stopwatch.StartNew();
        using (GpuContext.BeginBackground())
        {
            gpu.InvokeStep(() =>
            {
                calls++;
                return Stopwatch.GetTimestamp() >= readyAt;
            });
        }

        Assert.True(sw.ElapsedMilliseconds >= 40);
        Assert.True(calls > 2);
    }

    [Fact]
    public void A_live_step_that_is_not_ready_returns_at_once()
    {
        using var gpu = Started();
        var calls = 0;

        var sw = Stopwatch.StartNew();
        gpu.InvokeStep(() =>
        {
            calls++;
            return false;
        });

        Assert.Equal(1, calls);
        Assert.True(sw.ElapsedMilliseconds < 1000);
    }

    [Fact]
    public async Task Live_jobs_stay_prompt_while_many_background_compiles_are_pending()
    {
        using var gpu = Started();
        const int Programs = 123;
        var gate = new CompileGate(GpuContext.MaxBackgroundCompiles);
        var maxInFlight = 0;
        var compileTicks = Stopwatch.Frequency / 30;

        // Each "program" takes a gate slot, then is ready ~33ms later without holding the thread.
        var thumbs = new List<Task>();
        for (var i = 0; i < Programs; i++)
        {
            thumbs.Add(Task.Run(() =>
            {
                long readyAt = 0;
                var held = false;
                using var bg = GpuContext.BeginBackground();
                gpu.InvokeStep(() =>
                {
                    if (!held)
                    {
                        if (!gate.TryEnter())
                        {
                            return false;
                        }
                        held = true;
                        maxInFlight = Math.Max(maxInFlight, gate.InFlight);
                        readyAt = Stopwatch.GetTimestamp() + compileTicks;
                    }
                    if (Stopwatch.GetTimestamp() < readyAt)
                    {
                        return false;
                    }
                    gate.Exit();
                    return true;
                });
            }));
        }

        var worst = 0.0;
        var live = Task.Run(() =>
        {
            var until = Stopwatch.StartNew();
            while (!Task.WhenAll(thumbs).IsCompleted && until.Elapsed < TimeSpan.FromSeconds(60))
            {
                var t = Stopwatch.GetTimestamp();
                gpu.Invoke(() => { });
                worst = Math.Max(worst, (Stopwatch.GetTimestamp() - t) * 1000.0 / Stopwatch.Frequency);
                Thread.Sleep(5);
            }
        });

        await Task.WhenAll(thumbs);
        await live;

        Assert.True(maxInFlight <= GpuContext.MaxBackgroundCompiles, $"in flight {maxInFlight}");
        // A live job waits behind at most one short poll, never behind a compile.
        Assert.True(worst < 30, $"live max wait {worst:0.0}ms");
    }

    [Fact]
    public void A_loss_releases_a_caller_parked_on_a_pending_step()
    {
        using var gpu = Started();
        var call = Task.Run(() =>
        {
            using var bg = GpuContext.BeginBackground();
            gpu.InvokeStep(() => false);
        });
        Thread.Sleep(50);

        gpu.ReportContextLost("test");

        Assert.True(call.Wait(TimeSpan.FromSeconds(5)));
    }
}
