using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Helper;

internal static class TaskObserve
{
    /// <summary>
    /// For one-way helper commands whose task is discarded: a helper that died
    /// mid-write faults it with a pipe error, and its reconnect re-syncs state,
    /// so the fault is read here instead of surfacing as an unobserved exception.
    /// Awaited callers keep the throw.
    /// </summary>
    public static void ObserveFaults(this Task task) =>
        task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
}
