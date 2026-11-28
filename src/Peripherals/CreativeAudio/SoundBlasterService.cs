using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Nexus.Service.Activity;
using Nexus.Service.Media;
using Nexus.Service.Models.Devices;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Serialization;
using static Nexus.Service.Peripherals.CreativeAudio.SoundBlasterProtocol;

namespace Nexus.Service.Peripherals.CreativeAudio;

/// <summary>Outcome of a change to a Sound Blaster.</summary>
public sealed record SoundBlasterResult(bool Ok, string Status, string? Message = null)
{
    public static readonly SoundBlasterResult Done = new(true, "ok");
    public static SoundBlasterResult NotPresent => new(false, "not-present", "no supported Sound Blaster is connected");
    public static SoundBlasterResult Invalid(string message) => new(false, "invalid", message);
    public static SoundBlasterResult Failed(string message) => new(false, "failed", message);
}

/// <summary>
/// A Creative Sound Blaster G8 / G6 the way Creative App drives it (see <see cref="SoundBlasterProtocol"/>):
/// reads the SBX effects, EQ, Scout Mode, mic processing (with Voice Morph and the mic EQ), output, DAC filter
/// and device features (Direct Mode, S/PDIF-Out Direct Mode, logo LED, energy saving, volume sync), and changes
/// them. The settings live in the device; Nexus keeps only the user's saved modes. The mixer is Windows' own
/// volume of the card's endpoints (the G8's mixer is USB Audio Class, not this HID protocol).
/// </summary>
public sealed class SoundBlasterService
{
    private const string Brand = "Creative";
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(1);

    // The parameters the page shows: SBX (surround, Dialog+, Smart Volume, Crystalizer), EQ toggle, pre-amp and
    // ten bands, bass crossover / toggle / level (MalcolmPlaybackManagerCommand); echo cancellation, noise reduction and
    // mic Smart Volume (MalcolmVoiceInputManagerCommand); the decoder dynamic range.
    private static readonly byte[] PlaybackIds =
        Enumerable.Range(0x00, 0x15).Select(i => (byte)i).Concat(new byte[] { 0x17, 0x18, 0x19 }).ToArray();
    private static readonly byte[] RecordingIds =
        new byte[] { 0x00, 0x04, 0x05 }.Concat(Enumerable.Range(0x0A, 0x12).Select(i => (byte)i)).Concat(new byte[] { 0x2C, 0x2D }).ToArray();

    private static readonly (string Name, byte Bit)[] FeatureBits =
    {
        ("directMode", FeatureDirectMode),
        ("speakerVirtualization", FeatureSpeakerVirtualization),
        ("spdifDirect", FeatureSpdifDirect),
        ("logoLed", FeatureLogoLed),
        ("energySaving", FeatureEnergySaving),
        ("volumeSync", FeatureVolumeSync),
    };

    private static readonly TimeSpan EndpointsFresh = TimeSpan.FromSeconds(30);
    private const int ModeNameMax = 40;
    private static readonly byte[] DecoderIds = { 0x02 };

    // The effect switches and the EQ: the SBX master turns them all off, and Scout Mode swaps them for its own
    // (Smart Volume and the EQ). The G8 does not put them back as they were (after Scout Mode it leaves Surround
    // off, after SBX on with nothing remembered it turns on Surround alone), so Nexus keeps them and puts them back.
    private static readonly byte[] EffectSwitches = { 0x00, 0x02, 0x04, 0x07, 0x09, 0x18 };

    private readonly ISoundBlasterChannel _channel;
    private readonly object _lock = new();
    private SoundBlasterStateDto? _state;
    private DateTime _readAt;
    private string? _identityOf;
    private (string? Firmware, string? Serial, List<string> Outputs, List<int> Filters, uint Features) _identity = (null, null, new(), new(), 0);
    private Dictionary<byte, float>? _effectsBefore;
    private readonly IAudioDeviceProvider? _audio;
    private readonly IVolumeProvider? _volume;
    private readonly string? _modesPath;
    private List<SoundBlasterMode>? _modes;
    private List<(string Id, string Name, string Direction)> _endpoints = new();
    private DateTime _endpointsAt;

