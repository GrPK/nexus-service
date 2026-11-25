using System.Collections.Generic;

namespace Nexus.Service.Models.Devices;

/// <summary>
/// A Logitech mouse's onboard memory (GET /devices/logitech-mouse): the profiles it uses with
/// no software running, as G HUB's onboard memory mode edits them.
/// </summary>
public sealed class LogitechMouseStateDto
{
    public bool Present { get; set; }
    public string? Name { get; set; }
    public string? Model { get; set; }
    /// <summary>"onboard" (the mouse runs its stored profile) or "host" (software drives it); null when unknown.</summary>
    public string? Mode { get; set; }
    /// <summary>Number (1-based, directory order) of the profile the mouse uses now.</summary>
    public int? ActiveProfile { get; set; }
    public int ButtonCount { get; set; }
    /// <summary>A label per physical button slot, from the model's factory layout.</summary>
    public List<string> ButtonLabels { get; set; } = new();
    /// <summary>Built-in functions a button can run, by firmware code.</summary>
    public List<LogitechMouseSpecialDto> Specials { get; set; } = new();
    /// <summary>Report rates the mouse supports, in Hz.</summary>
    public List<int> ReportRatesHz { get; set; } = new();
    public List<LogitechMouseProfileDto> Profiles { get; set; } = new();
    /// <summary>Set when the memory could not be read.</summary>
    public string? Error { get; set; }
}

public sealed class LogitechMouseSpecialDto
{
    public int Code { get; set; }
    public string Name { get; set; } = "";
}

public sealed class LogitechMouseProfileDto
{
    public int Number { get; set; }
    public int Sector { get; set; }
    public bool Enabled { get; set; }
    public string Name { get; set; } = "";
    public int ReportRateHz { get; set; }
    public List<int> Dpi { get; set; } = new();
    public int DefaultDpiIndex { get; set; }
    public int ShiftDpiIndex { get; set; }
    public List<LogitechMouseButtonDto> Buttons { get; set; } = new();
    public List<LogitechMouseButtonDto> GShiftButtons { get; set; } = new();
    public bool CrcValid { get; set; }
    /// <summary>True when a backup of the profile as first read exists, so it can be restored.</summary>
    public bool CanRestore { get; set; }
}

/// <summary>One button binding: kind "mouse" (code = button mask), "key" (code = HID usage, modifiers), "consumer" (code = usage), "special" (code = function), "raw" (kept as is).</summary>
public sealed class LogitechMouseButtonDto
{
    public int Slot { get; set; }
    public string Kind { get; set; } = "";
    public int Code { get; set; }
    public int Modifiers { get; set; }
    /// <summary>Raw binding bytes in hex, for "raw" (macros and other encodings).</summary>
    public string? Raw { get; set; }
}

/// <summary>PUT /devices/logitech-mouse/profiles/{number}: any subset; omitted fields keep their value.</summary>
public sealed class LogitechMouseProfileRequest
{
    public string? Name { get; set; }
    public int? ReportRateHz { get; set; }
    public List<int>? Dpi { get; set; }
    public int? DefaultDpiIndex { get; set; }
    public int? ShiftDpiIndex { get; set; }
    public List<LogitechMouseButtonDto>? Buttons { get; set; }
    public List<LogitechMouseButtonDto>? GShiftButtons { get; set; }
}

/// <summary>POST /devices/logitech-mouse/active: the profile number to switch to.</summary>
public sealed class LogitechMouseActiveRequest
{
    public int? Profile { get; set; }
}

/// <summary>POST /devices/logitech-mouse/mode: "onboard" or "host".</summary>
public sealed class LogitechMouseModeRequest
{
    public string? Mode { get; set; }
}
