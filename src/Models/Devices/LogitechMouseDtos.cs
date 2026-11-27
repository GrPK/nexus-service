using System.Collections.Generic;

namespace Nexus.Service.Models.Devices;

/// <summary>
/// A Logitech mouse's onboard memory (GET /devices/logitech-mouse): the profiles it uses with
/// no software running, as G HUB's onboard memory mode edits them.
/// </summary>
/// <summary>What the mouse uses right now: its profile and DPI level (see LogitechMouseService.GetLive).</summary>
public sealed class LogitechMouseLiveDto
{
    public bool Present { get; set; }
    /// <summary>Number of the profile the mouse uses (1-based, as in the state); null when unknown.</summary>
    public int? ActiveProfile { get; set; }
    /// <summary>Index of the DPI level it uses in that profile's list (0-based); null when unknown.</summary>
    public int? DpiIndex { get; set; }
}

public sealed class LogitechMouseStateDto
{
    public bool Present { get; set; }
    public string? Name { get; set; }
    public string? Model { get; set; }
    /// <summary>"onboard" (the mouse runs its stored profile) or "host" (software drives it); null when unknown.</summary>
    public string? Mode { get; set; }
    /// <summary>Whether Nexus puts the mouse back on its onboard memory when another app switches it to host mode.</summary>
    public bool KeepOnboard { get; set; }
    /// <summary>A picture of the mouse the user put in its Nexus folder, with where each button is on it; null when there is none.</summary>
    public LogitechMousePictureDto? Picture { get; set; }
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
    /// <summary>Onboard memory sectors macros can use, and how many of them hold no macro in use.</summary>
    public int MacroSectors { get; set; }
    public int MacroSectorsFree { get; set; }
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

/// <summary>
/// One button binding: kind "mouse" (code = button mask), "key" (code = HID usage, modifiers),
/// "consumer" (code = usage), "special" (code = function), "macro" (code = sector, modifiers =
/// address; steps in <see cref="Macro"/>), "raw" (kept as is).
/// </summary>
public sealed class LogitechMouseButtonDto
{
    public int Slot { get; set; }
    public string Kind { get; set; } = "";
    public int Code { get; set; }
    public int Modifiers { get; set; }
    /// <summary>Raw binding bytes in hex, for "raw" (other encodings).</summary>
    public string? Raw { get; set; }
    /// <summary>
    /// A macro's steps. Read: the steps the mouse holds (null when it uses commands Nexus does not
    /// edit). Write: kind "macro" with steps stores them in the onboard memory; without steps the
    /// binding keeps pointing at the macro at code/modifiers.
    /// </summary>
    public List<LogitechMouseMacroStepDto>? Macro { get; set; }
    /// <summary>For "macro": false when the macro uses commands Nexus keeps but does not edit.</summary>
    public bool? MacroEditable { get; set; }
}

/// <summary>
/// One macro step: type "keyDown"/"keyUp" (code = HID usage, 0xE0-0xE7 for modifier keys;
/// modifiers = extra mask), "delay" (code = ms), "mouseDown"/"mouseUp" (code = button mask),
/// "consumerDown"/"consumerUp" (code = consumer usage), "wheel"/"hwheel" (code = signed clicks).
/// </summary>
public sealed class LogitechMouseMacroStepDto
{
    public string Type { get; set; } = "";
    public int Code { get; set; }
    public int Modifiers { get; set; }
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

/// <summary>POST /devices/logitech-mouse/profiles/{number}/cycle: whether the profile button steps through this profile.</summary>
public sealed class LogitechMouseCycleRequest
{
    public bool? Enabled { get; set; }
}

/// <summary>
/// A picture of the mouse (devices/logitech-mouse/&lt;model&gt;/picture.png|webp|jpg, served at
/// GET /devices/logitech-mouse/picture) and picture.json beside it: its size and each button's spot.
/// </summary>
public sealed class LogitechMousePictureDto
{
    public int Width { get; set; }
    public int Height { get; set; }
    public List<LogitechMousePictureSpotDto> Spots { get; set; } = new();
}

/// <summary>Where a button (by profile slot) is on the picture, in its pixels.</summary>
public sealed class LogitechMousePictureSpotDto
{
    public int Slot { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    /// <summary>"left" or "right": which side its callout goes; null to decide from where the button is.</summary>
    public string? Side { get; set; }
}

/// <summary>POST /devices/logitech-mouse/keep-onboard.</summary>
public sealed class LogitechMouseKeepOnboardRequest
{
    public bool? Enabled { get; set; }
}

/// <summary>What Nexus keeps for the mouse besides its memory (devices/logitech-mouse/settings.json).</summary>
public sealed class LogitechMouseSettings
{
    public bool KeepOnboard { get; set; } = true;
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
