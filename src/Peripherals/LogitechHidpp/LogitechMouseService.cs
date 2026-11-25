using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Media;
using Nexus.Service.Models.Devices;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.LogitechHidpp;

/// <summary>Outcome of a change to a mouse's onboard memory.</summary>
public sealed record MouseResult(bool Ok, string Status, string? Message = null)
{
    public static readonly MouseResult Done = new(true, "ok");
    public static MouseResult NotPresent => new(false, "not-present", "no supported Logitech mouse is connected");
    public static MouseResult Invalid(string message) => new(false, "invalid", message);
    public static MouseResult Failed(string message) => new(false, "failed", message);
}

/// <summary>
/// A wired Logitech mouse's onboard memory (HID++ 2.0 feature 0x8100): reads its profiles,
/// edits DPI levels, report rate, button bindings and names the way G HUB's onboard memory
/// mode does, and switches the active profile. Every write is preceded by a backup of the
/// sector (the first one ever read is kept for good, so a profile can always go back to how
/// the mouse came), lands whole, and is read back; a sector that does not read back as
/// written is rewritten with what was there before.
/// </summary>
public sealed class LogitechMouseService : BackgroundService
{
    private const string Tag = "logitech-mouse";
    private const int LogitechVendorId = 0x046D;
    private const ushort ReportRateFeature = 0x8060;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);

    private sealed record MouseModel(int ProductId, string Name, int UsagePage, int Usage, string[] ButtonLabels);

    // Button labels follow the factory bindings of each slot.
    private static readonly MouseModel G502XPlus = new(0xC095, "G502 X PLUS", 0xFF00, 0x0002, new[]
    {
        "Left click", "Right click", "Middle click", "Back", "DPI shift", "Forward",
        "Scroll left", "Scroll right", "Profile switch", "DPI up", "DPI down",
    });

    private readonly MouseModel _model;
    private readonly IHidppChannel _channel;
    private readonly string _backupRoot;
    private readonly object _lock = new();
    private int _loadedGeneration = -1;
    private HidppOnboardInfo? _info;
    private int? _mode;
    private int? _currentSector;
    private IReadOnlyList<HidppProfileSlot> _slots = Array.Empty<HidppProfileSlot>();
    private readonly Dictionary<int, HidppOnboardProfile> _profiles = new();
    private IReadOnlyList<int> _rates = Array.Empty<int>();
    private string? _error;

    public LogitechMouseService(IHidEnumerator hid)
        : this(new HidppChannel(hid, LogitechVendorId, G502XPlus.ProductId, G502XPlus.UsagePage, G502XPlus.Usage, Tag),
               G502XPlus, MediaLibrary.DeviceStoreDir(Tag))
    {
    }

    private LogitechMouseService(IHidppChannel channel, MouseModel model, string backupRoot)
    {
        _channel = channel;
        _model = model;
        _backupRoot = backupRoot;
    }

    /// <summary>For tests: a G502 X PLUS behind <paramref name="channel"/>, backups under <paramref name="backupRoot"/>.</summary>
    internal static LogitechMouseService ForTest(IHidppChannel channel, string backupRoot) => new(channel, G502XPlus, backupRoot);

    /// <summary>The connected model's name ("G502 X PLUS"), or null.</summary>
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

    /// <summary>Reads the memory once per connection.</summary>
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

    public LogitechMouseStateDto GetState()
    {
        lock (_lock)
        {
            var state = new LogitechMouseStateDto
            {
                Specials = HidppButtonBinding.SpecialNames.Select(kv => new LogitechMouseSpecialDto { Code = kv.Key, Name = kv.Value }).ToList(),
            };
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
            state.Mode = _mode switch { HidppOnboardProfiles.ModeOnboard => "onboard", HidppOnboardProfiles.ModeHost => "host", _ => null };
            state.ButtonCount = _info?.ButtonCount ?? 0;
            state.ButtonLabels = Enumerable.Range(0, state.ButtonCount)
                .Select(i => i < _model.ButtonLabels.Length ? _model.ButtonLabels[i] : $"Button {i + 1}").ToList();
            state.ReportRatesHz = _rates.ToList();
            for (var n = 0; n < _slots.Count; n++)
            {
                var slot = _slots[n];
                if (slot.Sector == _currentSector)
                {
                    state.ActiveProfile = n + 1;
                }
                if (_profiles.TryGetValue(slot.Sector, out var profile))
                {
                    state.Profiles.Add(ToDto(n + 1, slot, profile, state.ButtonCount, File.Exists(OriginalPath(slot.Sector))));
                }
            }
            return state;
        }
    }

    public MouseResult UpdateProfile(int number, LogitechMouseProfileRequest request)
    {
        HidppOnboardProfileEdit edit;
        try
        {
            edit = ToEdit(request);
        }
        catch (ArgumentException ex)
        {
            return MouseResult.Invalid(ex.Message);
        }
        lock (_lock)
        {
            if (Ready() is { } notReady)
            {
                return notReady;
            }
            if (!TrySlot(number, out var slot) || !_profiles.TryGetValue(slot.Sector, out var current))
            {
                return MouseResult.Invalid($"no profile {number}");
            }
            HidppOnboardProfile edited;
            try
            {
                edited = current.With(edit);
            }
            catch (ArgumentException ex)
            {
                return MouseResult.Invalid(ex.Message);
            }
            return Commit(slot.Sector, current, edited);
        }
    }

    /// <summary>Writes back the profile as Nexus first read it from this mouse.</summary>
    public MouseResult RestoreProfile(int number)
    {
        lock (_lock)
        {
            if (Ready() is { } notReady)
            {
                return notReady;
            }
            if (!TrySlot(number, out var slot) || !_profiles.TryGetValue(slot.Sector, out var current))
            {
                return MouseResult.Invalid($"no profile {number}");
            }
            var path = OriginalPath(slot.Sector);
            if (!File.Exists(path) || HidppOnboardProfile.Parse(File.ReadAllBytes(path)) is not { CrcValid: true } original
                || original.SectorSize != current.SectorSize)
            {
                return MouseResult.Failed("no usable backup of this profile");
            }
            return Commit(slot.Sector, current, original);
        }
    }

    public MouseResult SetActiveProfile(int number)
    {
        lock (_lock)
        {
            if (Ready() is { } notReady)
            {
                return notReady;
            }
            if (!TrySlot(number, out var slot))
            {
                return MouseResult.Invalid($"no profile {number}");
            }
            if (HidppOnboardProfiles.Open(_channel) is not { } onboard || !onboard.SetCurrentSector(slot.Sector))
            {
                return MouseResult.Failed("the mouse did not switch profiles");
            }
            _currentSector = onboard.GetCurrentSector() ?? slot.Sector;
            return MouseResult.Done;
        }
    }

    public MouseResult SetMode(string? mode)
    {
        var value = mode switch { "onboard" => HidppOnboardProfiles.ModeOnboard, "host" => HidppOnboardProfiles.ModeHost, _ => 0 };
        if (value == 0)
        {
            return MouseResult.Invalid("mode is \"onboard\" or \"host\"");
        }
        lock (_lock)
        {
            if (Ready() is { } notReady)
            {
                return notReady;
            }
            if (HidppOnboardProfiles.Open(_channel) is not { } onboard || !onboard.SetMode(value))
            {
                return MouseResult.Failed("the mouse did not change mode");
            }
            _mode = onboard.GetMode() ?? value;
            return MouseResult.Done;
        }
    }

    // Caller holds _lock.
    private MouseResult? Ready()
    {
        if (!_channel.IsPresent())
        {
            return MouseResult.NotPresent;
        }
        if (_channel.Generation != _loadedGeneration)
        {
            Load();
        }
        return _info is null ? MouseResult.Failed(_error ?? "the onboard memory could not be read") : null;
    }

    private bool TrySlot(int number, out HidppProfileSlot slot)
    {
        slot = number >= 1 && number <= _slots.Count ? _slots[number - 1] : new HidppProfileSlot(0, false);
        return slot.Sector > 0;
    }

    // Caller holds _lock.
    private MouseResult Commit(int sector, HidppOnboardProfile current, HidppOnboardProfile next)
    {
        if (current.ToBytes().AsSpan().SequenceEqual(next.ToBytes()))
        {
            return MouseResult.Done;
        }
        if (HidppOnboardProfiles.Open(_channel) is not { } onboard)
        {
            return MouseResult.Failed("the mouse has no onboard memory");
        }
        try
        {
            Backup(sector, current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return MouseResult.Failed($"backup failed, nothing was written: {ex.Message}");
        }
        var bytes = next.ToBytes();
        var landed = onboard.WriteSector(sector, bytes) && onboard.ReadSector(sector, bytes.Length) is { } back && back.AsSpan().SequenceEqual(bytes);
        if (!landed)
        {
            var restored = onboard.WriteSector(sector, current.ToBytes())
                && onboard.ReadSector(sector, bytes.Length) is { } again && again.AsSpan().SequenceEqual(current.ToBytes());
            ServiceLog.Warn($"[{Tag}] sector 0x{sector:X4} did not read back as written; previous contents {(restored ? "restored" : "NOT restored")}");
            return MouseResult.Failed(restored ? "the mouse did not keep the change; the profile is as it was" : "the write failed; restore the profile from its backup");
        }
        _profiles[sector] = next;
        ServiceLog.Info($"[{Tag}] wrote profile sector 0x{sector:X4}");
        // The mouse reads the active profile when it is selected; select it again so the change applies now.
        if (sector == _currentSector)
        {
            onboard.SetCurrentSector(sector);
        }
        return MouseResult.Done;
    }

    // Caller holds _lock.
    private void Load()
    {
        _loadedGeneration = _channel.Generation;
        _profiles.Clear();
        _slots = Array.Empty<HidppProfileSlot>();
        _info = null;
        _error = null;
        var onboard = HidppOnboardProfiles.Open(_channel);
        var info = onboard?.GetInfo();
        if (onboard is null || info is null || info.SectorSize < 64)
        {
            _error = "the mouse did not answer the onboard memory questions";
            return;
        }
        _mode = onboard.GetMode();
        _currentSector = onboard.GetCurrentSector();
        var directory = onboard.ReadSector(0, info.SectorSize);
        if (directory is null)
        {
            _error = "the profile directory could not be read";
            return;
        }
        _slots = HidppOnboardProfiles.ParseDirectory(directory).Where(s => s.Sector is > 0 and < HidppOnboardProfiles.RomSectorBase).ToList();
        foreach (var slot in _slots)
        {
            if (onboard.ReadSector(slot.Sector, info.SectorSize) is { } bytes && HidppOnboardProfile.Parse(bytes) is { } profile)
            {
                _profiles[slot.Sector] = profile;
                KeepOriginal(slot.Sector, profile);
            }
        }
        _rates = ReadReportRates();
        _info = info;
        ServiceLog.Info($"[{Tag}] {_model.Name}: {_profiles.Count} profiles, mode {_mode}, active sector 0x{_currentSector:X4}");
    }

    private IReadOnlyList<int> ReadReportRates()
    {
        if (_channel.FeatureIndex(ReportRateFeature) is not { } index || _channel.Call(index, 0, ReadOnlySpan<byte>.Empty) is not { } p)
        {
            return new[] { 1000, 500, 250, 125 };
        }
        var rates = new List<int>();
        for (var bit = 0; bit < 8; bit++)
        {
            if ((p[0] & (1 << bit)) != 0 && 1000 % (bit + 1) == 0)
            {
                rates.Add(1000 / (bit + 1));
            }
        }
        return rates;
    }

    private string DeviceDir() => Path.Combine(_backupRoot, _model.Name.Replace(' ', '-').ToLowerInvariant());

    private string OriginalPath(int sector) => Path.Combine(DeviceDir(), $"original-sector-{sector:X4}.bin");

    // The first copy of each sector is kept for good; later backups are per write.
    private void KeepOriginal(int sector, HidppOnboardProfile profile)
    {
        if (!profile.CrcValid)
        {
            return;
        }
        try
        {
            var path = OriginalPath(sector);
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(DeviceDir());
                File.WriteAllBytes(path, profile.ToBytes());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ServiceLog.Warn($"[{Tag}] could not keep the original of sector 0x{sector:X4}: {ex.Message}");
        }
    }

    private void Backup(int sector, HidppOnboardProfile profile)
    {
        Directory.CreateDirectory(DeviceDir());
        KeepOriginal(sector, profile);
        File.WriteAllBytes(Path.Combine(DeviceDir(), $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-sector-{sector:X4}.bin"), profile.ToBytes());
    }

    private static LogitechMouseProfileDto ToDto(int number, HidppProfileSlot slot, HidppOnboardProfile p, int buttons, bool canRestore) => new()
    {
        Number = number,
        Sector = slot.Sector,
        Enabled = slot.Enabled,
        Name = p.Name,
        ReportRateHz = p.ReportIntervalMs > 0 ? 1000 / p.ReportIntervalMs : 0,
        Dpi = p.Dpi.ToList(),
        DefaultDpiIndex = p.DefaultDpiIndex,
        ShiftDpiIndex = p.ShiftDpiIndex,
        Buttons = Enumerable.Range(0, buttons).Select(i => ToDto(i, p.Button(i))).ToList(),
        GShiftButtons = Enumerable.Range(0, buttons).Select(i => ToDto(i, p.GShiftButton(i))).ToList(),
        CrcValid = p.CrcValid,
        CanRestore = canRestore,
    };

    private static LogitechMouseButtonDto ToDto(int slot, HidppButtonBinding b) => new()
    {
        Slot = slot,
        Kind = b.Kind.ToString().ToLowerInvariant(),
        Code = b.Code,
        Modifiers = b.Modifiers,
        Raw = b.Kind == HidppButtonKind.Raw ? Convert.ToHexString(b.Encode()) : null,
    };

    private static HidppOnboardProfileEdit ToEdit(LogitechMouseProfileRequest r) => new()
    {
        Name = r.Name,
        ReportIntervalMs = r.ReportRateHz switch
        {
            null => null,
            1000 => 1,
            500 => 2,
            250 => 4,
            125 => 8,
            var hz => throw new ArgumentException($"report rate {hz} Hz is not one of 1000, 500, 250, 125"),
        },
        Dpi = r.Dpi,
        DefaultDpiIndex = r.DefaultDpiIndex,
        ShiftDpiIndex = r.ShiftDpiIndex,
        Buttons = ToBindings(r.Buttons),
        GShiftButtons = ToBindings(r.GShiftButtons),
    };

    private static Dictionary<int, HidppButtonBinding>? ToBindings(List<LogitechMouseButtonDto>? list)
    {
        if (list is null)
        {
            return null;
        }
        var map = new Dictionary<int, HidppButtonBinding>();
        foreach (var b in list)
        {
            if (b.Slot is < 0 or >= HidppOnboardProfile.ButtonSlots)
            {
                throw new ArgumentException($"button slot {b.Slot} out of range");
            }
            map[b.Slot] = b.Kind switch
            {
                "mouse" => HidppButtonBinding.Mouse(b.Code),
                "key" => HidppButtonBinding.Key(b.Code, b.Modifiers),
                "consumer" => HidppButtonBinding.Consumer(b.Code),
                "special" when HidppButtonBinding.SpecialNames.ContainsKey(b.Code) => HidppButtonBinding.Special(b.Code),
                "raw" when b.Raw is { Length: 8 } hex => HidppButtonBinding.Decode(Convert.FromHexString(hex)),
                _ => throw new ArgumentException($"button slot {b.Slot}: unsupported binding \"{b.Kind}\""),
            };
        }
        return map;
    }
}
