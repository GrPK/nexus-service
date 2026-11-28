using System;
using System.Collections.Generic;

namespace Nexus.Service.Models.Devices;

/// <summary>
/// A Creative Sound Blaster (G8, G6) as Creative App controls it: the SBX acoustic engine and the
/// equalizer, Scout Mode, the microphone's voice processing and boost, the output and the DAC filter.
/// </summary>
public sealed class SoundBlasterStateDto
{
    public bool Present { get; set; }
    /// <summary>"Creative Sound Blaster G8".</summary>
    public string? Name { get; set; }
    public string? Model { get; set; }
    public string? Firmware { get; set; }
    public string? Serial { get; set; }
    /// <summary>Set when the device answered nothing (e.g. it is busy or was unplugged mid-read).</summary>
    public string? Error { get; set; }
    /// <summary>Whether the SBX processing (any playback effect or the EQ) is on.</summary>
    public bool Sbx { get; set; }
    /// <summary>Scout Mode (it turns SBX off while on).</summary>
    public bool Scout { get; set; }
    /// <summary>Whether the recording processing (Voice Clarity) is on.</summary>
    public bool VoiceClarity { get; set; }
    /// <summary>
    /// DSP parameters by "family:id" in hex ("96:07" = Crystalizer on/off, "95:04" = noise reduction):
    /// toggles 0 or 1, levels 0..1, EQ in dB, crossover in Hz. Only the ones the device answered.
    /// </summary>
    public Dictionary<string, float> Values { get; set; } = new();
    /// <summary>"speakers" or "headphones"; null when unknown.</summary>
    public string? Output { get; set; }
    public List<string> Outputs { get; set; } = new();
    /// <summary>The DAC filter (Creative's numbering: 1 fast roll-off minimum phase ... 5 slow roll-off linear phase).</summary>
    public int? DacFilter { get; set; }
    public List<int> DacFilters { get; set; } = new();
    /// <summary>Microphone boost in dB (0, 10, 20 or 30).</summary>
    public int? MicBoost { get; set; }
    /// <summary>
    /// Device features the device has, on or off: "directMode", "spdifDirect", "speakerVirtualization",
    /// "logoLed", "energySaving", "volumeSync".
    /// </summary>
    public Dictionary<string, bool> Features { get; set; } = new();
    /// <summary>The Voice Morph character the voice FX parameters match; null when none does.</summary>
    public string? VoiceMorph { get; set; }
    /// <summary>The microphone EQ preset the band gains match; null when none does.</summary>
    public string? MicEq { get; set; }
    public List<SoundBlasterPresetDto> VoicePresets { get; set; } = new();
    public List<SoundBlasterPresetDto> MicEqPresets { get; set; } = new();
    public List<int> MicEqBandHz { get; set; } = new();
    /// <summary>Windows' volume and mute of the card's own endpoints (playback first).</summary>
    public List<SoundBlasterMixerChannelDto> Mixer { get; set; } = new();
    /// <summary>The modes saved in Nexus, by name.</summary>
    public List<string> Modes { get; set; } = new();
    /// <summary>The saved mode whose values all match the device now; null when none does.</summary>
    public string? ActiveMode { get; set; }
}

public sealed class SoundBlasterPresetDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>For a mic EQ preset, its band gains in dB.</summary>
    public List<float>? Values { get; set; }
}

public sealed class SoundBlasterMixerChannelDto
{
    /// <summary>The Windows endpoint id.</summary>
    public string Id { get; set; } = "";
    /// <summary>"Speakers", "Microphone", "Line", "SPDIF Interface", "Digital Audio Interface".</summary>
    public string Name { get; set; } = "";
    /// <summary>"playback" or "recording".</summary>
    public string Direction { get; set; } = "playback";
    /// <summary>0 to 1.</summary>
    public double Volume { get; set; }
    public bool Muted { get; set; }
}

/// <summary>A mode saved in Nexus: the playback values (family 0x96) and the decoder's (0x97) by "family:id".</summary>
public sealed class SoundBlasterMode
{
    public string Name { get; set; } = "";
    public Dictionary<string, float> Values { get; set; } = new();
    public DateTime SavedAt { get; set; }
}

public sealed class SoundBlasterModesFile
{
    public List<SoundBlasterMode> Modes { get; set; } = new();
}

public sealed class SoundBlasterFeatureRequest
{
    public string? Name { get; set; }
    public bool? Enabled { get; set; }
}

public sealed class SoundBlasterPresetRequest
{
    public string? Preset { get; set; }
}

public sealed class SoundBlasterMixerRequest
{
    public string? Id { get; set; }
    public double? Volume { get; set; }
    public bool? Muted { get; set; }
}

public sealed class SoundBlasterModeRequest
{
    public string? Name { get; set; }
    /// <summary>The new name, when renaming.</summary>
    public string? To { get; set; }
}

public sealed class SoundBlasterParameterRequest
{
    /// <summary>0x95 recording, 0x96 playback, 0x97 decoder.</summary>
    public int? Family { get; set; }
    public int? Id { get; set; }
    public float? Value { get; set; }
}

public sealed class SoundBlasterToggleRequest
{
    public bool? Enabled { get; set; }
}

public sealed class SoundBlasterOutputRequest
{
    /// <summary>"speakers" or "headphones".</summary>
    public string? Output { get; set; }
}

public sealed class SoundBlasterValueRequest
{
    public int? Value { get; set; }
}
