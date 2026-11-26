using System;
using System.Buffers.Binary;
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
    // Sectors that hold (or may take) macros, as last read or written; null when unreadable.
    private readonly Dictionary<int, byte[]?> _macroSectors = new();
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
            var macroSectors = MacroSectorRange().ToList();
            var live = LiveMacros().Select(m => m.Sector).ToHashSet();
            state.MacroSectors = macroSectors.Count;
            state.MacroSectorsFree = macroSectors.Count(s => !live.Contains(s));
            return state;
        }
    }

    public MouseResult UpdateProfile(int number, LogitechMouseProfileRequest request)
    {
        HidppOnboardProfileEdit edit;
        var newMacros = new List<(bool Shift, int Slot, HidppOnboardMacro Macro)>();
        try
        {
            edit = ToEdit(request, newMacros);
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
            if (newMacros.Count > 0 && PlaceMacros(newMacros, ref edit) is { } failed)
            {
                return failed;
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
            // The mouse will not switch to a profile that is out of the cycle; put it in first.
            if (!slot.Enabled && SetCycle(number, slot, true) is { Ok: false } refused)
            {
                return refused;
            }
            if (HidppOnboardProfiles.Open(_channel) is not { } onboard || !onboard.SetCurrentSector(slot.Sector))
            {
                return MouseResult.Failed("the mouse did not switch profiles");
            }
            _currentSector = onboard.GetCurrentSector() ?? slot.Sector;
            return MouseResult.Done;
        }
    }

    /// <summary>Puts a profile in the cycle the profile button steps through, or takes it out (the profile directory).</summary>
    public MouseResult SetProfileEnabled(int number, bool enabled)
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
            return SetCycle(number, slot, enabled);
        }
    }

    // Caller holds _lock and has checked Ready(). Backs up the directory, writes it whole, reads it back.
    private MouseResult SetCycle(int number, HidppProfileSlot slot, bool enabled)
    {
        if (slot.Enabled == enabled)
        {
            return MouseResult.Done;
        }
        if (!enabled && _slots.Count(s => s.Enabled) <= 1)
        {
            return MouseResult.Invalid("at least one profile stays in the cycle");
        }
        if (HidppOnboardProfiles.Open(_channel) is not { } onboard || _info is null)
        {
            return MouseResult.Failed("the mouse has no onboard memory");
        }
        var current = onboard.ReadSector(0, _info.SectorSize);
        var entry = current is null ? -1 : HidppOnboardProfiles.DirectoryEntry(current, slot.Sector);
        if (current is null || entry < 0)
        {
            return MouseResult.Failed("the profile directory could not be read");
        }
        var size = current.Length;
        if (BinaryPrimitives.ReadUInt16BigEndian(current.AsSpan(size - 2)) != HidppOnboardProfile.Crc16(current.AsSpan(0, size - 2)))
        {
            return MouseResult.Failed("the profile directory does not check out; it was left alone");
        }
        var next = current.ToArray();
        next[entry + 2] = (byte)(enabled ? 1 : 0);
        BinaryPrimitives.WriteUInt16BigEndian(next.AsSpan(size - 2), HidppOnboardProfile.Crc16(next.AsSpan(0, size - 2)));
        try
        {
            Directory.CreateDirectory(DeviceDir());
            var original = Path.Combine(DeviceDir(), "original-sector-0000.bin");
            if (!File.Exists(original))
            {
                File.WriteAllBytes(original, current);
            }
            File.WriteAllBytes(Path.Combine(DeviceDir(), $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-sector-0000.bin"), current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return MouseResult.Failed($"backup failed, nothing was written: {ex.Message}");
        }
        var landed = onboard.WriteDirectory(next) && onboard.ReadSector(0, size) is { } back && back.AsSpan().SequenceEqual(next);
        if (!landed)
        {
            var restored = onboard.WriteDirectory(current) && onboard.ReadSector(0, size) is { } again && again.AsSpan().SequenceEqual(current);
            ServiceLog.Warn($"[{Tag}] directory did not read back as written; previous contents {(restored ? "restored" : "NOT restored")}");
            return MouseResult.Failed(restored ? "the mouse did not keep the change; the cycle is as it was" : "the directory write failed");
        }
        _slots = HidppOnboardProfiles.ParseDirectory(next).Where(s => s.Sector is > 0 and < HidppOnboardProfiles.RomSectorBase).ToList();
        ServiceLog.Info($"[{Tag}] profile {number} {(enabled ? "added to" : "taken out of")} the cycle");
        return MouseResult.Done;
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
        _macroSectors.Clear();
        foreach (var sector in LiveMacros().Select(m => m.Sector).Distinct())
        {
            _macroSectors[sector] = onboard.ReadSector(sector, info.SectorSize);
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

    private LogitechMouseProfileDto ToDto(int number, HidppProfileSlot slot, HidppOnboardProfile p, int buttons, bool canRestore) => new()
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

    private LogitechMouseButtonDto ToDto(int slot, HidppButtonBinding b)
    {
        var dto = new LogitechMouseButtonDto
        {
            Slot = slot,
            Kind = b.Kind.ToString().ToLowerInvariant(),
            Code = b.Code,
            Modifiers = b.Modifiers,
            Raw = b.Kind == HidppButtonKind.Raw ? Convert.ToHexString(b.Encode()) : null,
        };
        if (b.Kind == HidppButtonKind.Macro)
        {
            var macro = MacroAt(b.Code, b.Modifiers);
            dto.MacroEditable = macro?.Editable ?? false;
            dto.Macro = macro is { Editable: true } ? macro.Steps.Select(ToDto).ToList() : null;
        }
        return dto;
    }

    private static readonly IReadOnlyDictionary<HidppMacroStepKind, string> StepNames = new Dictionary<HidppMacroStepKind, string>
    {
        [HidppMacroStepKind.KeyDown] = "keyDown",
        [HidppMacroStepKind.KeyUp] = "keyUp",
        [HidppMacroStepKind.Delay] = "delay",
        [HidppMacroStepKind.MouseDown] = "mouseDown",
        [HidppMacroStepKind.MouseUp] = "mouseUp",
        [HidppMacroStepKind.ConsumerDown] = "consumerDown",
        [HidppMacroStepKind.ConsumerUp] = "consumerUp",
        [HidppMacroStepKind.Wheel] = "wheel",
        [HidppMacroStepKind.HWheel] = "hwheel",
    };

    private static LogitechMouseMacroStepDto ToDto(HidppMacroStep s) => new() { Type = StepNames[s.Kind], Code = s.Code, Modifiers = s.Modifiers };

    private static HidppMacroStep ToStep(LogitechMouseMacroStepDto s)
    {
        foreach (var (kind, name) in StepNames)
        {
            if (string.Equals(name, s.Type, StringComparison.Ordinal))
            {
                return new HidppMacroStep(kind, s.Code, s.Modifiers);
            }
        }
        throw new ArgumentException($"unknown macro step \"{s.Type}\"");
    }

    // Caller holds _lock. Every macro binding in the stored profiles, both layers.
    private IEnumerable<(int Sector, int Address)> LiveMacros()
    {
        foreach (var profile in _profiles.Values)
        {
            for (var i = 0; i < HidppOnboardProfile.ButtonSlots; i++)
            {
                foreach (var b in new[] { profile.Button(i), profile.GShiftButton(i) })
                {
                    if (b.Kind == HidppButtonKind.Macro)
                    {
                        yield return (b.Code, b.Modifiers);
                    }
                }
            }
        }
    }

    // Caller holds _lock.
    private HidppOnboardMacro? MacroAt(int sector, int address) =>
        _macroSectors.TryGetValue(sector, out var bytes) && bytes is not null ? HidppOnboardMacro.Parse(bytes, address) : null;

    // Caller holds _lock. User sectors after the profiles' own (sectors 1..profile count), below the factory ones.
    private IEnumerable<int> MacroSectorRange()
    {
        if (_info is not { } info)
        {
            yield break;
        }
        var profileSectors = _slots.Select(s => s.Sector).ToHashSet();
        var first = Math.Max(info.ProfileCount, profileSectors.Count > 0 ? profileSectors.Max() : 0) + 1;
        var last = Math.Min(info.SectorCount, HidppOnboardProfiles.RomSectorBase) - 1;
        for (var s = first; s <= last; s++)
        {
            if (!profileSectors.Contains(s))
            {
                yield return s;
            }
        }
    }

    /// <summary>
    /// Caller holds _lock. Stores each new macro in the onboard memory and points its binding at
    /// it: a macro the memory already holds is reused; otherwise it goes after the macros in use in
    /// a macro sector with room (16-byte aligned), or at the start of a sector no binding uses. The
    /// sector is backed up, written whole with its CRC and read back; one that does not read back
    /// as written gets its previous bytes again.
    /// </summary>
    private MouseResult? PlaceMacros(List<(bool Shift, int Slot, HidppOnboardMacro Macro)> macros, ref HidppOnboardProfileEdit edit)
    {
        if (HidppOnboardProfiles.Open(_channel) is not { } onboard || _info is not { } info)
        {
            return MouseResult.Failed("the mouse has no onboard memory");
        }
        var size = info.SectorSize;
        var buttons = new Dictionary<int, HidppButtonBinding>(edit.Buttons ?? new Dictionary<int, HidppButtonBinding>());
        var shifted = new Dictionary<int, HidppButtonBinding>(edit.GShiftButtons ?? new Dictionary<int, HidppButtonBinding>());
        // Macros written in this request, not yet referenced by a stored profile.
        var placed = new List<(int Sector, int Address, int Length)>();
        foreach (var (shift, slot, macro) in macros)
        {
            (int Sector, int Address)? at = null;
            foreach (var (sector, address) in LiveMacros().Concat(placed.Select(p => (p.Sector, p.Address))))
            {
                if (MacroAt(sector, address) is { } existing && existing.Bytes.AsSpan().SequenceEqual(macro.Bytes))
                {
                    at = (sector, address);
                    break;
                }
            }
            if (at is null)
            {
                foreach (var sector in MacroSectorRange().ToList())
                {
                    if (!_macroSectors.TryGetValue(sector, out var bytes))
                    {
                        bytes = onboard.ReadSector(sector, size);
                        _macroSectors[sector] = bytes;
                    }
                    if (bytes is null)
                    {
                        continue;
                    }
                    var inUse = LiveMacros().Where(m => m.Sector == sector)
                        .Select(m => (m.Address, Length: MacroAt(m.Sector, m.Address)?.Bytes.Length ?? 0))
                        .Concat(placed.Where(p => p.Sector == sector).Select(p => (p.Address, p.Length)))
                        .ToList();
                    if (inUse.Any(m => m.Length == 0))
                    {
                        continue; // a binding points into this sector at something unreadable; leave it alone
                    }
                    var start = inUse.Count == 0 ? 0 : (inUse.Max(m => m.Address + m.Length) + 15) / 16 * 16;
                    if (start + macro.Bytes.Length > size - 2)
                    {
                        continue;
                    }
                    var next = inUse.Count == 0 ? Enumerable.Repeat((byte)0xFF, size).ToArray() : (byte[])bytes.Clone();
                    macro.Bytes.CopyTo(next, start);
                    BinaryPrimitives.WriteUInt16BigEndian(next.AsSpan(size - 2), HidppOnboardProfile.Crc16(next.AsSpan(0, size - 2)));
                    if (WriteMacroSector(onboard, sector, bytes, next) is { } failed)
                    {
                        return failed;
                    }
                    at = (sector, start);
                    placed.Add((sector, start, macro.Bytes.Length));
                    break;
                }
            }
            if (at is not { } where)
            {
                return MouseResult.Failed("the onboard memory has no room left for this macro; remove a macro from another button first");
            }
            (shift ? shifted : buttons)[slot] = HidppButtonBinding.Macro(where.Sector, where.Address);
        }
        edit = edit.WithButtons(buttons.Count > 0 ? buttons : edit.Buttons, shifted.Count > 0 ? shifted : edit.GShiftButtons);
        return null;
    }

    // Caller holds _lock.
    private MouseResult? WriteMacroSector(HidppOnboardProfiles onboard, int sector, byte[] previous, byte[] next)
    {
        try
        {
            if (previous.Any(b => b != 0xFF))
            {
                Directory.CreateDirectory(DeviceDir());
                File.WriteAllBytes(Path.Combine(DeviceDir(), $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-sector-{sector:X4}.bin"), previous);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return MouseResult.Failed($"backup failed, nothing was written: {ex.Message}");
        }
        if (previous.AsSpan().SequenceEqual(next))
        {
            return null;
        }
        var landed = onboard.WriteSector(sector, next) && onboard.ReadSector(sector, next.Length) is { } back && back.AsSpan().SequenceEqual(next);
        if (!landed)
        {
            var restored = onboard.WriteSector(sector, previous)
                && onboard.ReadSector(sector, previous.Length) is { } again && again.AsSpan().SequenceEqual(previous);
            _macroSectors[sector] = restored ? previous : null;
            ServiceLog.Warn($"[{Tag}] macro sector 0x{sector:X4} did not read back as written; previous contents {(restored ? "restored" : "NOT restored")}");
            return MouseResult.Failed("the mouse did not keep the macro; nothing changed");
        }
        _macroSectors[sector] = next;
        ServiceLog.Info($"[{Tag}] wrote macro sector 0x{sector:X4}");
        return null;
    }

    private static HidppOnboardProfileEdit ToEdit(LogitechMouseProfileRequest r, List<(bool Shift, int Slot, HidppOnboardMacro Macro)> newMacros) => new()
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
        Buttons = ToBindings(r.Buttons, false, newMacros),
        GShiftButtons = ToBindings(r.GShiftButtons, true, newMacros),
    };

    private static Dictionary<int, HidppButtonBinding>? ToBindings(List<LogitechMouseButtonDto>? list, bool shift, List<(bool Shift, int Slot, HidppOnboardMacro Macro)> newMacros)
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
            if (b.Kind == "macro")
            {
                if (b.Macro is { } steps)
                {
                    // Placed, and its binding set, once the memory is locked.
                    newMacros.Add((shift, b.Slot, HidppOnboardMacro.FromSteps(steps.Select(ToStep).ToList())));
                    continue;
                }
                if (b.Code <= 0)
                {
                    throw new ArgumentException($"button slot {b.Slot}: a macro binding needs its steps or its sector");
                }
                map[b.Slot] = HidppButtonBinding.Macro(b.Code, b.Modifiers);
                continue;
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
