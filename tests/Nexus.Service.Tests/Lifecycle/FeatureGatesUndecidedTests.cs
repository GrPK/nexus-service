using Nexus.Service.Lifecycle;
using Xunit;

namespace Nexus.Service.Tests.Lifecycle;

public class FeatureGatesUndecidedTests
{
    [Fact]
    public void Undecided_ReadsLightingAndCoolingOff_ButMonitoringAndDiagnosticsOn()
    {
        var store = new InMemoryConfigStore();
        store.Update(s => s.FeaturesOnboardingCompleted = false);
        var gates = new FeatureGates(store);

        Assert.False(gates.Lighting);
        Assert.False(gates.Cooling);
        Assert.True(gates.Monitoring);
        Assert.True(gates.Diagnostics);
    }

    [Fact]
    public void Decided_FollowsTheChosenFlags_Live()
    {
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.FeaturesOnboardingCompleted = false;
            s.Features.Cooling = false;
        });
        var gates = new FeatureGates(store);

        store.Update(s => s.FeaturesOnboardingCompleted = true);

        Assert.True(gates.Lighting);
        Assert.False(gates.Cooling);

        store.Update(s => s.Features.Cooling = true);

        Assert.True(gates.Cooling);
    }
}