    public SoundBlasterService(IHidEnumerator hid, IAudioDeviceProvider? audio = null, IVolumeProvider? volume = null)
        : this(new SoundBlasterChannel(hid), audio, volume, MediaLibrary.DeviceStoreDir("creative-soundblaster"))
    {
    }

    /// <summary>For tests: modes are kept in memory when <paramref name="storeDir"/> is null.</summary>
    internal SoundBlasterService(ISoundBlasterChannel channel, IAudioDeviceProvider? audio = null, IVolumeProvider? volume = null, string? storeDir = null)
    {
        _channel = channel;
        _audio = audio;
        _volume = volume;
        _modesPath = storeDir is null ? null : Path.Combine(storeDir, "modes.json");
    }

    /// <summary>The connected model's name ("Sound Blaster G8"), or null.</summary>
    public string? PresentModel() => _channel.Present()?.Name;

    public SoundBlasterStateDto GetState()
    {
        lock (_lock)
        {
            if (_state is { } cached && DateTime.UtcNow - _readAt < Fresh)
            {
                return Copy(cached);
            }
            var model = _channel.Present();
            if (model is null)
            {
                _state = null;
                _identityOf = null;
                return new SoundBlasterStateDto();
            }
            _state = Read(model);
            _readAt = DateTime.UtcNow;
            return Copy(_state);
        }
    }

    public SoundBlasterResult SetParameter(int? family, int? id, float? value)
    {
        if (family is not (Recording or Playback or Decoder) || id is not (>= 0 and <= 0x7F) || value is not { } v
            || !float.IsFinite(v) || v < -100 || v > 1000)
        {
            return SoundBlasterResult.Invalid("family 0x95-0x97, id 0-127 and a finite value are required");
        }
        return Write(WriteParameter((byte)family, (byte)id, v), state => state.Values[Key((byte)family, (byte)id)] = v);
    }

    /// <summary>The SBX master: off turns every effect and the EQ off; on puts back the ones that were on.</summary>
    public SoundBlasterResult SetSbx(bool on)
    {
        lock (_lock)
        {
            return Master(WriteProfile(0x01, on), remember: !on, restore: on);
        }
    }

    /// <summary>Scout Mode: it takes the effects over while on; off puts back the ones that were on before it.</summary>
    public SoundBlasterResult SetScout(bool on)
    {
        lock (_lock)
        {
            return Master(WriteProfile(0x02, on), remember: on && _state?.Scout != true, restore: !on);
        }
    }

    // Caller holds _lock. The device changes many switches at once here, so the copy is read again afterwards.
    private SoundBlasterResult Master(byte[] request, bool remember, bool restore)
    {
        if (remember && ReadSwitches() is { } before && before.Values.Any(v => v >= 0.5f))
        {
            _effectsBefore = before;
        }
        var result = Write(request, _ => { });
        if (result.Ok && restore && _effectsBefore is { } wanted)
        {
            _effectsBefore = null;
            if (ReadSwitches() is { } now)
            {
                foreach (var (id, value) in wanted)
                {
                    if (now.TryGetValue(id, out var current) && (current >= 0.5f) != (value >= 0.5f))
                    {
                        Write(WriteParameter(Playback, id, value), _ => { });
                    }
                }
            }
        }
        _readAt = DateTime.MinValue;
        return result;
    }

    // Caller holds _lock. Null when one of them did not answer.
    private Dictionary<byte, float>? ReadSwitches()
    {
        var values = new Dictionary<byte, float>();
        foreach (var id in EffectSwitches)
        {
            if (ReadOne(Playback, id) is not { } value)
            {
                return null;
            }
            values[id] = value;
        }
        return values;
    }

    // Caller holds _lock.
    private float? ReadOne(byte family, byte id)
    {
        var reply = _channel.Exchange(ReadParameter(family, id),
            r => (TryParseParameter(r, out var f, out var i, out _) && f == family && i == id)
                || (TryParseAck(r, out var c, out _) && c == GetParameter));
        return reply is not null && TryParseParameter(reply, out _, out _, out var value) ? value : null;
    }

