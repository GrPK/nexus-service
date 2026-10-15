namespace Nexus.Service.Tests.Lighting.CustomEffects;

internal static class CustomFrags
{
    /// <summary>A fresh id per call: ShaderLibrary's custom registry is process-wide, so tests never share one.</summary>
    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    public static string Frag(float gainDefault = 0.5f, bool audio = false, string? extra = null) =>
        "#version 330 core\n"
        + "uniform vec2 u_resolution;\n"
        + "uniform float u_time;\n"
        + "uniform float u_speed; // hint_range(0.0, 4.0, 0.01) = 1.0\n"
        + $"uniform float u_b1_gain; // hint_range(0.0, 2.0, 0.01) = {gainDefault.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n"
        + (audio ? "uniform float u_audioLevel;\n" : "")
        + (extra ?? "")
        + "out vec4 fragColor;\n"
        + "void main() { fragColor = vec4(vec3(u_b1_gain), 1.0); }\n";
}
