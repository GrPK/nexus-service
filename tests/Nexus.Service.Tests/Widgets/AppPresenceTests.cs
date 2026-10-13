using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Models.Widgets;
using Nexus.Service.Sensors;
using Nexus.Service.Sockets;
using Nexus.Service.Widgets;
using Xunit;

namespace Nexus.Service.Tests.Widgets;

public sealed class AppPresenceTests : IDisposable
{
    // Only the Level Sense members and GetGpus matter here; the rest are never called.
    private sealed class Sensors : ISensorProvider
    {
        public List<GpuLevelSenseState> States { get; } = new();
        public List<GpuPinCurrents> Cards { get; } = new();
        public int GpuReads { get; private set; }

        public IReadOnlyList<GpuLevelSenseState> GetGpuLevelSenseStates() => States;
        public IReadOnlyList<GpuPinCurrents> ReadGpuPinCurrents() => Cards;
        public IReadOnlyList<GpuReadout> GetGpus()
        {
            GpuReads++;
            return Array.Empty<GpuReadout>();
        }

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

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.FromUnixTimeMilliseconds(1_000_000);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly GpuLevelSenseState Astral = new("/gpu-nvidia/0", "ASUS ROG Astral RTX 5090", false, 42);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "nexus-app-presence-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static AppManifest Manifest(string? requires, string? category = "device") => new()
    {
        Id = "com.example.levelsense",
        Name = "Level Sense",
        Category = category,
        Requires = requires,
    };

    private static AppInstalledListing Listing(AppManifest manifest) =>
        new() { Id = manifest.Id, Name = manifest.Name, Category = manifest.Category };

    [Fact]
    public void Apply_leaves_an_app_that_requires_nothing_alone()
    {
        var manifest = Manifest(requires: null);
        var listing = Listing(manifest);

        AppPresence.Apply(listing, manifest, new Sensors());

        Assert.Equal("device", listing.Category);
        Assert.Equal("Level Sense", listing.Name);
    }

    [Fact]
    public void Apply_keeps_a_device_app_out_of_devices_until_the_card_is_detected()
    {
        var manifest = Manifest(AppPresence.LevelSense);
        var listing = Listing(manifest);

        AppPresence.Apply(listing, manifest, new Sensors());

        Assert.Null(listing.Category);
        Assert.Equal("Level Sense", listing.Name);
    }

    [Fact]
    public void Apply_lists_a_detected_card_under_devices_by_its_name()
    {
        var manifest = Manifest(AppPresence.LevelSense);
        var listing = Listing(manifest);
        var sensors = new Sensors();
        sensors.States.Add(Astral);

        AppPresence.Apply(listing, manifest, sensors);

        Assert.Equal("device", listing.Category);
        Assert.Equal("ROG Astral RTX 5090", listing.Name);
    }

    [Fact]
    public void Apply_lists_any_rog_astral_card_for_an_app_that_requires_one()
    {
        var manifest = Manifest(AppPresence.Astral);
        var listing = Listing(manifest);
        var sensors = new Sensors();
        AppPresence.Apply(listing, manifest, sensors);
        Assert.Null(listing.Category);

        sensors.Cards.Add(new GpuPinCurrents("/gpu-nvidia/0", "ASUS ROG Astral RTX 5080", new[] { 1f, 1f, 1f, 1f, 1f, 1f }, 42));
        AppPresence.Apply(listing, manifest, sensors);

        Assert.Equal("device", listing.Category);
        Assert.Equal("ROG Astral RTX 5080", listing.Name);
    }

    [Fact]
    public void An_unknown_requirement_is_never_present()
    {
        var sensors = new Sensors();
        sensors.States.Add(Astral);

        Assert.False(AppPresence.Check("gpu.something-else", sensors).Present);
    }

    private AppRegistry RegistryWithApp(string? requires)
    {
        var dir = Path.Combine(_root, "com.example.levelsense");
        Directory.CreateDirectory(dir);
        var requiresJson = requires is null ? "" : $"\"requires\": \"{requires}\",";
        File.WriteAllText(Path.Combine(dir, "manifest.json"),
            "{ \"schema\": \"nexus.app/1\", \"id\": \"com.example.levelsense\", \"name\": \"Level Sense\", " +
            "\"version\": \"1.0.0\", \"runtime\": \"sdk\", \"category\": \"device\", " + requiresJson +
            " \"surfaces\": [\"cell\", \"page\"], \"page\": true, \"sizes\": [\"2x2\"], \"capabilities\": { \"code\": \"worker\" } }");
        File.WriteAllText(Path.Combine(dir, "widget.mjs"), "export {};");
        return new AppRegistry(() => new[] { new AppInstallPaths.Root(_root, AppInstallPaths.Source.Bundled) });
    }

    [Fact]
    public void Notifier_reports_the_card_appearing_and_going_once_each()
    {
        var sensors = new Sensors();
        var notifier = new AppPresenceNotifier(RegistryWithApp(AppPresence.LevelSense), sensors, new MultiplexHub(), new Clock());

        Assert.False(notifier.Tick());
        sensors.States.Add(Astral);
        Assert.True(notifier.Tick());
        Assert.False(notifier.Tick());
        sensors.States.Clear();
        Assert.True(notifier.Tick());
    }

    [Fact]
    public void Notifier_reads_the_gpus_at_most_every_thirty_seconds_while_waiting()
    {
        var sensors = new Sensors();
        var clock = new Clock();
        var notifier = new AppPresenceNotifier(RegistryWithApp(AppPresence.LevelSense), sensors, new MultiplexHub(), clock);

        notifier.Tick();
        notifier.Tick();
        Assert.Equal(1, sensors.GpuReads);

        clock.Now += TimeSpan.FromSeconds(31);
        notifier.Tick();
        Assert.Equal(2, sensors.GpuReads);
    }

    [Fact]
    public void Notifier_does_nothing_without_an_app_that_requires_hardware()
    {
        var sensors = new Sensors();
        sensors.States.Add(Astral);
        var notifier = new AppPresenceNotifier(RegistryWithApp(requires: null), sensors, new MultiplexHub(), new Clock());

        Assert.False(notifier.Tick());
        Assert.Equal(0, sensors.GpuReads);
    }
}
