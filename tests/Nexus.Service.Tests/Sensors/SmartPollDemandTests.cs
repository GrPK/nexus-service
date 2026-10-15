using Nexus.Service.Lifecycle;
using Nexus.Service.Persistence;
using Nexus.Service.Sensors;
using Xunit;

namespace Nexus.Service.Tests.Sensors;

public class SmartPollDemandTests
{
    private static (InMemoryConfigStore Store, FeatureGates Gates) Build()
    {
        var store = new InMemoryConfigStore();
        return (store, new FeatureGates(store));
    }

    private static void Pillars(InMemoryConfigStore store, bool monitoring, bool diagnostics, bool cooling) =>
        store.Update(s =>
        {
            s.Features.Monitoring = monitoring;
            s.Features.Diagnostics = diagnostics;
            s.Features.Cooling = cooling;
        });

    private static void DriveCurve(InMemoryConfigStore store, string input) =>
        store.Update(s => s.Cooling.Curves.Add(new CurveDocument
        {
            Id = "c",
            Name = "c",
            Type = "Flat",
            Input = new CurveInputDocument { Id = input, Type = "Temperature" },
        }));

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void ActiveFollowsMonitoringAndDiagnostics(bool monitoring, bool diagnostics, bool active)
    {
        var (store, gates) = Build();
        Pillars(store, monitoring, diagnostics, cooling: false);

        Assert.Equal(active, SmartPollDemand.IsActive(gates, store.Load()));
        Assert.Equal(active, SmartPollDemand.IsNeededFor(gates, store.Load(), "/nvme/0"));
    }

    [Fact]
    public void ADriveTemperatureCurve_KeepsThatDriveReadingWhileCoolingIsOn()
    {
        var (store, gates) = Build();
        Pillars(store, false, false, cooling: true);
        DriveCurve(store, "/nvme/0/temperature/0");

        Assert.True(SmartPollDemand.IsActive(gates, store.Load()));
        Assert.True(SmartPollDemand.IsNeededFor(gates, store.Load(), "/nvme/0"));
        Assert.False(SmartPollDemand.IsNeededFor(gates, store.Load(), "/nvme/1"));
    }

    [Fact]
    public void ADriveCurve_DoesNotCountWhileCoolingIsOff_OrForACpuCurve()
    {
        var (store, gates) = Build();
        Pillars(store, false, false, cooling: false);
        DriveCurve(store, "/nvme/0/temperature/0");
        Assert.False(SmartPollDemand.IsActive(gates, store.Load()));

        var (cpuStore, cpuGates) = Build();
        Pillars(cpuStore, false, false, cooling: true);
        DriveCurve(cpuStore, "/intelcpu/0/temperature/0");
        Assert.False(SmartPollDemand.IsActive(cpuGates, cpuStore.Load()));
    }

    [Fact]
    public void SwitchingAPillarBackOn_IsSeenLive()
    {
        var (store, gates) = Build();
        Pillars(store, false, false, cooling: false);
        Assert.False(SmartPollDemand.IsNeededFor(gates, store.Load(), "/hdd/0"));

        store.Update(s => s.Features.Diagnostics = true);

        Assert.True(SmartPollDemand.IsNeededFor(gates, store.Load(), "/hdd/0"));
    }
}
