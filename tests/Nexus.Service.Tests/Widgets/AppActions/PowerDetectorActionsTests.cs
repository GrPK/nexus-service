using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Persistence;
using Nexus.Service.Platform.Power;
using Nexus.Service.Sensors;
using Nexus.Service.Sensors.Astral;
using Nexus.Service.Widgets;
using Nexus.Service.Widgets.AppActions;
using Xunit;

namespace Nexus.Service.Tests.Widgets.AppActions;

public class PowerDetectorActionsTests
{
    // Only the pin currents matter here; the rest are never called.
    private sealed class PinSensors : ISensorProvider
    {
        public List<GpuPinCurrents> Cards { get; } = new();
        public IReadOnlyList<GpuPinCurrents> ReadGpuPinCurrents() => Cards;

        public IReadOnlyList<GpuReadout> GetGpus() => Array.Empty<GpuReadout>();
        public string GetCpuModel() => throw new NotSupportedException();
        public IReadOnlyList<HardwareSensor> GetCpuSensors() => throw new NotSupportedException();
        public (bool Healthy, float DistanceToTJMax) GetCpuHealth() => throw new NotSupportedException();
        public IReadOnlyList<string> GetGpuModels() => throw new NotSupportedException();
        public IReadOnlyList<HardwareSensor> GetGpuSensors() => throw new NotSupportedException();
        public IReadOnlyList<HardwareSensor> GetMemorySensors() => throw new NotSupportedException();
        public string GetMemoryTotalFormatted() => throw new NotSupportedException();
        public string GetRamBrandModel() => throw new NotSupportedException();
        public IReadOnlyDictionary<string, StorageComponent> GetStorageComponents(bool includeSmart = true) => throw new NotSupportedException();
        public IReadOnlyList<string> GetStoragePartitions() => throw new NotSupportedException();
        public IReadOnlyList<StorageDriveInfo> GetStorageInfo() => throw new NotSupportedException();
        public string GetStorageBrandModel() => throw new NotSupportedException();
        public IReadOnlyList<HardwareSensor> GetMotherboardSensors() => throw new NotSupportedException();
        public string GetMotherboardModel() => throw new NotSupportedException();
        public SensorExtras GetSensorExtras() => throw new NotSupportedException();
        public string GetOsVersion() => throw new NotSupportedException();
        public void SetPollingRate(int pollingRate) { }
        public Task ReadyAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoPower : ISystemPowerProvider
    {
        public bool Lock() => throw new NotSupportedException();
        public bool Sleep() => throw new NotSupportedException();
        public bool Shutdown() => throw new NotSupportedException();
        public bool Restart() => throw new NotSupportedException();
        public bool Logout() => throw new NotSupportedException();
    }

    private static AppActionRegistry.ActionHandler Handler(string action)
    {
        var registry = new AppActionRegistry();
        PowerDetectorActions.RegisterAll(registry);
        Assert.True(registry.TryGet(action, out var handler));
        return handler;
    }

    private static (IServiceProvider Services, InMemoryConfigStore Store, AstralPowerDetectorService Detector) WithDetector()
    {
        var store = new InMemoryConfigStore();
        var sensors = new PinSensors();
        sensors.Cards.Add(new GpuPinCurrents("/gpu-nvidia/0", "ASUS ROG Astral RTX 5090", new[] { 7.1f, 7.2f, 7.3f, 7.4f, 7.5f, 7.6f }, 0));
        var detector = new AstralPowerDetectorService(sensors, store, new NoPower());
        detector.ShutdownSink = _ => throw new InvalidOperationException("no shutdown in tests");
        var services = new ServiceCollection()
            .AddSingleton<IConfigStore>(store)
            .AddSingleton(detector)
            .BuildServiceProvider();
        return (services, store, detector);
    }

    private static Dictionary<string, JsonElement> Args(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public async Task State_without_a_detector_reports_unsupported()
    {
        var services = new ServiceCollection().BuildServiceProvider();

        var result = await Handler("powerdetector.state")(services, null, CancellationToken.None);

        Assert.False(result!.Value.GetProperty("supported").GetBoolean());
        Assert.False(result.Value.GetProperty("settings").GetProperty("overcurrentShutdown").GetBoolean());
    }

    [Fact]
    public async Task State_lists_the_pins_limits_and_settings()
    {
        var (services, _, detector) = WithDetector();
        detector.Tick();

        var result = await Handler("powerdetector.state")(services, null, CancellationToken.None);

        var state = result!.Value;
        Assert.True(state.GetProperty("supported").GetBoolean());
        Assert.Equal("normal", state.GetProperty("level").GetString());
        Assert.Equal(12.5f, state.GetProperty("overcurrentAmps").GetSingle());
        Assert.Equal(17f, state.GetProperty("criticalAmps").GetSingle());
        var gpu = state.GetProperty("gpus")[0];
        Assert.Equal("ASUS ROG Astral RTX 5090", gpu.GetProperty("name").GetString());
        Assert.Equal(6, gpu.GetProperty("pinAmps").GetArrayLength());
        Assert.True(state.GetProperty("settings").GetProperty("trayAlerts").GetBoolean());
        Assert.False(state.TryGetProperty("countdown", out var countdown) && countdown.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task SetConfig_changes_only_what_it_names()
    {
        var (services, store, _) = WithDetector();

        var result = await Handler("powerdetector.setConfig")(services,
            Args("""{"criticalShutdown":true,"countdownSeconds":120,"trayAlerts":"yes"}"""), CancellationToken.None);

        var saved = store.Load().AstralPowerDetector;
        Assert.True(saved.CriticalShutdown);
        Assert.Equal(120, saved.CountdownSeconds);
        Assert.True(saved.TrayAlerts);
        Assert.False(saved.OvercurrentShutdown);
        Assert.True(result!.Value.GetProperty("settings").GetProperty("criticalShutdown").GetBoolean());
    }

    [Fact]
    public async Task Cancel_and_shutdown_now_do_nothing_without_a_countdown()
    {
        var (services, _, _) = WithDetector();

        var cancel = await Handler("powerdetector.cancel")(services, null, CancellationToken.None);
        var now = await Handler("powerdetector.shutdownNow")(services, null, CancellationToken.None);

        Assert.False(cancel!.Value.GetProperty("ok").GetBoolean());
        Assert.False(now!.Value.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void Every_action_is_listed()
    {
        Assert.Equal(new[] { "powerdetector.state", "powerdetector.setConfig", "powerdetector.cancel", "powerdetector.shutdownNow" }, PowerDetectorActions.AllActions);
    }
}
