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
        var watcher = new ConflictWatcher(new MultiplexHub(), null, new FeatureGates(store), displayInUse, () => processes) { HandlerCacheMs = 0 };
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
        watcher.GetConflicts();
        var first = watcher.FilterEpoch;
        Assert.Equal(first, watcher.FilterEpoch);

        store.Update(s => s.Features.Lighting = false);

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

public class ConflictHandlerFilterTests
{
    [Fact]
    public void IcueWithLightingOff_IsListedOnlyWhileACorsairLinkHandlerIsInUse()
    {
        var inUse = false;
        var store = new InMemoryConfigStore();
        store.Update(s => s.Features.Lighting = false);
        var processes = new Dictionary<string, List<(int Pid, string? Path)>>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["iCUE"] = new() { (100, null) },
        };
        var watcher = new ConflictWatcher(new MultiplexHub(), null, new FeatureGates(store), null, () => processes, h => inUse && h == "corsair")
        {
            HandlerCacheMs = 0,
        };

        Assert.Empty(watcher.GetConflicts());

        inUse = true;

        Assert.Single(watcher.GetConflicts());
    }

    [Fact]
    public void ARevealedAppIsReportedAsAlreadyRunning_ButARealLaunchIsNot()
    {
        var inUse = false;
        var store = new InMemoryConfigStore();
        store.Update(s => s.Features.Lighting = false);
        var processes = new Dictionary<string, List<(int Pid, string? Path)>>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["iCUE"] = new() { (100, null) },
        };
        var watcher = new ConflictWatcher(new MultiplexHub(), null, new FeatureGates(store), null, () => processes, h => inUse && h == "corsair")
        {
            HandlerCacheMs = 0,
        };
        watcher.GetConflicts();
        watcher.PillarsChanged();

        inUse = true;
        processes["FanControl"] = new() { (200, null) };
        watcher.PillarsChanged();
        var list = watcher.GetConflicts(out _, out var revealed);

        Assert.Contains("icue", revealed);
        Assert.DoesNotContain("fan-control", revealed);
        Assert.Equal(2, list.Count);
    }
}

public class ConflictRevealedTests
{
    [Fact]
    public void AnAppAlreadyVisibleBeforeTheChange_IsNotReportedAsRevealed()
    {
        var corsair = false;
        var store = new InMemoryConfigStore();
        store.Update(s => s.Features.Lighting = false);
        var processes = new Dictionary<string, List<(int Pid, string? Path)>>(System.StringComparer.OrdinalIgnoreCase);
        var watcher = new ConflictWatcher(new MultiplexHub(), null, new FeatureGates(store), null, () => processes, h => corsair && h == "corsair")
        {
            HandlerCacheMs = 0,
        };
        watcher.GetConflicts();

        // FanControl launches and is read while visible; only then does a Corsair hub appear and reveal iCUE.
        processes["FanControl"] = new() { (200, null) };
        processes["iCUE"] = new() { (100, null) };
        watcher.PillarsChanged();
        var visibleBefore = watcher.GetConflicts();
        Assert.Equal(new[] { "fan-control" }, visibleBefore.Select(c => c.Id));

        corsair = true;
        var list = watcher.GetConflicts(out _, out var revealed);

        Assert.Equal(2, list.Count);
        Assert.Equal(new[] { "icue" }, revealed);
    }

    [Fact]
    public void AKeyChangeThatLeavesTheVisibleSetAlone_DoesNotBumpTheEpoch()
    {
        var kraken = false;
        var store = new InMemoryConfigStore();
        store.Update(s => s.Features.Lighting = false);
        var processes = new Dictionary<string, List<(int Pid, string? Path)>>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["FanControl"] = new() { (200, null) },
        };
        var watcher = new ConflictWatcher(new MultiplexHub(), null, new FeatureGates(store), null, () => processes, h => kraken && h == "nzxt-kraken")
        {
            HandlerCacheMs = 0,
        };
        watcher.GetConflicts(out var before, out _);

        kraken = true;
        watcher.GetConflicts(out var after, out _);

        Assert.Equal(before, after);
    }
}

public class ConflictDisplayEpochTests
{
    private static Dictionary<string, List<(int Pid, string? Path)>> Cam() =>
        new(System.StringComparer.OrdinalIgnoreCase) { ["NZXT CAM"] = new() { (100, null) } };

    [Fact]
    public void ADisplayStartingToBeDriven_BumpsTheEpochAndRevealsTheAppTogether()
    {
        var driving = false;
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Features.Lighting = false;
            s.Features.Cooling = false;
        });
        var watcher = new ConflictWatcher(new MultiplexHub(), null, new FeatureGates(store), h => driving && h == "nzxt-kraken", Cam) { HandlerCacheMs = 0 };
        var hidden = watcher.GetConflicts(out var epochBefore);
        Assert.Empty(hidden);

        driving = true;
        var shown = watcher.GetConflicts(out var epochAfter);

        Assert.Single(shown);
        Assert.NotEqual(epochBefore, epochAfter);
    }

    [Fact]
    public void ADisplayComingIntoUse_IsNotToastedAsALaunch()
    {
        var driving = false;
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Features.Lighting = false;
            s.Features.Cooling = false;
            s.Ui.ConflictAutoKillExclusions.Clear();
        });
        var watcher = new ConflictWatcher(new MultiplexHub(), null, new FeatureGates(store), h => driving && h == "nzxt-kraken", Cam) { HandlerCacheMs = 0 };
        var notifier = new ConflictLaunchNotifier(watcher, store);
        var launched = new List<string>();
        notifier.AppLaunched += a => launched.Add(a.Id);

        notifier.Tick(0);
        driving = true;
        notifier.Tick(6_000);

        Assert.Empty(launched);
    }

    [Fact]
    public void ARealLaunch_IsStillToasted()
    {
        var store = new InMemoryConfigStore();
        store.Update(s => s.Ui.ConflictAutoKillExclusions.Clear());
        var processes = new Dictionary<string, List<(int Pid, string? Path)>>(System.StringComparer.OrdinalIgnoreCase);
        var watcher = new ConflictWatcher(new MultiplexHub(), null, new FeatureGates(store), null, () => processes) { HandlerCacheMs = 0 };
        var notifier = new ConflictLaunchNotifier(watcher, store);
        var launched = new List<string>();
        notifier.AppLaunched += a => launched.Add(a.Id);

        notifier.Tick(0);
        processes["SignalRgb"] = new() { (100, null) };
        watcher.PillarsChanged();
        notifier.Tick(6_000);

        Assert.Equal(new[] { "signalrgb" }, launched);
    }
}
