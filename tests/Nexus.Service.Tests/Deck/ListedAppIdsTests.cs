using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Activity;
using Nexus.Service.Deck;
using Nexus.Service.Models.Activity;
using Xunit;

namespace Nexus.Service.Tests.Deck;

public sealed class ListedAppIdsTests
{
    private sealed class ScriptedProvider : IShortcutsProvider
    {
        public readonly ManualResetEventSlim Gate = new(false);
        public int GetAllCalls;
        public int EmptyCalls;

        public IReadOnlyList<Shortcut> GetAll()
        {
            Gate.Wait(5000);
            if (Interlocked.Increment(ref GetAllCalls) <= EmptyCalls)
            {
                return [];
            }
            return [new Shortcut { Id = "steam://rungameid/1", Name = "Game One" }];
        }

        public Shortcut? GetById(string targetId) => null;
        public byte[]? GetIcon(string targetId) => null;
        public bool Launch(string targetId) => false;
        public string ResolveProcessName(string targetId) => "";
    }

    [Fact]
    public async Task ColdSet_FailsClosedWithoutBlocking_ThenListsIdOnly()
    {
        var provider = new ScriptedProvider();
        using var ids = new ListedAppIds(provider, TimeSpan.FromHours(1), TimeSpan.FromHours(1));

        var cold = ids.Snapshot();
        Assert.False(cold.IsLoaded);
        Assert.False(cold.Contains("steam://rungameid/1"));

        provider.Gate.Set();
        await ids.Loaded.WaitAsync(TimeSpan.FromSeconds(5));

        var warm = ids.Snapshot();
        Assert.True(warm.IsLoaded);
        Assert.True(warm.Contains("steam://rungameid/1"));
        Assert.True(warm.Contains("STEAM://rungameid/1"));
        Assert.False(warm.Contains("Game One"));
        Assert.False(cold.IsLoaded);
    }

    [Fact]
    public async Task EmptyFirstLoad_IsRetriedInTheBackgroundUntilItSucceeds()
    {
        var provider = new ScriptedProvider { EmptyCalls = 2 };
        provider.Gate.Set();
        using var ids = new ListedAppIds(provider, TimeSpan.FromHours(1), TimeSpan.FromMilliseconds(1));

        await ids.Loaded.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(ids.Snapshot().Contains("steam://rungameid/1"));
        Assert.Equal(3, provider.GetAllCalls);
    }
}
