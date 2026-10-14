using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Activity;
using Nexus.Service.Platform;

namespace Nexus.Service.Deck;

/// <summary>A lookup frozen to one loaded set. Converts to Func so one request judges stored and incoming keys against the same data.</summary>
public sealed class ListedAppSnapshot
{
    private readonly HashSet<string>? _ids;

    internal ListedAppSnapshot(HashSet<string>? ids)
    {
        _ids = ids;
    }

    /// <summary>A cold snapshot (no successful load yet) lists nothing, so direct launchApp targets are privileged.</summary>
    public bool Contains(string id) => _ids is not null && _ids.Contains(id);

    public static implicit operator Func<string, bool>(ListedAppSnapshot snapshot) => snapshot.Contains;
}

/// <summary>
/// In-process set of the ids the shortcuts provider lists, kept fresh by one
/// background loop so DeckLayoutPolicy never waits on the helper RPC. A cold
/// set lists nothing, which makes direct launchApp targets privileged (fail
/// closed). Matches Id only, never display Name.
/// </summary>
public sealed class ListedAppIds : IDisposable
{
    // Shorter than the provider's own app-list cache so the list stays warm.
    private static readonly TimeSpan DefaultRefresh = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan DefaultRetry = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxRetry = TimeSpan.FromSeconds(60);

    private readonly IShortcutsProvider _shortcuts;
    private readonly TimeSpan _refresh;
    private readonly TimeSpan _retry;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private HashSet<string>? _ids;

    public ListedAppIds(IShortcutsProvider shortcuts) : this(shortcuts, DefaultRefresh, DefaultRetry)
    {
    }

    internal ListedAppIds(IShortcutsProvider shortcuts, TimeSpan refresh, TimeSpan retry)
    {
        _shortcuts = shortcuts;
        _refresh = refresh;
        _retry = retry;
        _ = Task.Run(() => RunAsync(_stop.Token));
    }

    /// <summary>Completes after the first successful load.</summary>
    internal Task Loaded => _loaded.Task;

    public ListedAppSnapshot Snapshot() => new(Volatile.Read(ref _ids));

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var retry = _retry;
        var failing = false;
        while (!ct.IsCancellationRequested)
        {
            var delay = _refresh;
            try
            {
                var ids = new HashSet<string>(_shortcuts.GetAll().Select(s => s.Id), StringComparer.OrdinalIgnoreCase);
                if (ids.Count > 0)
                {
                    Volatile.Write(ref _ids, ids);
                    _loaded.TrySetResult();
                    retry = _retry;
                    failing = false;
                }
                else
                {
                    delay = retry;
                    retry = TimeSpan.FromTicks(Math.Min(retry.Ticks * 2, MaxRetry.Ticks));
                }
            }
            catch (Exception ex)
            {
                if (!failing)
                {
                    ServiceLog.Warn($"[deck] listed-app refresh failed, retrying with backoff: {ex.Message}");
                    failing = true;
                }
                delay = retry;
                retry = TimeSpan.FromTicks(Math.Min(retry.Ticks * 2, MaxRetry.Ticks));
            }

            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
