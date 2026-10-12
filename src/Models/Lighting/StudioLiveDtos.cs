using System.Collections.Generic;

namespace Nexus.Service.Models.Lighting;

/// <summary>
/// POST body for /lighting/studio-live. The look fields mean what they do on the animate
/// start body: Speed 50 is 1x forward, negative runs in reverse, 0 freezes.
/// </summary>
public sealed class StudioLiveBody
{
    public string? Frag { get; set; }
    public int Speed { get; set; } = 50;
    public float Intensity { get; set; } = 1f;
    public float Hue { get; set; }
    public float Colorize { get; set; }
    public float Saturation { get; set; } = 1f;
    public float Contrast { get; set; } = 1f;
    public Dictionary<string, float>? Params { get; set; }
}

/// <summary>Answer to POST /lighting/studio-live (Error and Msg come from the envelope).</summary>
public sealed class StudioLiveResponse : ApiResponse
{
    /// <summary>True while the session runs.</summary>
    public bool Active { get; set; }

    /// <summary>Milliseconds the lease lasts after this call; re-POST before it runs out.</summary>
    public int LeaseMs { get; set; }

    /// <summary>Devices driven although their saved settings have them LEDs-off or not controlled.</summary>
    public int DevicesForced { get; set; }

    /// <summary>Device ids the session cannot drive because their controller is detached from OpenRGB or held off.</summary>
    public List<string> Skipped { get; set; } = new();
}
