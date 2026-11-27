using System;
using System.Collections.Generic;

namespace Nexus.Service.Models.Devices;

/// <summary>
/// A Logitech keyboard's key assignments (GET /devices/logitech-keyboard), the three layers G HUB
/// calls BASE, FN and G-SHIFT. The keyboard applies them itself, so they work with no software
/// running. The keyboard cannot report its tables, so the layers are the ones Nexus keeps.
/// </summary>
public sealed class LogitechKeyboardStateDto
{
    public bool Present { get; set; }
    public string? Name { get; set; }
    public string? Model { get; set; }
    /// <summary>Set when the keyboard could not be read or written.</summary>
    public string? Error { get; set; }
    /// <summary>The keyboard's layout code (feature 0x4540).</summary>
    public int? LayoutCode { get; set; }
    /// <summary>"ansi" or "iso" (shape of Enter, the extra key beside it): the user's choice, else the one known for <see cref="LayoutCode"/>, else null.</summary>
    public string? Layout { get; set; }
    /// <summary>The user's choice ("ansi" / "iso"), or null to follow the keyboard.</summary>
    public string? LayoutChoice { get; set; }
    /// <summary>The layout known for <see cref="LayoutCode"/>, or null when unknown.</summary>
    public string? LayoutDetected { get; set; }
    /// <summary>Whether G HUB's picture of this keyboard (for <see cref="Layout"/>) is on this PC (GET /devices/logitech-keyboard/image?layout=).</summary>
    public bool Photo { get; set; }
    /// <summary>Logitech control id of each key number (the key's position in this list); 0 for an empty slot.</summary>
    public List<int> Controls { get; set; } = new();
    /// <summary>Whether the keyboard applies the assignments now (G HUB's "custom assignments", Fn+F1).</summary>
    public bool? Enabled { get; set; }
    /// <summary>Whether Nexus has written its layers to this keyboard; until then it keeps whatever was written before.</summary>
    public bool Written { get; set; }
    public LogitechKeyboardLayersDto Layers { get; set; } = new();
    /// <summary>When the user last saved a copy of the assignments to restore later (UTC); null when there is none.</summary>
    public DateTime? SavedAt { get; set; }
}

/// <summary>Assignments per layer. A key that is not listed does what it does by default.</summary>
public sealed class LogitechKeyboardLayersDto
{
    public List<LogitechKeyboardRowDto> Base { get; set; } = new();
    /// <summary>While Fn is held.</summary>
    public List<LogitechKeyboardRowDto> Fn { get; set; } = new();
    /// <summary>While the G-shift key is held. Keys not listed keep their BASE assignment.</summary>
    public List<LogitechKeyboardRowDto> GShift { get; set; } = new();
}

public sealed class LogitechKeyboardRowDto
{
    /// <summary>Key number: the key's position in <see cref="LogitechKeyboardStateDto.Controls"/>.</summary>
    public int Key { get; set; }
    /// <summary>"keyboard", "mouse", "consumer", "gshift" or "disabled".</summary>
    public string Kind { get; set; } = "";
    /// <summary>HID usage (keyboard, consumer) or button mask (mouse).</summary>
    public int Code { get; set; }
    /// <summary>Modifier bits for a keyboard key: 1 Ctrl, 2 Shift, 4 Alt, 8 Win; the same bits shifted by 4 for the right-hand ones.</summary>
    public int Modifiers { get; set; }
}

/// <summary>POST /devices/logitech-keyboard/enabled.</summary>
public sealed class LogitechKeyboardEnabledRequest
{
    public bool? Enabled { get; set; }
}

/// <summary>POST /devices/logitech-keyboard/layout: "ansi", "iso", or null to follow the keyboard.</summary>
public sealed class LogitechKeyboardLayoutRequest
{
    public string? Layout { get; set; }
}

/// <summary>What Nexus keeps for a keyboard (devices/logitech-keyboard/&lt;model&gt;.json).</summary>
public sealed class LogitechKeyboardConfig
{
    public LogitechKeyboardLayersDto Layers { get; set; } = new();
    public bool Enabled { get; set; } = true;
    public string? Layout { get; set; }
    /// <summary>False until the layers have been written to the keyboard; a later connection retries.</summary>
    public bool Written { get; set; }
    /// <summary>A copy of the layers the user saved, to restore later; null until the first save.</summary>
    public LogitechKeyboardLayersDto? Saved { get; set; }
    /// <summary>When that copy was saved (UTC).</summary>
    public DateTime? SavedAt { get; set; }
}
