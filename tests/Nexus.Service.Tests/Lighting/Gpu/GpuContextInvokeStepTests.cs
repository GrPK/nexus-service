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

    private static bool Eventually(Func<bool> cond)
    {
        var until = Stopwatch.StartNew();
        while (until.Elapsed < TimeSpan.FromSeconds(60))
        {
            if (cond())
            {
                return true;
            }
            Thread.Sleep(2);
        }
        return cond();
    }

    [Fact]
    public async Task Live_jobs_never_queue_behind_the_background_compile_backlog()
    {
        using var gpu = Started();
        const int Programs = 123;
        var maxInFlight = 0;
        long backgroundRuns = 0;
        var compileTicks = Stopwatch.Frequency / 30;

        // Each "program" takes a slot, then becomes ready a little later without holding the thread.
        var thumbs = new List<Task>();
        for (var i = 0; i < Programs; i++)
        {
            thumbs.Add(Task.Factory.StartNew(() =>
            {
                long readyAt = 0;
                var held = false;
                using var bg = GpuContext.BeginBackground();
                gpu.InvokeStep(() =>
                {
                    Interlocked.Increment(ref backgroundRuns);
                    if (!held)
                    {
                        if (!gpu.TryAcquireCompileSlot())
                        {
                            return StepState.WaitSlot;
                        }
                        held = true;
                        maxInFlight = Math.Max(maxInFlight, gpu.BackgroundCompiles.InFlight);
                        readyAt = Stopwatch.GetTimestamp() + compileTicks;
                    }
                    if (Stopwatch.GetTimestamp() < readyAt)
                    {
                        return StepState.Poll;
                    }
                    gpu.ReleaseCompileSlot();
                    return StepState.Done;
                });
            }, TaskCreationOptions.LongRunning));
        }

        // How many background step runs got ahead of each live job between its submission and its run. Counted, not
        // timed: a live job waits behind at most the one job already running, whatever the machine's load.
        var deltas = new List<long>();
        var live = Task.Factory.StartNew(() =>
        {
            while (!Task.WhenAll(thumbs).IsCompleted)
            {
                var before = Interlocked.Read(ref backgroundRuns);
                long during = 0;
                gpu.Invoke(() => during = Interlocked.Read(ref backgroundRuns));
                deltas.Add(during - before);
                Thread.Sleep(3);
            }
        }, TaskCreationOptions.LongRunning);

        await Task.WhenAll(thumbs);
        await live;

        Assert.True(maxInFlight <= GpuContext.MaxBackgroundCompiles, $"in flight {maxInFlight}");
        Assert.NotEmpty(deltas);
        // A few samples can see more when the sampling thread is descheduled between reading the count and queuing;
        // a live job stuck behind the backlog would see dozens on nearly every sample.
        var behind = deltas.Count(d => d > 2);
        Assert.True(behind * 50 <= deltas.Count, $"{behind} of {deltas.Count} live jobs ran behind more than one background job");
        Assert.True(deltas.Max() < 8, $"a live job ran behind {deltas.Max()} background jobs");
        // Measured on the GL thread, so thread-pool pressure on the callers does not count. Every step here returns at once.
        Assert.True(gpu.MaxLiveWaitMs < 40, $"live max wait {gpu.MaxLiveWaitMs:0.0}ms");
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
        var call = Task.Factory.StartNew(() =>
        {
            using var bg = GpuContext.BeginBackground();
            gpu.InvokeStep(() =>
            {
                Interlocked.Increment(ref calls);
                if (!gpu.TryAcquireCompileSlot())
                {
                    return StepState.WaitSlot;
                }
                gpu.ReleaseCompileSlot();
                return StepState.Done;
            });
        }, TaskCreationOptions.LongRunning);

        Assert.True(Eventually(() => gpu.ParkedWaiters == 1));
        // GL-thread round trips give a polling step every chance to run again; a parked one does not.
        for (var i = 0; i < 20; i++)
        {
            gpu.Invoke(() => { });
        }
        Assert.False(call.IsCompleted);
        Assert.Equal(1, Volatile.Read(ref calls));

        gpu.Invoke(() => gpu.ReleaseCompileSlot());

        Assert.True(call.Wait(TimeSpan.FromSeconds(60)));
        Assert.Equal(2, Volatile.Read(ref calls));
    }

    [Fact]
    public void A_released_slot_goes_to_the_longest_waiter_before_a_newcomer()
    {
        using var gpu = Started();
        gpu.Invoke(() =>
        {
            while (gpu.BackgroundCompiles.TryEnter())
            {
            }
        });
        var order = new List<string>();
        Task Waiter(string name) => Task.Factory.StartNew(() =>
        {
            using var bg = GpuContext.BeginBackground();
            gpu.InvokeStep(() =>
            {
                if (!gpu.TryAcquireCompileSlot())
                {
                    return StepState.WaitSlot;
                }
                lock (order)
                {
                    order.Add(name);
                }
                return StepState.Done;
            });
        }, TaskCreationOptions.LongRunning);

        var first = Waiter("first");
        Assert.True(Eventually(() => gpu.ParkedWaiters == 1));
        var second = Waiter("second");
        Assert.True(Eventually(() => gpu.ParkedWaiters == 2));
        int inFlightAfterRelease = -1;

        // The release and a newcomer's attempt run back to back on the GL thread: the slot is already spoken for.
        bool newcomerGotIt = true;
        gpu.Invoke(() =>
        {
            gpu.ReleaseCompileSlot();
            newcomerGotIt = gpu.BackgroundCompiles.TryEnter();
            inFlightAfterRelease = gpu.BackgroundCompiles.InFlight;
        });

        Assert.False(newcomerGotIt);
        Assert.Equal(GpuContext.MaxBackgroundCompiles, inFlightAfterRelease);
        Assert.True(first.Wait(TimeSpan.FromSeconds(60)));
        Assert.Equal(new[] { "first" }, order);

        gpu.Invoke(() => gpu.ReleaseCompileSlot());
        Assert.True(second.Wait(TimeSpan.FromSeconds(60)));
        Assert.Equal(new[] { "first", "second" }, order);
    }

    [Fact]
    public async Task A_background_step_that_blocks_the_gl_thread_shows_in_the_live_wait()
    {
        using var gpu = Started();
        using var blocking = new ManualResetEventSlim(false);
        var bg = Task.Factory.StartNew(() =>
        {
            using var b = GpuContext.BeginBackground();
            gpu.Invoke(() =>
            {
                blocking.Set();
                Thread.Sleep(120);
            });
        }, TaskCreationOptions.LongRunning);
        Assert.True(blocking.Wait(TimeSpan.FromSeconds(60)));

        gpu.Invoke(() => { });
        await bg;

        Assert.True(gpu.MaxLiveWaitMs > 40, $"live max wait {gpu.MaxLiveWaitMs:0.0}ms");
    }

    [Fact]
    public void A_loss_releases_a_caller_parked_on_a_pending_step()
    {
        using var gpu = Started();
        using var started = new ManualResetEventSlim(false);
        var call = Task.Run(() =>
        {
            using var bg = GpuContext.BeginBackground();
            gpu.InvokeStep(() =>
            {
                started.Set();
                return StepState.Poll;
            });
        });
        Assert.True(started.Wait(TimeSpan.FromSeconds(60)));

        gpu.ReportContextLost("test");

        Assert.True(call.Wait(TimeSpan.FromSeconds(5)));
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
        Assert.True(Eventually(() => gpu.ParkedWaiters == 1));

        gpu.ReportContextLost("test");

        Assert.True(call.Wait(TimeSpan.FromSeconds(60)));
    }
}
