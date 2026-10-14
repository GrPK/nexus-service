using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Sensors;
using System.Text.Json.Nodes;
using Nexus.Service.Cooling;
using Nexus.Service.Migration;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Migration;

/// <summary>A Y70-shaped machine (the capture's hardware, scrubbed serials) that tests mutate to cover one rule at a time.</summary>
internal sealed class Hw
{
    public const string SmartHub = "2053AAAA0001";
    public const string MiniHub = "205DBBBB0002";
    public const string Np50 = "NPFAKE000004";
    public const string QSeries = "2045CCCC0003";
    public const string Cpu = "12th Gen Intel Core i7-12700KF";
    public const string Gpu = "NVIDIA GeForce RTX 3070";
    public const string Board = "ASUS PRIME Z690-P WIFI D4";

    public List<FanChannel> Channels { get; } = new();
    public List<TemperatureSource> Sources { get; } = new();
    public HashSet<string> Uncontrolled { get; } = new();
    public List<CurveDocument> Curves { get; } = new();
    public string CpuModel { get; set; } = Cpu;
    public string BoardModel { get; set; } = Board;
    public List<string> GpuModels { get; } = new() { Gpu };
    public double NexusGlobal { get; set; } = 1.0;
    public Func<IReadOnlyList<CurveDocument>, LintScope, LintInput>? Lint { get; set; }
    public HashSet<string> ManualDriven { get; } = new();
    public HashSet<string> HealedIds { get; } = new();
    public Dictionary<string, string> FanRoles { get; } = new();
    public bool CoolingEnabled { get; set; } = true;
    public bool GuardEnabled { get; set; } = true;
    public bool AllowHubs { get; set; }
    public bool CoolingUntouched { get; set; } = true;

    public static Hw Y70()
    {
        var hw = new Hw();
        for (var i = 0; i < 2; i++)
        {
            hw.Channels.Add(GpuFan(i));
        }
        for (var i = 0; i < 4; i++)
        {
            hw.Channels.Add(new FanChannel
            {
                Id = $"smarthub:{SmartHub}:fan{i}", Name = $"Fan Port {i + 1}", DeviceId = $"smarthub:{SmartHub}", DeviceName = "HYTE Smart Hub",
            });
        }
        hw.Channels.Add(new FanChannel { Id = $"minihub:{MiniHub}:port1", Name = "Port 1 Fan", DeviceId = $"minihub:{MiniHub}", DeviceName = "HYTE Mini Hub" });
        hw.Channels.Add(new FanChannel { Id = $"minihub:{MiniHub}:port2", Name = "Port 2 Fans (3)", DeviceId = $"minihub:{MiniHub}", DeviceName = "HYTE Mini Hub" });
        hw.Sources.Add(new TemperatureSource { Id = "/intelcpu/0/temperature/14", Name = "CPU Package", Category = "CPU", Value = 40 });
        hw.Sources.Add(new TemperatureSource { Id = "/gpu-nvidia/0/temperature/0", Name = "GPU Core", Category = "GPU", Value = 45 });
        hw.Sources.Add(new TemperatureSource { Id = "/lpc/nct6798d/0/temperature/2", Name = "Temperature #2", Category = "Motherboard", Value = 30 });
        return hw;
    }

    public static FanChannel GpuFan(int index) => new()
    {
        Id = $"/gpu-nvidia/0/control/{index}", Name = $"GPU Fan {index + 1}", IsGpu = true, DeviceId = "/gpu-nvidia/0", DeviceName = Gpu,
    };

    public Hw AddBoardFan(string id, string name)
    {
        Channels.Add(new FanChannel { Id = id, Name = name });
        return this;
    }

    public Hw AddNp50()
    {
        AllowHubs = true;
        Channels.Add(new FanChannel { Id = $"np50:{Np50}:legacy", Name = "Legacy 4-pin", DeviceId = $"np50:{Np50}", DeviceName = "HYTE NP50" });
        Channels.Add(new FanChannel { Id = $"np50:{Np50}:port1:dev2", Name = "FP12 (Port 1 #2)", DeviceId = $"np50:{Np50}", DeviceName = "HYTE NP50" });
        return this;
    }

    public Hw AddQSeries(string product = "HYTE Q60")
    {
        AllowHubs = true;
        var device = $"qseries:{QSeries}";
        Channels.Add(new FanChannel { Id = $"{device}:pump", Name = "Pump", Kind = FanKinds.Pump, DeviceId = device, DeviceName = product });
        Channels.Add(new FanChannel { Id = $"{device}:p1:2", Name = "FP12 (Port 1 #2)", DeviceId = device, DeviceName = product });
        Channels.Add(new FanChannel { Id = $"{device}:p2:1:2", Name = "Duo (Port 2 #1-2)", DeviceId = device, DeviceName = product });
        Sources.Add(new TemperatureSource { Id = $"{device}:coolant-in", Name = "Coolant in", Category = "Cooler", Value = 35, DeviceId = device, DeviceName = product });
        Sources.Add(new TemperatureSource { Id = $"{device}:coolant-out", Name = "Coolant out", Category = "Cooler", Value = 36, DeviceId = device, DeviceName = product });
        return this;
    }

