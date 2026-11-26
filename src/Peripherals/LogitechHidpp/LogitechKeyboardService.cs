using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Media;
using Nexus.Service.Models.Devices;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Serialization;

namespace Nexus.Service.Peripherals.LogitechHidpp;

/// <summary>
/// A wired Logitech keyboard's key assignments (HID++ 2.0 features 0x1B05, 0x1B10, 0x8101), the
/// BASE / FN / G-SHIFT layers of G HUB's KEYCONTROL. The keyboard applies them itself, so they
/// work with no software running. It cannot report its tables, so Nexus keeps the layers and
/// writes all three whole, as G HUB does. The G-SHIFT table carries the BASE rows too (the
/// keyboard reads only that table while G-shift is held), with the G-SHIFT rows on top.
/// </summary>
public sealed class LogitechKeyboardService : BackgroundService
{
    private const string Tag = "logitech-keyboard";
    private const int LogitechVendorId = 0x046D;
    private const ushort LayoutFeature = 0x4540;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);
    private static MouseResult NotPresent => new(false, "not-present", "no supported Logitech keyboard is connected");

    private sealed record KeyboardModel(int ProductId, string Name, string File, int UsagePage, int Usage);

    private static readonly KeyboardModel G915X = new(0xC359, "G915 X", "g915-x.json", 0xFF00, 0x0002);

    // Layout codes (feature 0x4540) whose keyboards have the tall ISO Enter.
    private static readonly HashSet<int> IsoLayoutCodes = new() { 0x37 };

    private readonly KeyboardModel _model;
    private readonly IHidppChannel _channel;
    private readonly string _configPath;
    private readonly object _lock = new();
    private int _loadedGeneration = -1;
    private IReadOnlyList<int> _controls = Array.Empty<int>();
    private int? _layoutCode;
    private bool? _enabled;
    private string? _error;
    private LogitechKeyboardConfig? _config;

    public LogitechKeyboardService(IHidEnumerator hid)
        : this(new HidppChannel(hid, LogitechVendorId, G915X.ProductId, G915X.UsagePage, G915X.Usage, Tag),
               G915X, MediaLibrary.DeviceStoreDir(Tag))
    {
    }

    private LogitechKeyboardService(IHidppChannel channel, KeyboardModel model, string storeDir)
    {
        _channel = channel;
        _model = model;
        _configPath = Path.Combine(storeDir, model.File);
    }

    /// <summary>For tests: a G915 X behind <paramref name="channel"/>, its layers kept under <paramref name="storeDir"/>.</summary>
    internal static LogitechKeyboardService ForTest(IHidppChannel channel, string storeDir) => new(channel, G915X, storeDir);

    /// <summary>The connected model's name ("G915 X"), or null.</summary>
    public string? PresentModel()
    {
        lock (_lock)
        {
            return _channel.IsPresent() ? _model.Name : null;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckInterval);
        do
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[{Tag}] check failed: {ex.Message}");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>Reads the keyboard once per connection and finishes a write that did not land.</summary>
    internal void Tick()
    {
        lock (_lock)
        {
            if (_channel.IsPresent() && _channel.Generation != _loadedGeneration)
            {
                Load();
            }
        }
    }

    public LogitechKeyboardStateDto GetState()
    {
        lock (_lock)
        {
            var config = Config();
            var state = new LogitechKeyboardStateDto { Layers = Copy(config.Layers), Written = config.Written };
            if (!_channel.IsPresent())
            {
                return state;
            }
            if (_channel.Generation != _loadedGeneration)
            {
                Load();
            }
            state.Present = true;
            state.Model = _model.Name;
            state.Name = $"Logitech {_model.Name}";
            state.Error = _error;
            state.LayoutCode = _layoutCode;
            state.LayoutDetected = _layoutCode is { } code ? (IsoLayoutCodes.Contains(code) ? "iso" : "ansi") : null;
            state.LayoutChoice = config.Layout;
            state.Layout = config.Layout ?? state.LayoutDetected;
            state.Controls = _controls.ToList();
            state.Enabled = _enabled;
            state.Written = config.Written;
            return state;
        }
    }

    /// <summary>Replaces all three layers and writes them to the keyboard.</summary>
    public MouseResult SetLayers(LogitechKeyboardLayersDto layers)
    {
        LogitechKeyboardLayersDto clean;
        try
        {
            clean = Normalize(layers);
            foreach (var table in Tables(clean))
            {
                HidppKeyTable.Build(table.Rows);
            }
        }
        catch (ArgumentException ex)
        {
            return MouseResult.Invalid(ex.Message);
        }
        lock (_lock)
        {
            var config = Config();
            config.Layers = clean;
            config.Written = false;
            Save(config);
            if (!_channel.IsPresent())
            {
                return NotPresent;
            }
            return WriteAll(config);
        }
    }

    /// <summary>Writes the kept layers again (after another program replaced them).</summary>
    public MouseResult Rewrite()
    {
        lock (_lock)
        {
            if (!_channel.IsPresent())
            {
                return NotPresent;
            }
            return WriteAll(Config());
        }
    }

    /// <summary>Turns the assignments on or off on the keyboard (G HUB's switch, Fn+F1).</summary>
    public MouseResult SetEnabled(bool enabled)
    {
        lock (_lock)
        {
            var config = Config();
            config.Enabled = enabled;
            Save(config);
            if (!_channel.IsPresent())
            {
                return NotPresent;
            }
            if (HidppKeyCustomization.Open(_channel) is not { } fkc || !fkc.SetEnabled(enabled))
            {
                return MouseResult.Failed("the keyboard did not take the switch");
            }
            _enabled = fkc.GetEnabled() ?? enabled;
            return MouseResult.Done;
        }
    }

    public MouseResult SetLayout(string? layout)
    {
        if (layout is not (null or "ansi" or "iso"))
        {
            return MouseResult.Invalid("layout must be ansi, iso or null");
        }
        lock (_lock)
        {
            var config = Config();
            config.Layout = layout;
            Save(config);
            return MouseResult.Done;
        }
    }

    // Caller holds _lock.
    private void Load()
    {
        _loadedGeneration = _channel.Generation;
        _error = null;
        _controls = HidppControlList.Read(_channel) ?? Array.Empty<int>();
        _layoutCode = _channel.FeatureIndex(LayoutFeature) is { } layoutIndex ? _channel.Call(layoutIndex, 0, ReadOnlySpan<byte>.Empty)?[0] : null;
        _enabled = HidppKeyCustomization.Open(_channel)?.GetEnabled();
        if (_controls.Count == 0 || HidppFileManagement.Open(_channel) is null || HidppKeyCustomization.Open(_channel) is null)
        {
            _error = "this keyboard does not offer key assignments";
            return;
        }
        var config = Config();
        if (!config.Written && File.Exists(_configPath))
        {
            var result = WriteAll(config);
            ServiceLog.Info($"[{Tag}] pending layers written on connect: {result.Status}");
        }
    }

    // Caller holds _lock.
    private MouseResult WriteAll(LogitechKeyboardConfig config)
    {
        if (HidppFileManagement.Open(_channel) is not { } files || HidppKeyCustomization.Open(_channel) is not { } fkc)
        {
            return MouseResult.Failed("this keyboard does not offer key assignments");
        }
        foreach (var table in Tables(config.Layers))
        {
            var data = HidppKeyTable.Build(table.Rows);
            if (!files.WriteFile(HidppKeyTable.OwnerFeature, (byte)table.Layer, HidppKeyTable.FileType, data))
            {
                _error = $"the keyboard did not take layer {table.Layer}";
                ServiceLog.Warn($"[{Tag}] {_error}");
                return MouseResult.Failed(_error);
            }
        }
        if (!fkc.SetEnabled(config.Enabled))
        {
            _error = "the keyboard did not take the assignments switch";
            return MouseResult.Failed(_error);
        }
        _enabled = fkc.GetEnabled() ?? config.Enabled;
        _error = null;
        config.Written = true;
        Save(config);
        ServiceLog.Info($"[{Tag}] layers written: {config.Layers.Base.Count} base, {config.Layers.Fn.Count} fn, {config.Layers.GShift.Count} g-shift");
        return MouseResult.Done;
    }

    /// <summary>The three tables to write: BASE, FN, and G-SHIFT carrying BASE under its own rows.</summary>
    internal static IEnumerable<(int Layer, List<HidppKeyRow> Rows)> Tables(LogitechKeyboardLayersDto layers)
    {
        var baseRows = layers.Base.Select(ToRow).ToList();
        yield return (HidppKeyTable.LayerBase, baseRows);
        yield return (HidppKeyTable.LayerFn, layers.Fn.Select(ToRow).ToList());
        var shifted = layers.GShift.Select(ToRow).ToDictionary(r => r.Key);
        foreach (var row in baseRows)
        {
            shifted.TryAdd(row.Key, row);
        }
        yield return (HidppKeyTable.LayerGShift, shifted.Values.ToList());
    }

    private static HidppKeyRow ToRow(LogitechKeyboardRowDto dto)
    {
        var kind = dto.Kind switch
        {
            "keyboard" => HidppKeyActionKind.Keyboard,
            "mouse" => HidppKeyActionKind.Mouse,
            "consumer" => HidppKeyActionKind.Consumer,
            "gshift" => HidppKeyActionKind.GShift,
            "disabled" => HidppKeyActionKind.Disabled,
            _ => throw new ArgumentException($"unknown kind '{dto.Kind}'"),
        };
        var action = kind switch
        {
            HidppKeyActionKind.GShift => HidppKeyAction.GShift,
            HidppKeyActionKind.Disabled => HidppKeyAction.Disabled,
            _ => new HidppKeyAction(kind, dto.Code, dto.Modifiers),
        };
        action.Validate();
        return new HidppKeyRow(dto.Key, action);
    }

    private static LogitechKeyboardLayersDto Normalize(LogitechKeyboardLayersDto layers)
    {
        static List<LogitechKeyboardRowDto> Clean(List<LogitechKeyboardRowDto>? rows, string layer)
        {
            var list = (rows ?? new()).Select(r => new LogitechKeyboardRowDto
            {
                Key = r.Key,
                Kind = r.Kind,
                Code = r.Kind is "gshift" ? HidppKeyAction.GShiftCode : r.Kind is "disabled" ? 0 : r.Code,
                Modifiers = r.Kind is "keyboard" ? r.Modifiers : 0,
            }).OrderBy(r => r.Key).ToList();
            var dup = list.GroupBy(r => r.Key).FirstOrDefault(g => g.Count() > 1);
            if (dup is not null)
            {
                throw new ArgumentException($"key {dup.Key} appears twice in {layer}");
            }
            foreach (var row in list)
            {
                ToRow(row);
            }
            return list;
        }
        var clean = new LogitechKeyboardLayersDto
        {
            Base = Clean(layers.Base, "base"),
            Fn = Clean(layers.Fn, "fn"),
            GShift = Clean(layers.GShift, "g-shift"),
        };
        if (clean.Fn.Concat(clean.GShift).Any(r => r.Kind == "gshift"))
        {
            throw new ArgumentException("the G-shift key belongs on the base layer");
        }
        return clean;
    }

    private static LogitechKeyboardLayersDto Copy(LogitechKeyboardLayersDto layers) => new()
    {
        Base = layers.Base.Select(CopyRow).ToList(),
        Fn = layers.Fn.Select(CopyRow).ToList(),
        GShift = layers.GShift.Select(CopyRow).ToList(),
    };

    private static LogitechKeyboardRowDto CopyRow(LogitechKeyboardRowDto r) => new() { Key = r.Key, Kind = r.Kind, Code = r.Code, Modifiers = r.Modifiers };

    // Caller holds _lock.
    private LogitechKeyboardConfig Config()
    {
        if (_config is not null)
        {
            return _config;
        }
        try
        {
            if (File.Exists(_configPath))
            {
                _config = JsonSerializer.Deserialize(File.ReadAllText(_configPath), AppJsonContext.Default.LogitechKeyboardConfig);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            ServiceLog.Warn($"[{Tag}] could not read {_configPath}: {ex.Message}");
        }
        return _config ??= new LogitechKeyboardConfig();
    }

    // Caller holds _lock.
    private void Save(LogitechKeyboardConfig config)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
            AtomicJsonFile.Write(_configPath, JsonSerializer.Serialize(config, AppJsonContext.Default.LogitechKeyboardConfig));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ServiceLog.Warn($"[{Tag}] could not save {_configPath}: {ex.Message}");
        }
    }
}
