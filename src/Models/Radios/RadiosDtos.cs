using System.Text.Json.Serialization;

namespace Nexus.Service.Models.Radios;

public static class RadioKinds
{
    public const string Wifi = "wifi";
    public const string Bluetooth = "bluetooth";
    public const string Airplane = "airplane";

    public static bool IsKnown(string? kind) => kind is Wifi or Bluetooth or Airplane;
}

public sealed class RadioToggle
{
    public bool On { get; set; }
}

/// <summary>A null member means that radio or control is absent or
/// unsupported on this machine. The wire carries the explicit null, unlike
/// the context-wide omit-nulls default.</summary>
public sealed class RadiosState
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public RadioToggle? Wifi { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public RadioToggle? Bluetooth { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public RadioToggle? Airplane { get; set; }
}

public sealed class SetRadioBody
{
    public string Kind { get; set; } = "";
    public bool On { get; set; }
}
