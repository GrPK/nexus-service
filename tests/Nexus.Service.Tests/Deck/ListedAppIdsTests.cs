using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Nexus.Service.Activity;
using Nexus.Service.Deck;
using Nexus.Service.Models.Activity;
using Xunit;

namespace Nexus.Service.Tests.Deck;

public sealed class ListedAppIdsTests
{
    private sealed class SlowProvider : IShortcutsProvider
    {
        public readonly ManualResetEventSlim Release = new(false);
        public int GetAllCalls;

        public IReadOnlyList<Shortcut> GetAll()
        {
            Interlocked.Increment(ref GetAllCalls);
            Release.Wait(5000);
            return [new Shortcut { Id = "steam://rungameid/1", Name = "Game One" }];
        }

        public Shortcut? GetById(string targetId) => null;
        public byte[]? GetIcon(string targetId) => null;
        public bool Launch(string targetId) => false;
        public string ResolveProcessName(string targetId) => "";
    }

    [Fact]
    public void ColdSet_FailsClosedWithoutBlocking_ThenListsIdOnly()
    {
        var provider = new SlowProvider();
        var ids = new ListedAppIds(provider);

        var sw = Stopwatch.StartNew();
        var cold = ids.Snapshot();
        Assert.True(sw.ElapsedMilliseconds < 1000);
        Assert.False(cold("steam://rungameid/1"));
        ids.Snapshot();
        ids.Snapshot();

        provider.Release.Set();
        var deadline = Stopwatch.StartNew();
        while (!ids.Snapshot()("steam://rungameid/1") && deadline.ElapsedMilliseconds < 5000)
        {
            Thread.Sleep(10);
        }

        var warm = ids.Snapshot();
        Assert.True(warm("steam://rungameid/1"));
        Assert.True(warm("STEAM://rungameid/1"));
        Assert.False(warm("Game One"));
        Assert.Equal(1, provider.GetAllCalls);
    }
}