    public SoundBlasterResult SetOutput(string? output)
    {
        byte? code = output switch { "speakers" => OutputSpeakers, "headphones" => OutputHeadphones, _ => null };
        return code is { } c ? Write(WriteOutput(c), state => state.Output = output) : SoundBlasterResult.Invalid("output is \"speakers\" or \"headphones\"");
    }

    public SoundBlasterResult SetFilter(int? filter) =>
        filter is >= 0 and <= 10
            ? Write(WriteFilter((byte)filter), state => state.DacFilter = filter)
            : SoundBlasterResult.Invalid("filter 0-10 is required");

    public SoundBlasterResult SetMicBoost(int? db) =>
        db is 0 or 10 or 20 or 30
            ? Write(WriteMicBoost((byte)db), state => state.MicBoost = db)
            : SoundBlasterResult.Invalid("mic boost is 0, 10, 20 or 30 dB");

    /// <summary>Turns a device feature on or off by its name in <see cref="SoundBlasterStateDto.Features"/>.</summary>
    public SoundBlasterResult SetFeature(string? name, bool? on)
    {
        var entry = FeatureBits.FirstOrDefault(f => f.Name == name);
        if (entry.Name is null || on is not { } value)
        {
            return SoundBlasterResult.Invalid("a known feature name and enabled are required");
        }
        lock (_lock)
        {
            var result = Write(WriteFeature(entry.Bit, value), _ => { });
            // Direct Mode on makes the G8 drop off USB and come back (it changes its audio interfaces).
            if (result.Ok && entry.Bit == FeatureDirectMode && value)
            {
                WaitForReturn();
            }
            _readAt = DateTime.MinValue;
            return result;
        }
    }

    /// <summary>Writes a Voice Morph character's parameters (the switch is recording 0x0A).</summary>
    public SoundBlasterResult SetVoiceMorph(string? id) =>
        SoundBlasterPresets.Voices.FirstOrDefault(v => v.Id == id) is { } voice
            ? WriteRecordingBlock(SoundBlasterPresets.VoiceFxFirst, voice.Values)
            : SoundBlasterResult.Invalid("unknown Voice Morph character");

    /// <summary>Writes a microphone EQ preset's band gains (the switch is recording 0x13).</summary>
    public SoundBlasterResult SetMicEq(string? id) =>
        SoundBlasterPresets.MicEq.FirstOrDefault(p => p.Id == id) is { } preset
            ? WriteRecordingBlock(SoundBlasterPresets.MicEqFirst, preset.Bands)
            : SoundBlasterResult.Invalid("unknown microphone EQ preset");

    /// <summary>Sets the Windows volume or mute of one of the card's endpoints (ids from <see cref="SoundBlasterStateDto.Mixer"/>).</summary>
    public SoundBlasterResult SetMixer(string? id, double? volume, bool? muted)
    {
        if (_volume is null)
        {
            return SoundBlasterResult.Failed("no mixer on this platform");
        }
        lock (_lock)
        {
            if (_channel.Present() is not { } model)
            {
                return SoundBlasterResult.NotPresent;
            }
            if (id is null || Endpoints(model).All(e => e.Id != id) || (volume is null && muted is null)
                || volume is { } v && (!double.IsFinite(v) || v < 0 || v > 1))
            {
                return SoundBlasterResult.Invalid("one of the Sound Blaster's endpoints, with a volume 0-1 or muted");
            }
            if (volume is { } level)
            {
                _volume.SetVolume(id, level);
            }
            if (muted is { } mute)
            {
                _volume.SetMuted(id, mute);
            }
            _readAt = DateTime.MinValue;
            return SoundBlasterResult.Done;
        }
    }

