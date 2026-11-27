using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Models.Devices;
using Nexus.Service.Peripherals.Hid;
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
/// reads the SBX effects, EQ, Scout Mode, mic processing, output and DAC filter, and changes them.
/// The settings live in the device, so nothing is kept here but a short-lived copy of the last read.
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
    private static readonly byte[] RecordingIds = { 0x00, 0x04, 0x05, 0x2C, 0x2D };
    private static readonly byte[] DecoderIds = { 0x02 };

    private readonly ISoundBlasterChannel _channel;
    private readonly object _lock = new();
    private SoundBlasterStateDto? _state;
    private DateTime _readAt;
    private string? _identityOf;
    private (string? Firmware, string? Serial, List<string> Outputs, List<int> Filters) _identity = (null, null, new(), new());

    public SoundBlasterService(IHidEnumerator hid)
        : this(new SoundBlasterChannel(hid))
    {
    }

    internal SoundBlasterService(ISoundBlasterChannel channel)
    {
        _channel = channel;
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

    public SoundBlasterResult SetSbx(bool on) => Write(WriteProfile(0x01, on), state => state.Sbx = on);

    public SoundBlasterResult SetScout(bool on) => Write(WriteProfile(0x02, on), state =>
    {
        state.Scout = on;
        if (on)
        {
            state.Sbx = false;
        }
    });

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

    internal static string Key(byte family, byte id) => $"{family:x2}:{id:x2}";

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
                var reply = _channel.Exchange(ReadParameter(family, id),
                    r => (TryParseParameter(r, out var f, out var i, out _) && f == family && i == id)
                        || (TryParseAck(r, out var c, out _) && c == GetParameter));
                if (reply is not null && TryParseParameter(reply, out _, out _, out var value))
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
        if (answered == 0)
        {
            state.Error = "no answer";
        }
        return state;
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
        _identity = (firmware, serial, outputs, filters);
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
    };
}
