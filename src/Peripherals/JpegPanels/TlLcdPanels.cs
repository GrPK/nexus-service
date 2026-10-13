using System;
using System.Collections.Generic;
using System.Linq;

namespace Nexus.Service.Peripherals.JpegPanels;

/// <summary>One attached TL LCD screen. <see cref="Key"/> is the serial its panel record persists under.</summary>
public sealed record TlLcdScreen(string Key, string DisplayName, string Path, JpegPanelHub Hub, TlLcdHandshake Handshake);

/// <summary>The attached TL LCD screens, one <see cref="JpegPanelHub"/> each. Written by the connection worker only.</summary>
public sealed class TlLcdPanels
{
    /// <summary>The screens belong to the Uni Fan TL hub's device and follow its Nexus Control switch.</summary>
    public const string GateHandlerId = "lianli-tl";

    private readonly object _lock = new();
    private readonly Dictionary<string, TlLcdScreen> _byPath = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<TlLcdScreen> Attached
    {
        get { lock (_lock) { return _byPath.Values.Where(s => s.Hub.IsConnected).ToArray(); } }
    }

    public bool AnyAttached
    {
        get { lock (_lock) { return _byPath.Values.Any(s => s.Hub.IsConnected); } }
    }

    public TlLcdScreen? ByKey(string key)
    {
        lock (_lock)
        {
            return _byPath.Values.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.Ordinal));
        }
    }

    internal bool Contains(string path)
    {
        lock (_lock) { return _byPath.ContainsKey(path); }
    }

    internal IReadOnlyList<TlLcdScreen> All
    {
        get { lock (_lock) { return _byPath.Values.ToArray(); } }
    }

    internal void Add(TlLcdScreen screen)
    {
        lock (_lock) { _byPath[screen.Path] = screen; }
    }

    internal void Remove(string path)
    {
        lock (_lock) { _byPath.Remove(path); }
    }

    /// <summary>
    /// Screen key for its settings and panel record: L-Connect's serial, else the screen's place,
    /// else its HID path; a key another attached screen holds falls through, so keys stay unique.
    /// </summary>
    internal string KeyFor(TlLcdIdentity identity, string path)
    {
        var candidates = new List<string>(3);
        if (identity.Serial is { } serial)
        {
            candidates.Add(serial);
        }
        if (identity.Port >= 0 && identity.Index >= 0)
        {
            candidates.Add($"p{identity.Port}i{identity.Index}");
        }
        lock (_lock)
        {
            foreach (var key in candidates)
            {
                if (!_byPath.Values.Any(s => s.Key == key))
                {
                    return key;
                }
            }
        }
        return PathHash(path);
    }

    /// <summary>"#port.fan", one-based like the TL fan hub's channels; empty when the screen did not say.</summary>
    public static string LabelFor(TlLcdIdentity identity) =>
        identity.Port >= 0 && identity.Index >= 0 ? $"#{identity.Port + 1}.{identity.Index + 1}" : "";

    /// <summary>The model name plus <see cref="LabelFor"/>; no words, since the record name shows in every locale.</summary>
    internal static string DisplayNameFor(TlLcdIdentity identity) =>
        identity.Port >= 0 && identity.Index >= 0
            ? $"{JpegPanelModel.TlLcd.Name} {LabelFor(identity)}"
            : JpegPanelModel.TlLcd.Name;

    /// <summary>FNV-1a over the lowercased path; stable across restarts on Windows only, where the path follows the USB port.</summary>
    private static string PathHash(string path)
    {
        uint hash = 2166136261;
        foreach (char c in path.ToLowerInvariant())
        {
            hash = (hash ^ c) * 16777619;
        }
        return hash.ToString("x8");
    }
}
