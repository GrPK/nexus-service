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
