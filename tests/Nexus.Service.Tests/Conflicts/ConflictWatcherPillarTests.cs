using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Conflicts;
using Nexus.Service.Lifecycle;
using Nexus.Service.Sockets;
using Xunit;

namespace Nexus.Service.Tests.Conflicts;

public class ConflictWatcherPillarTests
{
    private static Dictionary<string, List<(int Pid, string? Path)>> Running(params string[] names) =>
        names.ToDictionary(n => n, n => new List<(int, string?)> { (100, null) }, System.StringComparer.OrdinalIgnoreCase);

    private static (ConflictWatcher Watcher, InMemoryConfigStore Store) Build(
        Dictionary<string, List<(int Pid, string? Path)>> processes, System.Func<string, bool>? displayInUse = null)
    {
        var store = new InMemoryConfigStore();
        var watcher = new ConflictWatcher(new MultiplexHub(), null, new FeatureGates(store), displayInUse, () => processes);
        return (watcher, store);
    }

    [Fact]
    public void ALightingOnlyApp_IsHiddenWhileLightingIsOff_AndReturnsLiveWithoutARescan()
    {
        var (watcher, store) = Build(Running("SignalRgb"));
        store.Update(s => s.Features.Lighting = false);

        Assert.Empty(watcher.GetConflicts());

        store.Update(s => s.Features.Lighting = true);

        Assert.Equal(new[] { "signalrgb" }, watcher.GetConflicts().Select(c => c.Id));
    }

    [Fact]
    public void AnUndecidedInstall_HidesLightingAndCoolingApps_UntilTheFeatureStepCompletes()
    {
        var (watcher, store) = Build(Running("SignalRgb", "FanControl"));
        store.Update(s => s.FeaturesOnboardingCompleted = false);
        Assert.Empty(watcher.GetConflicts());

        store.Update(s => s.FeaturesOnboardingCompleted = true);
        watcher.PillarsChanged();

        Assert.Equal(2, watcher.GetConflicts().Count);
    }

    [Fact]
    public void IsAppRunning_IgnoresThePillarFilter()
    {
        var (watcher, store) = Build(Running("SignalRgb"));
        store.Update(s => s.Features.Lighting = false);

        Assert.Empty(watcher.GetConflicts());
        Assert.True(watcher.IsAppRunning("signalrgb"));
    }

    [Fact]
    public void FilterEpoch_ChangesWhenThePillarsDo()
    {
        var (watcher, store) = Build(Running("SignalRgb"));
        var first = watcher.FilterEpoch;
        Assert.Equal(first, watcher.FilterEpoch);

        store.Update(s => s.Features.Cooling = false);

        Assert.NotEqual(first, watcher.FilterEpoch);
    }

    [Fact]
    public void ADisplayApp_CompetesWithBothPillarsOff_OnlyWhileNexusDrivesItsDisplay()
    {
        var driving = true;
        var (watcher, store) = Build(Running("NZXT CAM"), h => driving && h == "nzxt-kraken");
        store.Update(s =>
        {
            s.Features.Lighting = false;
            s.Features.Cooling = false;
        });

        Assert.Single(watcher.GetConflicts());

        driving = false;

        Assert.Empty(watcher.GetConflicts());
    }
}