    /// <summary>Keeps the current playback settings (effects, EQ, decoder) under <paramref name="name"/>; the same name replaces it.</summary>
    public SoundBlasterResult SaveMode(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length is 0 or > ModeNameMax)
        {
            return SoundBlasterResult.Invalid($"a mode name of 1-{ModeNameMax} characters is required");
        }
        lock (_lock)
        {
            if (_channel.Present() is not { } model)
            {
                return SoundBlasterResult.NotPresent;
            }
            _readAt = DateTime.MinValue;
            var state = Read(model);
            var values = state.Values.Where(kv => kv.Key.StartsWith("96:") || kv.Key.StartsWith("97:"))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            if (values.Count == 0)
            {
                return SoundBlasterResult.Failed("the Sound Blaster did not answer");
            }
            var modes = Modes();
            modes.RemoveAll(m => string.Equals(m.Name, trimmed, StringComparison.OrdinalIgnoreCase));
            modes.Add(new SoundBlasterMode { Name = trimmed, Values = values, SavedAt = DateTime.UtcNow });
            return SaveModes(modes);
        }
    }

    /// <summary>Writes a saved mode back: only the values that differ, the effect switches last.</summary>
    public SoundBlasterResult ApplyMode(string? name)
    {
        lock (_lock)
        {
            if (_channel.Present() is not { } model)
            {
                return SoundBlasterResult.NotPresent;
            }
            if (Modes().FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)) is not { } mode)
            {
                return SoundBlasterResult.Invalid("no such mode");
            }
            var now = Read(model).Values;
            var changes = mode.Values
                .Where(kv => !now.TryGetValue(kv.Key, out var current) || Math.Abs(current - kv.Value) > 0.0005f)
                .Select(kv => (Key: kv.Key, Value: kv.Value, Parsed: ParseKey(kv.Key)))
                .Where(c => c.Parsed is not null)
                .OrderBy(c => c.Parsed!.Value.Family == Playback && EffectSwitches.Contains(c.Parsed.Value.Id))
                .ToList();
            foreach (var change in changes)
            {
                var (family, id) = change.Parsed!.Value;
                var result = Write(WriteParameter(family, id, change.Value), _ => { });
                if (!result.Ok)
                {
                    _readAt = DateTime.MinValue;
                    return result;
                }
            }
            _readAt = DateTime.MinValue;
            return SoundBlasterResult.Done;
        }
    }

    public SoundBlasterResult RenameMode(string? name, string? to)
    {
        var trimmed = to?.Trim() ?? "";
        if (trimmed.Length is 0 or > ModeNameMax)
        {
            return SoundBlasterResult.Invalid($"a mode name of 1-{ModeNameMax} characters is required");
        }
        lock (_lock)
        {
            var modes = Modes();
            if (modes.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)) is not { } mode)
            {
                return SoundBlasterResult.Invalid("no such mode");
            }
            if (modes.Any(m => m != mode && string.Equals(m.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
            {
                return SoundBlasterResult.Invalid("a mode with that name exists");
            }
            mode.Name = trimmed;
            return SaveModes(modes);
        }
    }

    public SoundBlasterResult DeleteMode(string? name)
    {
        lock (_lock)
        {
            var modes = Modes();
            if (modes.RemoveAll(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)) == 0)
            {
                return SoundBlasterResult.Invalid("no such mode");
            }
            return SaveModes(modes);
        }
    }

    internal static string Key(byte family, byte id) => $"{family:x2}:{id:x2}";

    private static (byte Family, byte Id)? ParseKey(string key) =>
        key.Length == 5 && key[2] == ':'
        && byte.TryParse(key.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var family)
        && byte.TryParse(key.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber, null, out var id)
        && family is Recording or Playback or Decoder
            ? (family, id)
            : null;

    private SoundBlasterResult WriteRecordingBlock(byte first, IReadOnlyList<float> values)
    {
        lock (_lock)
        {
            for (var i = 0; i < values.Count; i++)
            {
                var id = (byte)(first + i);
                var result = Write(WriteParameter(Recording, id, values[i]), state => state.Values[Key(Recording, id)] = values[i]);
                if (!result.Ok)
                {
                    return result;
                }
            }
            _readAt = DateTime.MinValue;
            return SoundBlasterResult.Done;
        }
    }

    // Caller holds _lock.
    private void WaitForReturn()
    {
        Thread.Sleep(1500);
        var until = Environment.TickCount64 + 8000;
        while (Environment.TickCount64 < until)
        {
            if (_channel.Present() is not null && _channel.Exchange(ReadFeatures(), r => ParseFeatureMask(r) is not null) is not null)
            {
                return;
            }
            Thread.Sleep(500);
        }
        ServiceLog.Warn("[creative-soundblaster] the Sound Blaster did not come back after Direct Mode turned on");
    }

    // Caller holds _lock. The card's own Windows endpoints (not the second port's, USB-2), cached for a while.
    private List<(string Id, string Name, string Direction)> Endpoints(SoundBlasterModel model)
    {
        if (_audio is null)
        {
            return new();
        }
        if (DateTime.UtcNow - _endpointsAt < EndpointsFresh && _endpoints.Count > 0)
        {
            return _endpoints;
        }
        static string Squash(string s) => s.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
        var wanted = Squash(model.Name);
        var list = _audio.ListDevices();
        _endpoints = list.Outputs.Select(d => (d, "playback")).Concat(list.Inputs.Select(d => (d, "recording")))
            .Where(x => Squash(x.d.Name).Contains(wanted) && !x.d.Name.Contains("USB-2", StringComparison.OrdinalIgnoreCase))
            .Select(x => (x.d.Id, Label(x.d.Name), x.Item2))
            .ToList();
        _endpointsAt = DateTime.UtcNow;
        return _endpoints;
    }

    // "Line (2- Sound Blaster G8 USB-1)" -> "Line".
    private static string Label(string name)
    {
        var cut = name.IndexOf(" (", StringComparison.Ordinal);
        return cut > 0 ? name[..cut] : name;
    }

    private List<SoundBlasterMixerChannelDto> ReadMixer(SoundBlasterModel model)
    {
        var channels = new List<SoundBlasterMixerChannelDto>();
        if (_volume is null)
        {
            return channels;
        }
        foreach (var (id, name, direction) in Endpoints(model))
        {
            var v = _volume.GetState(id);
            if (v.Supported)
            {
                channels.Add(new SoundBlasterMixerChannelDto { Id = id, Name = name, Direction = direction, Volume = v.Volume, Muted = v.Muted });
            }
        }
        return channels;
    }

    // Caller holds _lock.
    private List<SoundBlasterMode> Modes()
    {
        if (_modes is not null)
        {
            return _modes;
        }
        _modes = new();
        if (_modesPath is not null && File.Exists(_modesPath))
        {
            try
            {
                _modes = JsonSerializer.Deserialize(File.ReadAllText(_modesPath), AppJsonContext.Default.SoundBlasterModesFile)?.Modes ?? new();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                ServiceLog.Warn($"[creative-soundblaster] could not read {_modesPath}: {ex.Message}");
            }
        }
        return _modes;
    }

    // Caller holds _lock.
    private SoundBlasterResult SaveModes(List<SoundBlasterMode> modes)
    {
        _modes = modes;
        if (_modesPath is not null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_modesPath)!);
                AtomicJsonFile.Write(_modesPath, JsonSerializer.Serialize(new SoundBlasterModesFile { Modes = modes }, AppJsonContext.Default.SoundBlasterModesFile));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return SoundBlasterResult.Failed($"could not save the modes: {ex.Message}");
            }
        }
        if (_state is not null)
        {
            _state.Modes = modes.Select(m => m.Name).ToList();
        }
        _readAt = DateTime.MinValue;
        return SoundBlasterResult.Done;
    }

    private SoundBlasterResult Write(byte[] request, Action<SoundBlasterStateDto> apply)
    {
        lock (_lock)
        {
            if (_channel.Present() is null)
            {
                return SoundBlasterResult.NotPresent;
            }
            var command = request[2];
            var answer = _channel.Exchange(request, r => TryParseAck(r, out var c, out _) && c == command, Observe);
            if (answer is null)
            {
                return SoundBlasterResult.Failed("the Sound Blaster did not answer");
            }
            TryParseAck(answer, out _, out var status);
            if (status != 0)
            {
                return SoundBlasterResult.Failed($"the Sound Blaster refused it (status 0x{status:X2})");
            }
            if (_state is not null)
            {
                apply(_state);
            }
            return SoundBlasterResult.Done;
        }
    }

    // Caller holds _lock.
    private SoundBlasterStateDto Read(SoundBlasterModel model)
    {
        var state = new SoundBlasterStateDto
        {
            Present = true,
            Model = model.Name,
            Name = $"{Brand} {model.Name}",
        };
        var answered = 0;
        void Parameters(byte family, IEnumerable<byte> ids)
        {
            foreach (var id in ids)
            {
                if (ReadOne(family, id) is { } value)
                {
                    state.Values[Key(family, id)] = value;
                    answered++;
                }
            }
        }
        Parameters(Playback, PlaybackIds);
        Parameters(Recording, RecordingIds);
        Parameters(Decoder, DecoderIds);
        if (Ask(ReadProfile(), r => ParseProfile(r) is not null) is { } profile && ParseProfile(profile) is { } bits)
        {
            state.Sbx = (bits & ProfileSbx) != 0;
            state.Scout = (bits & ProfileScout) != 0;
            state.VoiceClarity = (bits & ProfileVoiceClarity) != 0;
            answered++;
        }
        if (Ask(ReadOutput(), r => ParseOutput(r) is not null) is { } output)
        {
            state.Output = OutputName(ParseOutput(output));
        }
        if (Ask(ReadFilter(), r => ParseFilter(r) is not null) is { } filter)
        {
            state.DacFilter = ParseFilter(filter);
        }
        if (Ask(ReadMicBoost(), r => ParseMicBoost(r) is not null) is { } boost)
        {
            state.MicBoost = ParseMicBoost(boost);
        }
        ReadIdentity(model);
        state.Firmware = _identity.Firmware;
        state.Serial = _identity.Serial;
        state.Outputs = _identity.Outputs.ToList();
        state.DacFilters = _identity.Filters.ToList();
        if (Ask(ReadFeatures(), r => ParseFeatureMask(r) is not null) is { } features && ParseFeatureMask(features) is { } on)
        {
            foreach (var (name, bit) in FeatureBits)
            {
                if ((_identity.Features & (1u << bit)) != 0)
                {
                    state.Features[name] = (on & (1u << bit)) != 0;
                }
            }
        }
        state.VoiceMorph = Block(state, SoundBlasterPresets.VoiceFxFirst) is { } voice ? SoundBlasterPresets.MatchVoice(voice) : null;
        state.MicEq = Block(state, SoundBlasterPresets.MicEqFirst) is { } bands ? SoundBlasterPresets.MatchMicEq(bands) : null;
        state.VoicePresets = SoundBlasterPresets.Voices.Select(v => new SoundBlasterPresetDto { Id = v.Id, Name = v.Name }).ToList();
        state.MicEqPresets = SoundBlasterPresets.MicEq.Select(p => new SoundBlasterPresetDto { Id = p.Id, Name = p.Name, Values = p.Bands.ToList() }).ToList();
        state.MicEqBandHz = SoundBlasterPresets.MicEqBandHz.ToList();
        state.Mixer = ReadMixer(model);
        state.Modes = Modes().Select(m => m.Name).ToList();
        state.ActiveMode = Modes().FirstOrDefault(m => m.Values.All(kv => state.Values.TryGetValue(kv.Key, out var v) && Math.Abs(v - kv.Value) <= 0.0005f))?.Name;
        if (answered == 0)
        {
            state.Error = "no answer";
        }
        return state;
    }

    // The eight recording values from <paramref name="first"/>, or null when one is missing.
    private static float[]? Block(SoundBlasterStateDto state, byte first)
    {
        var values = new float[SoundBlasterPresets.Count];
        for (var i = 0; i < values.Length; i++)
        {
            if (!state.Values.TryGetValue(Key(Recording, (byte)(first + i)), out values[i]))
            {
                return null;
            }
        }
        return values;
    }

    // What does not change while the device stays connected: read once per connection.
    private void ReadIdentity(SoundBlasterModel model)
    {
        if (_identityOf == model.Name && _identity.Firmware is not null)
        {
            return;
        }
        var firmware = Ask(ReadFirmware(), r => CommandOf(r) == FirmwareString) is { } f ? ParseText(f, 0) : null;
        var serial = Ask(ReadSerial(), r => CommandOf(r) == DeviceInformation && r.Length > 4 && r[4] == 0x03) is { } s ? ParseText(s, 1) : null;
        var outputs = Ask(ListOutputs(), r => CommandOf(r) == OutputSelect && r.Length > 4 && r[4] == 0x02) is { } o
            ? ParseOutputList(o).Select(b => OutputName(b)).OfType<string>().ToList()
            : new List<string>();
        var filters = Ask(ListFilters(), r => CommandOf(r) == DacFilter && r.Length > 4 && r[4] == 0x02) is { } l
            ? ParseFilterList(l).Select(b => (int)b).ToList()
            : new List<int>();
        var supported = Ask(ListFeatures(), r => ParseFeatureMask(r, supported: true) is not null) is { } m
            ? ParseFeatureMask(m, supported: true) ?? 0
            : 0;
        _identity = (firmware, serial, outputs, filters, supported);
        _identityOf = model.Name;
    }

    // The wanted reply, or null; a refusal (an acknowledgement with an error status) ends the wait early.
    private byte[]? Ask(byte[] request, Func<byte[], bool> accept)
    {
        var command = request[2];
        var reply = _channel.Exchange(request, r => accept(r) || (TryParseAck(r, out var c, out var status) && c == command && status != 0));
        return reply is not null && accept(reply) ? reply : null;
    }

    // A value the device pushed (after a change from here, Creative App or its buttons) goes into the copy.
    private void Observe(byte[] report)
    {
        if (_state is null)
        {
            return;
        }
        if (TryParseParameter(report, out var family, out var id, out var value))
        {
            _state.Values[Key(family, id)] = value;
        }
        else if (ParseProfile(report) is { } bits)
        {
            _state.Sbx = (bits & ProfileSbx) != 0;
            _state.Scout = (bits & ProfileScout) != 0;
            _state.VoiceClarity = (bits & ProfileVoiceClarity) != 0;
        }
    }

    private static string? OutputName(byte? code) => code switch
    {
        OutputSpeakers => "speakers",
        OutputHeadphones => "headphones",
        _ => null,
    };

    private static SoundBlasterStateDto Copy(SoundBlasterStateDto s) => new()
    {
        Present = s.Present,
        Name = s.Name,
        Model = s.Model,
        Firmware = s.Firmware,
        Serial = s.Serial,
        Error = s.Error,
        Sbx = s.Sbx,
        Scout = s.Scout,
        VoiceClarity = s.VoiceClarity,
        Values = new Dictionary<string, float>(s.Values),
        Output = s.Output,
        Outputs = s.Outputs.ToList(),
        DacFilter = s.DacFilter,
        DacFilters = s.DacFilters.ToList(),
        MicBoost = s.MicBoost,
        Features = new Dictionary<string, bool>(s.Features),
        VoiceMorph = s.VoiceMorph,
        MicEq = s.MicEq,
        VoicePresets = s.VoicePresets.ToList(),
        MicEqPresets = s.MicEqPresets.ToList(),
        MicEqBandHz = s.MicEqBandHz.ToList(),
        Mixer = s.Mixer.Select(c => new SoundBlasterMixerChannelDto { Id = c.Id, Name = c.Name, Direction = c.Direction, Volume = c.Volume, Muted = c.Muted }).ToList(),
        Modes = s.Modes.ToList(),
        ActiveMode = s.ActiveMode,
    };
}
