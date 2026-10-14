using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Activity;

namespace Nexus.Service.Deck;

/// <summary>
/// In-process set of the ids the shortcuts provider lists, refreshed on a
/// background thread so DeckLayoutPolicy never waits on the helper RPC. A cold
/// or empty set reports nothing as listed, which makes direct launchApp targets
/// privileged (fail closed). Matches Id only, never display Name.
/// </summary>
public sealed class ListedAppIds
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan EmptyRetry = TimeSpan.FromSeconds(15);

    private readonly IShortcutsProvider _shortcuts;
    private HashSet<string>? _ids;
    private long _expiryTicks;
    private int _refreshing;

    public ListedAppIds(IShortcutsProvider shortcuts)
    {
        _shortcuts = shortcuts;
        Refresh();
    }

    /// <summary>A lookup frozen to the current set, so one request judges stored and incoming keys against the same data.</summary>
    public Func<string, bool> Snapshot()
    {
        if (Volatile.Read(ref _expiryTicks) <= DateTime.UtcNow.Ticks)
        {
            Refresh();
        }
        var ids = Volatile.Read(ref _ids);
        return id => ids is not null && ids.Contains(id);
    }

    private void Refresh()
    {
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0)
        {
            return;
        }
        _ = Task.Run(() =>
        {
            var ttl = EmptyRetry;
            try
            {
                var ids = new HashSet<string>(
                    _shortcuts.GetAll().Select(s => s.Id), StringComparer.OrdinalIgnoreCase);
                if (ids.Count > 0)
                {
                    Volatile.Write(ref _ids, ids);
                    ttl = Ttl;
                }
            }
            catch
            {
            }
            finally
            {
                Volatile.Write(ref _expiryTicks, (DateTime.UtcNow + ttl).Ticks);
                Volatile.Write(ref _refreshing, 0);
            }
        });
    }
}
