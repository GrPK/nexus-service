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
                return Stopwatch.GetTimestamp() >= readyAt ? StepState.Done : StepState.Poll;
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
            return StepState.Poll;
        });

        Assert.Equal(1, calls);
        Assert.True(sw.ElapsedMilliseconds < 1000);
    }

    [Fact]
    public async Task Live_jobs_stay_prompt_while_many_background_compiles_are_pending()
    {
        using var gpu = Started();
        const int Programs = 123;
        var gate = gpu.BackgroundCompiles;
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
                            return StepState.WaitSlot;
                        }
                        held = true;
                        maxInFlight = Math.Max(maxInFlight, gate.InFlight);
                        readyAt = Stopwatch.GetTimestamp() + compileTicks;
                    }
                    if (Stopwatch.GetTimestamp() < readyAt)
                    {
                        return StepState.Poll;
                    }
                    gpu.ReleaseCompileSlot();
                    return StepState.Done;
                });
            }));
        }

        var worst = 0.0;
        // Queue wait is measured on the GL thread, so thread-pool pressure from the blocked callers cannot inflate it.
        var live = Task.Factory.StartNew(() =>
        {
            var until = Stopwatch.StartNew();
            while (!Task.WhenAll(thumbs).IsCompleted && until.Elapsed < TimeSpan.FromSeconds(60))
            {
                var t = Stopwatch.GetTimestamp();
                long ranAt = 0;
                gpu.Invoke(() => ranAt = Stopwatch.GetTimestamp());
                worst = Math.Max(worst, (ranAt - t) * 1000.0 / Stopwatch.Frequency);
                Thread.Sleep(5);
            }
        }, TaskCreationOptions.LongRunning);

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
            gpu.InvokeStep(() => StepState.Poll);
        });
        Thread.Sleep(50);

        gpu.ReportContextLost("test");

        Assert.True(call.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void A_step_waiting_for_a_slot_parks_instead_of_polling_and_wakes_on_release()
    {
        using var gpu = Started();
        // Fill every slot from the GL thread.
        gpu.Invoke(() =>
        {
            while (gpu.BackgroundCompiles.TryEnter())
            {
            }
        });
        var calls = 0;
        var call = Task.Run(() =>
        {
            using var bg = GpuContext.BeginBackground();
            gpu.InvokeStep(() =>
            {
                calls++;
                if (!gpu.BackgroundCompiles.TryEnter())
                {
                    return StepState.WaitSlot;
                }
                gpu.ReleaseCompileSlot();
                return StepState.Done;
            });
        });
        Thread.Sleep(150);

        Assert.False(call.IsCompleted);
        Assert.Equal(1, calls);

        gpu.Invoke(() => gpu.ReleaseCompileSlot());

        Assert.True(call.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void A_loss_releases_callers_parked_for_a_slot()
    {
        using var gpu = Started();
        gpu.Invoke(() =>
        {
            while (gpu.BackgroundCompiles.TryEnter())
            {
            }
        });
        var call = Task.Run(() =>
        {
            using var bg = GpuContext.BeginBackground();
            gpu.InvokeStep(() => StepState.WaitSlot);
        });
        Thread.Sleep(50);

        gpu.ReportContextLost("test");

        Assert.True(call.Wait(TimeSpan.FromSeconds(5)));
    }
}