    public Nexus2FanCurveEnvironment Env() => new()
    {
        Channels = Channels,
        Sources = Sources,
        Uncontrolled = Uncontrolled,
        CpuModel = CpuModel,
        BoardModel = BoardModel,
        GpuModels = GpuModels,
        CurrentCurves = Curves,
        NexusGlobalModifier = NexusGlobal,
        FanRoles = FanRoles,
        ManualDriven = ManualDriven,
        HealedChannelIds = HealedIds,
        CoolingEnabled = CoolingEnabled,
        IsCoolingUntouched = _ => CoolingUntouched,
        ThermalGuardEnabled = GuardEnabled,
        AllowCoolerHubTargets = AllowHubs,
        BuildLint = Lint ?? ((curves, scope) => new LintInput { Curves = curves, Channels = Channels, Sources = Sources, Uncontrolled = Uncontrolled.ToList(), FanRoles = FanRoles, Scope = scope }),
    };
}

/// <summary>Builds Nexus 2 config.json shapes: every type block present, as the real file stores them.</summary>
internal static class N2
{
    public static JsonObject Out(string id, string deviceType, string? parent = null)
    {
        var o = new JsonObject { ["id"] = id, ["deviceType"] = deviceType };
        if (parent is not null)
        {
            o["parentDeviceId"] = parent;
        }
        return o;
    }

    public static JsonObject Gpu(int fan) => Out($"GPU Fan {fan}", "GPUFan", Hw.Gpu + "-ID");
    public static JsonObject Pwm(int n) => Out($"PWM {n}", "FanPort", Hw.SmartHub);
    public static JsonObject Mini(int usbPort) => Out(Hw.MiniHub + usbPort, "IBPFan", Hw.MiniHub);

    public static JsonObject Input(string id = "/intelcpu/0/temperature/14", string device = Hw.Cpu, string type = "Temperature") =>
        new() { ["id"] = id, ["device"] = device, ["type"] = type };

    private static JsonArray Points(IEnumerable<(double Temp, double Speed)> points)
    {
        var arr = new JsonArray();
        foreach (var (t, s) in points)
        {
            arr.Add(new JsonObject { ["speed"] = s, ["temp"] = t, ["id"] = Guid.NewGuid().ToString() });
        }
        return arr;
    }

    private static JsonObject Base(string type, string name, string id, JsonNode? input, IEnumerable<JsonObject> outputs)
    {
        var outs = new JsonArray();
        foreach (var o in outputs)
        {
            outs.Add(o.DeepClone());
        }
        return new JsonObject
        {
            ["name"] = name,
            ["type"] = type,
            ["id"] = id,
            ["input"] = input,
            ["outputs"] = outs,
            ["linear"] = new JsonObject { ["minTemp"] = 35, ["maxTemp"] = 70, ["minSpeed"] = 50, ["maxSpeed"] = 80, ["responseTime"] = 1 },
            ["flat"] = new JsonObject { ["speed"] = 50 },
            ["graph"] = new JsonObject
            {
                ["speedModifier"] = 100,
                ["points"] = Points(new[] { (0.0, 20.0), (20.0, 30.0), (40.0, 40.0), (70.0, 100.0) }),
                ["responseTime"] = 1,
            },
            ["mixed"] = new JsonObject { ["curveIds"] = new JsonArray(), ["responseTime"] = 1, ["inputFunction"] = "max", ["outputFunction"] = "max" },
        };
    }

    public static JsonObject Graph(string name, IEnumerable<(double Temp, double Speed)> points, JsonObject[] outputs, double modifier = 100, JsonNode? input = null, string? id = null)
    {
        var c = Base("graph", name, id ?? name, input ?? Input(), outputs);
        c["graph"] = new JsonObject { ["speedModifier"] = modifier, ["points"] = Points(points), ["responseTime"] = 3 };
        return c;
    }

    public static JsonObject Linear(string name, double minTemp, double maxTemp, double minSpeed, double maxSpeed, JsonObject[] outputs, JsonNode? input = null)
    {
        var c = Base("linear", name, name, input ?? Input(), outputs);
        c["linear"] = new JsonObject { ["minTemp"] = minTemp, ["maxTemp"] = maxTemp, ["minSpeed"] = minSpeed, ["maxSpeed"] = maxSpeed, ["responseTime"] = 2 };
        return c;
    }

