using System;
using System.Collections.Generic;
using System.Linq;

namespace Nexus.Service.Peripherals.CreativeAudio;

/// <summary>A Voice Morph character: the eight voice FX parameters (recording 0x0B-0x12) Creative App writes for it.</summary>
public sealed record SoundBlasterVoicePreset(string Id, string Name, float[] Values);

/// <summary>A microphone EQ preset: the eight band gains in dB (recording 0x14-0x1B).</summary>
public sealed record SoundBlasterMicEqPreset(string Id, string Name, float[] Bands);

/// <summary>
/// The Voice Morph characters and microphone EQ presets Creative App offers for the G8 / G6, as in its
/// PresetConfig.json (VoiceFx and VoiceEq) with the names from its string resources. The device keeps only
/// the parameter values; which preset is in use is found by comparing them.
/// </summary>
public static class SoundBlasterPresets
{
    /// <summary>Voice FX: enable, then formant 1-3 (Hz), pitch modulation, envelope modulation, pitch variability, quiver depth, contour depth.</summary>
    public const byte VoiceFxEnable = 0x0A;
    public const byte VoiceFxFirst = 0x0B;
    public const byte MicEqEnable = 0x13;
    public const byte MicEqFirst = 0x14;
    public const int Count = 8;

    /// <summary>The microphone EQ band centres (Hz).</summary>
    public static readonly int[] MicEqBandHz = { 31, 62, 125, 250, 500, 1000, 2000, 4000 };

    public static readonly SoundBlasterVoicePreset[] Voices =
    {
        new("Neutral", "Neutral", new[] { 400f, 1400f, 2000f, 1f, 1f, 1f, 0f, 0f }),
        new("Male", "Male", new[] { 400f, 1400f, 2000f, 0.6f, 1.05f, 1f, 0f, 0f }),
        new("Female", "Female", new[] { 400f, 1400f, 2220f, 2.37f, 0.92f, 1f, 0f, 0f }),
        new("Kid", "Kid", new[] { 400f, 1400f, 2000f, 3f, 0.66f, 1f, 0f, 0f }),
        new("Elderly", "Elderly", new[] { 713f, 1500f, 1800f, 1.4f, 1.45f, 1f, 0.1818f, 0f }),
        new("DeepVoice", "Deep voice", new[] { 339.5443f, 1362.496f, 2046f, 0.4291186f, 1.201318f, 1f, 0f, 0f }),
        new("FromUpNorth", "From up north", new[] { 400f, 1400f, 2000f, 1f, 1f, -0.2066f, 0f, 0.264461f }),
        new("Unstable", "Unstable", new[] { 400f, 1400f, 2000f, 2.37f, 0.92f, 2.5f, 0f, 0f }),
        new("Emo", "Emo", new[] { 400f, 1400f, 2000f, 1f, 1f, 0.2f, 0f, 0f }),
        new("Elf", "Elf", new[] { 398f, 1395f, 2451f, 1.11f, 0.96f, 1f, 0f, 0f }),
        new("Dwarf", "Dwarf", new[] { 294f, 1527f, 2055f, 0.77f, 0.97f, 1f, 0f, 0f }),
        new("Demon", "Demon", new[] { 453f, 1544f, 2600f, 0.69f, 2f, 1f, 0f, 0f }),
        new("Infiltrator", "Infiltrator", new[] { 200f, 1370f, 2000f, 1.24f, 1.06f, 1f, 0.13f, 0f }),
        new("Brute", "Brute", new[] { 383f, 1428f, 2550f, 0.65f, 1.31138f, 1f, 0.03f, 0f }),
        new("Orc", "Orc", new[] { 468f, 1321f, 2200f, 0.66f, 1.5f, 1f, 0f, 0f }),
        new("Marine", "Marine", new[] { 388f, 1155f, 1848f, 0.8f, 1.08f, 0.54f, 0f, 0f }),
        new("Chipmunk", "Chipmunk", new[] { 400f, 1400f, 2000f, 1f, 0.601627f, 1f, 0f, 0f }),
        new("Robot", "Robot", new[] { 400f, 1400f, 2000f, 1.394f, 1f, -0.00824f, 0f, 0f }),
    };

    public static readonly SoundBlasterMicEqPreset[] MicEq =
    {
        new("Preset1", "Preset 1: less bass and harshness, more clarity", new[] { -3f, -4f, 0f, 2f, 3f, -3f, 4f, 5f }),
        new("Preset2", "Preset 2: less bass, more voice and clarity", new[] { -3f, -4f, 0f, 2f, 4f, -2f, 2f, 4f }),
        new("Preset3", "Preset 3: less harshness, more voice", new[] { -2f, -3f, 3f, 4f, 4f, -4f, 3f, 2f }),
        new("Preset4", "Preset 4: less bass, more voice", new[] { -3f, -5f, 0f, 4f, 4f, -3f, 0f, 0f }),
        new("Preset5", "Preset 5: more voice, less harshness", new[] { -2f, -3f, 2f, 4f, 4f, 0f, -3f, 2f }),
        new("Preset6", "Preset 6: less bass, more clarity", new[] { -5f, -4f, -2f, 0f, 3f, 4f, 6f, 7f }),
        new("Preset7", "Preset 7: less voice, more bass and clarity", new[] { 0f, 3f, -2f, -4f, -4f, -2f, 5f, 7f }),
        new("Preset8", "Preset 8: less harshness, more voice and clarity", new[] { 0f, 0f, 2f, 2f, 3f, -4f, 2f, 4f }),
        new("Preset9", "Preset 9: less harshness, more clarity", new[] { 0f, 0f, 2f, 2f, -2f, 0f, -4f, 4f }),
        new("Preset10", "Preset 10: more clarity", new[] { 0f, 2f, -2f, 0f, 3f, 5f, 6f, 5f }),
        new("PresetDM1", "Preset 11: more voice and clarity", new[] { 0f, 8f, 0f, 12f, 12f, 4f, 8f, 10f }),
    };

    /// <summary>The voice whose parameters match <paramref name="values"/> (within rounding), or null.</summary>
    public static string? MatchVoice(IReadOnlyList<float> values) =>
        Voices.FirstOrDefault(v => Same(v.Values, values))?.Id;

    /// <summary>The mic EQ preset whose bands match <paramref name="bands"/>, or null.</summary>
    public static string? MatchMicEq(IReadOnlyList<float> bands) =>
        MicEq.FirstOrDefault(p => Same(p.Bands, bands))?.Id;

    private static bool Same(IReadOnlyList<float> a, IReadOnlyList<float> b) =>
        a.Count == b.Count && a.Zip(b).All(p => Math.Abs(p.First - p.Second) <= Math.Max(0.01f, Math.Abs(p.First) * 0.001f));
}
