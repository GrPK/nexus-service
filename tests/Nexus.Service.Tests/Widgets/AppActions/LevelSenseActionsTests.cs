using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Sensors;
using Nexus.Service.Widgets;
using Nexus.Service.Widgets.AppActions;
using Xunit;

namespace Nexus.Service.Tests.Widgets.AppActions;

public class LevelSenseActionsTests
{
    // Only the Level Sense members matter here; the rest are never called.
    private sealed class LevelSenseSensors : ISensorProvider
    {
        public List<GpuLevelSenseState> States { get; } = new();
        public int RecalibrateCalls { get; private set; }

        public IReadOnlyList<string> RecalibrateGpuLevelSense(string? gpuId)
        {
            RecalibrateCalls++;
            return new[] { "/gpu-nvidia/0" };
        }

        public IReadOnlyList<GpuLevelSenseState> GetGpuLevelSenseStates() => States;

        public string GetCpuModel() => throw new NotSupportedException();
        public IReadOnlyList<HardwareSensor> GetCpuSensors() => throw new NotSupportedException();
        public (bool Healthy, float DistanceToTJMax) GetCpuHealth() => throw new NotSupportedException();
        public IReadOnlyList<string> GetGpuModels() => throw new NotSupportedException();
        public IReadOnlyList<HardwareSensor> GetGpuSensors() => throw new NotSupportedException();
        public IReadOnlyList<GpuReadout> GetGpus() => throw new NotSupportedException();
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

    private static AppActionRegistry.ActionHandler Handler(string action)
    {
        var registry = new AppActionRegistry();
        LevelSenseActions.RegisterAll(registry);
        Assert.True(registry.TryGet(action, out var handler));
        return handler;
    }

    private static IServiceProvider Services(LevelSenseSensors sensors) =>
        new ServiceCollection().AddSingleton<ISensorProvider>(sensors).BuildServiceProvider();

    [Fact]
    public async Task State_lists_each_card_with_its_calibration_time()
    {
        var sensors = new LevelSenseSensors();
        sensors.States.Add(new GpuLevelSenseState("/gpu-nvidia/0", "ASUS ROG Astral RTX 5090", false, 1_760_000_000_000));

        var result = await Handler("levelsense.state")(Services(sensors), null, CancellationToken.None);

        var gpu = result!.Value.GetProperty("gpus")[0];
        Assert.Equal("/gpu-nvidia/0", gpu.GetProperty("gpuId").GetString());
        Assert.Equal("ASUS ROG Astral RTX 5090", gpu.GetProperty("name").GetString());
        Assert.False(gpu.GetProperty("calibrating").GetBoolean());
        Assert.Equal(1_760_000_000_000, gpu.GetProperty("calibratedAtMs").GetInt64());
    }

    [Fact]
    public async Task State_leaves_out_the_time_before_a_calibration_is_saved()
    {
        var sensors = new LevelSenseSensors();
        sensors.States.Add(new GpuLevelSenseState("/gpu-nvidia/0", "ASUS ROG Astral RTX 5090", true, null));

        var result = await Handler("levelsense.state")(Services(sensors), null, CancellationToken.None);

        var gpu = result!.Value.GetProperty("gpus")[0];
        Assert.True(gpu.GetProperty("calibrating").GetBoolean());
        Assert.False(gpu.TryGetProperty("calibratedAtMs", out _));
    }

    [Fact]
    public async Task Calibrate_starts_a_calibration()
    {
        var sensors = new LevelSenseSensors();
        sensors.States.Add(new GpuLevelSenseState("/gpu-nvidia/0", "ASUS ROG Astral RTX 5090", false, 42));

        var result = await Handler("levelsense.calibrate")(Services(sensors), null, CancellationToken.None);

        Assert.Equal(1, sensors.RecalibrateCalls);
        Assert.Equal("/gpu-nvidia/0", result!.Value.GetProperty("gpus")[0].GetString());
    }

    [Fact]
    public async Task Calibrate_lets_a_running_calibration_finish()
    {
        var sensors = new LevelSenseSensors();
        sensors.States.Add(new GpuLevelSenseState("/gpu-nvidia/0", "ASUS ROG Astral RTX 5090", true, 42));

        var result = await Handler("levelsense.calibrate")(Services(sensors), null, CancellationToken.None);

        Assert.Equal(0, sensors.RecalibrateCalls);
        Assert.Equal("/gpu-nvidia/0", result!.Value.GetProperty("gpus")[0].GetString());
    }

    [Fact]
    public void Both_actions_are_listed()
    {
        Assert.Equal(new[] { "levelsense.state", "levelsense.calibrate" }, LevelSenseActions.AllActions);
    }
}