    public static JsonObject Flat(string name, double speed, JsonObject[] outputs)
    {
        var c = Base("flat", name, name, null, outputs);
        c["flat"] = new JsonObject { ["speed"] = speed };
        return c;
    }

    public static JsonObject Mixed(string name, JsonObject[] outputs) => Base("mixed", name, name, null, outputs);

    public static JsonObject Profile(string name, bool active, double global, bool easy, params JsonObject[] curves)
    {
        var arr = new JsonArray();
        foreach (var c in curves)
        {
            arr.Add(c.DeepClone());
        }
        return new JsonObject
        {
            ["name"] = name,
            ["id"] = name,
            ["active"] = active,
            ["widgets"] = new JsonObject
            {
                ["cooling"] = new JsonObject
                {
                    ["globalSpeedModifier"] = global,
                    ["easyMode"] = new JsonObject { ["enabled"] = easy, ["selected"] = "balanced" },
                    ["curves"] = arr,
                },
            },
        };
    }

    public static JsonObject MiniLayout(int? fanPort1, int? fanPort2)
    {
        var layout = new JsonObject();
        if (fanPort1 is { } a)
        {
            layout["fanPort1"] = a;
        }
        if (fanPort2 is { } b)
        {
            layout["fanPort2"] = b;
        }
        layout["rgbPort1"] = 16;
        layout["rgbPort2"] = 0;
        return new JsonObject { ["minihub"] = new JsonObject { ["layout"] = layout } };
    }

    public static JsonElement Root(params JsonObject[] profiles) => RootWith(MiniLayout(1, 3), profiles);

    public static JsonElement RootWith(JsonObject? settings, params JsonObject[] profiles)
    {
        var arr = new JsonArray();
        foreach (var p in profiles)
        {
            arr.Add(p.DeepClone());
        }
        var root = new JsonObject { ["profiles"] = arr };
        if (settings is not null)
        {
            root["settings"] = settings.DeepClone();
        }
        return JsonDocument.Parse(root.ToJsonString()).RootElement;
    }

    public static JsonElement Single(double global, params JsonObject[] curves) => Root(Profile("Profile 1", true, global, false, curves));

    public static JsonElement FromFixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Nexus2", "cooling", name);
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement;
    }
}

internal sealed class RecordingFans : IFanControlProvider
{
    private readonly Hw _hw;
    public RecordingFans(Hw hw) => _hw = hw;
    public IReadOnlyList<FanChannel> GetFanChannels() => _hw.Channels;
    /// <summary>Runs once on the first source read, to change settings mid-plan.</summary>
    public Action? OnFirstSourceRead { get; set; }

    public IReadOnlyList<TemperatureSource> GetTemperatureSources()
    {
        var hook = OnFirstSourceRead;
        OnFirstSourceRead = null;
        hook?.Invoke();
        return _hw.Sources;
    }

    public float? ReadTemperature(string sensorId) => _hw.Sources.FirstOrDefault(s => s.Id == sensorId)?.Value;
    /// <summary>Every hardware-facing call in order, so two activation paths can be compared.</summary>
    public List<string> Calls { get; } = new();
    public int SetFanSpeed(string channelId, int dutyPercent) { Calls.Add($"set:{channelId}:{dutyPercent}"); return dutyPercent; }
    public void DriveFanSpeed(string channelId, int dutyPercent) => Calls.Add($"drive:{channelId}:{dutyPercent}");
    public void ReleaseFan(string channelId) => Calls.Add($"release:{channelId}");
    public void ReleaseAll() => Calls.Add("releaseAll");
    public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(IReadOnlyList<string> fanIds, IProgress<FanCalibrationProgress> progress, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());
}

internal sealed class FixedSensors : ISensorProvider
{
    public string GetCpuModel() => Hw.Cpu;
    public System.Collections.Generic.IReadOnlyList<Nexus.Service.Models.Sensors.HardwareSensor> GetCpuSensors() => Array.Empty<Nexus.Service.Models.Sensors.HardwareSensor>();
    public (bool Healthy, float DistanceToTJMax) GetCpuHealth() => (true, 0);
    public IReadOnlyList<string> GetGpuModels() => new[] { Hw.Gpu };
    public IReadOnlyList<Nexus.Service.Models.Sensors.HardwareSensor> GetGpuSensors() => Array.Empty<Nexus.Service.Models.Sensors.HardwareSensor>();
    public IReadOnlyList<Nexus.Service.Models.Sensors.GpuReadout> GetGpus() => Array.Empty<Nexus.Service.Models.Sensors.GpuReadout>();
    public IReadOnlyList<Nexus.Service.Models.Sensors.HardwareSensor> GetMemorySensors() => Array.Empty<Nexus.Service.Models.Sensors.HardwareSensor>();
    public string GetMemoryTotalFormatted() => "";
    public string GetRamBrandModel() => "";
    public IReadOnlyDictionary<string, Nexus.Service.Models.Sensors.StorageComponent> GetStorageComponents(bool includeSmart = true) => new Dictionary<string, Nexus.Service.Models.Sensors.StorageComponent>();
    public IReadOnlyList<string> GetStoragePartitions() => Array.Empty<string>();
    public IReadOnlyList<Nexus.Service.Models.Sensors.StorageDriveInfo> GetStorageInfo() => Array.Empty<Nexus.Service.Models.Sensors.StorageDriveInfo>();
    public string GetStorageBrandModel() => "";
    public IReadOnlyList<Nexus.Service.Models.Sensors.HardwareSensor> GetMotherboardSensors() => Array.Empty<Nexus.Service.Models.Sensors.HardwareSensor>();
    public string GetMotherboardModel() => Hw.Board;
    public Nexus.Service.Models.Sensors.SensorExtras GetSensorExtras() => new();
    public string GetOsVersion() => "";
    public void SetPollingRate(int pollingRate) { }
    public Task ReadyAsync(CancellationToken ct = default) => Task.CompletedTask;
}


internal static class FanCurveServices
{
    public static (Nexus2MigrationService Service, InMemoryConfigStore Store, Hw Hw) Service(string fixture, INexus2ConfigReader? reader = null, Action<RecordingFans>? configureFans = null, string? configText = null)
    {
        var hw = Hw.Y70();
        var store = new InMemoryConfigStore();
        var services = new ServiceCollection();
        var fans = new RecordingFans(hw);
        configureFans?.Invoke(fans);
        services.AddSingleton<IFanControlProvider>(fans);
        services.AddSingleton<ISensorProvider>(new FixedSensors());
        reader ??= new FakeNexus2ConfigReader { ConfigText = configText ?? File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Nexus2", "cooling", fixture)) };
        var service = new Nexus2MigrationService(reader, null!, null!, null!, store, null!, services.BuildServiceProvider());
        return (service, store, hw);
    }
}


/// <summary>Counts reads so a test can see a cached check skip the file.</summary>
internal sealed class CountingReader : INexus2ConfigReader
{
    private readonly string _text;
    public int Reads;
    public CountingReader(string fixture) =>
        _text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Nexus2", "cooling", fixture));

    public Nexus2ConfigReadResult? Read()
    {
        Reads++;
        return new Nexus2ConfigReadResult(JsonDocument.Parse(_text), "");
    }
}

/// <summary>
/// The hardware side of a curve engine tick: every driven channel is written, and on a shared-mode hub one write
/// also flips the whole device to software and re-sends the other channels' last duty (zero when never driven).
/// </summary>
internal sealed class WriteRecorder
{
    private static readonly string[] SharedModePrefixes = { "minihub:", "np50:", "qseries:" };

    public Dictionary<string, List<int>> Writes { get; } = new();
    public Dictionary<string, string> DeviceMode { get; } = new();

    public static WriteRecorder Tick(CoolingSettings cooling, IReadOnlyList<FanChannel> channels)
    {
        var rec = new WriteRecorder();
        var owned = new HashSet<string>();
        foreach (var curve in cooling.Curves)
        {
            var duty = curve.Type == "Flat"
                ? curve.Flat!.Speed
                : (int)Math.Round(CurveEngine.EvaluateGraph(curve.Graph, 40f) ?? 0);
            foreach (var output in curve.Outputs)
            {
                owned.Add(output.Id);
                rec.Drive(output.Id, duty, channels);
            }
        }
        foreach (var (id, duty) in cooling.ManualSpeeds)
        {
            if (!owned.Contains(id))
            {
                rec.Drive(id, duty, channels);
            }
        }
        return rec;
    }

    private void Drive(string id, int duty, IReadOnlyList<FanChannel> channels)
    {
        Record(id, duty);
        var channel = channels.FirstOrDefault(c => c.Id == id);
        if (channel?.DeviceId is not { } device || !SharedModePrefixes.Any(p => id.StartsWith(p, StringComparison.Ordinal)))
        {
            return;
        }
        DeviceMode[device] = "software";
        foreach (var sibling in channels.Where(c => c.DeviceId == device && c.Id != id && !c.ReadOnly))
        {
            Record(sibling.Id, Writes.TryGetValue(sibling.Id, out var seen) ? seen[^1] : 0);
        }
    }

    private void Record(string id, int duty)
    {
        if (!Writes.TryGetValue(id, out var list))
        {
            Writes[id] = list = new List<int>();
        }
        list.Add(duty);
    }

    public List<int> WritesTo(string id) => Writes.TryGetValue(id, out var list) ? list : new List<int>();
}
