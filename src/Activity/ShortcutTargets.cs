using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.IO;
using System.Text;

namespace Nexus.Service.Activity;

/// <summary>Platform-neutral parsing for launch-app target ids: typed paths, URIs, internet shortcuts and known-folder prefixes.</summary>
internal static partial class ShortcutTargets
{
    /// <summary>Largest .url file read; real ones are well under a kilobyte.</summary>
    internal const int MaxInternetShortcutBytes = 65536;

    private const int GuidLength = 36;
    private const int GuidPrefixLength = GuidLength + 2;
    private const int GuidCloseBraceIndex = GuidLength + 1;
    private const int GuidSeparatorIndex = GuidPrefixLength;
    private const int GuidPrefixWithSeparatorLength = GuidPrefixLength + 1;

    private static readonly Dictionary<string, Func<string>> KnownFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["6D809377-6AF0-444B-8957-A3773F02200E"] = () => Environment.GetEnvironmentVariable("ProgramW6432") ?? "",
        ["7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E"] = () => Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? "",
        ["905E63B6-C1BF-494E-B29C-65B732D3D21A"] = () => Environment.GetEnvironmentVariable("ProgramFiles") ?? "",
        ["1AC14E77-02E7-4E5D-B744-2EB1AE5198B7"] = () => Environment.SystemDirectory,
        ["D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27"] = () => Environment.GetEnvironmentVariable("SystemRoot") is { Length: > 0 } root ? Path.Combine(root, "SysWOW64") : "",
        ["F38BF404-1D43-42F2-9305-67DE0B28FC23"] = () => Environment.GetEnvironmentVariable("SystemRoot") ?? "",
    };

    /// <summary>Trims whitespace and surrounding quotes (Explorer's "Copy as path"), expands %VARS%, and replaces a leading {known-folder-guid}\ with its real directory.</summary>
    internal static string Normalize(string id)
    {
        var s = (id ?? "").Trim().Trim('"').Trim();
        if (s.Length == 0)
        {
            return s;
        }
        s = Environment.ExpandEnvironmentVariables(s);
        if (HasKnownFolderPrefix(s) &&
            KnownFolders.TryGetValue(s.Substring(1, GuidLength), out var resolve) &&
            resolve() is { Length: > 0 } dir)
        {
            s = Path.Combine(dir, s.Substring(GuidPrefixWithSeparatorLength));
        }
        return s;
    }

    private static bool HasKnownFolderPrefix(string s) =>
        s.Length > GuidSeparatorIndex && s[0] == '{' && s[GuidCloseBraceIndex] == '}' &&
        (s[GuidSeparatorIndex] == '\\' || s[GuidSeparatorIndex] == '/');

    /// <summary>A fully-qualified path on a local drive ("C:\..."), excluding UNC and device paths.</summary>
    internal static bool IsLocalDrivePath(string path)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            return false;
        }
        var root = Path.GetPathRoot(path);
        return root is not null && root.Length >= 2 && char.IsLetter(root[0]) && root[1] == ':';
    }

    /// <summary>
    /// True when a launchApp id names a file or URI directly instead of a
    /// Get-StartApps entry. Listed Win32 ids carry a known-folder GUID prefix
    /// (and UWP ids are Package!App), so those stay false without needing the
    /// helper's list. A GUID-prefixed id with a ".." segment is not trusted.
    /// </summary>
    internal static bool IsDirectTarget(string? id)
    {
        var raw = (id ?? "").Trim().Trim('"').Trim();
        if (raw.Length == 0)
        {
            return false;
        }
        if (HasKnownFolderPrefix(raw) && raw.Split('\\', '/').AsSpan().IndexOf("..") < 0)
        {
            return false;
        }
        var s = Normalize(raw);
        return IsLaunchUri(s) || s.IndexOfAny(['\\', '/']) >= 0 || Path.IsPathFullyQualified(s);
    }

    /// <summary>Schemes the launch-app fallback may hand to the shell.</summary>
    internal static bool IsAllowedLaunchScheme(string uri)
    {
        var colon = uri.IndexOf(':');
        if (colon <= 0)
        {
            return false;
        }
        var scheme = uri.Substring(0, colon);
        return scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
            scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ||
            scheme.Equals("steam", StringComparison.OrdinalIgnoreCase);
    }

    // InvariantGlobalization pins CultureInfo's ANSI page to 1252, so ask the OS.
    [LibraryImport("kernel32.dll")]
    private static partial uint GetACP();

    /// <summary>Reads a .url file: UTF-8 when valid (or BOM-marked), else the system ANSI code page, else Latin-1.</summary>
    internal static string ReadInternetShortcutText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
        }
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            if (OperatingSystem.IsWindows())
            {
                return Encoding.GetEncoding((int)GetACP()).GetString(bytes);
            }
        }
        catch (Exception)
        {
        }
        return Encoding.Latin1.GetString(bytes);
    }

    /// <summary>An absolute URI with a real scheme (steam://, ms-settings:) rather than a drive-letter path. file: is excluded.</summary>
    internal static bool IsLaunchUri(string id)
    {
        if (id.Length < 3 || id.StartsWith("\\\\", StringComparison.Ordinal))
        {
            return false;
        }
        var colon = id.IndexOf(':');
        if (colon < 2)
        {
            return false;
        }
        for (var i = 0; i < colon; i++)
        {
            var c = id[i];
            if (!(char.IsAsciiLetterOrDigit(c) || c == '+' || c == '-' || c == '.'))
            {
                return false;
            }
        }
        return !id.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The Url, IconFile and IconIndex of an internet shortcut (.url, INI format). Missing keys come back null or 0.</summary>
    internal static (string? Url, string? IconFile, int IconIndex) ParseInternetShortcut(string content)
    {
        string? url = null;
        string? iconFile = null;
        var iconIndex = 0;
        var inSection = false;
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inSection = line.Equals("[InternetShortcut]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inSection)
            {
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }
            var key = line.Substring(0, eq).Trim();
            var value = line.Substring(eq + 1).Trim();
            if (key.Equals("URL", StringComparison.OrdinalIgnoreCase))
            {
                url = value;
            }
            else if (key.Equals("IconFile", StringComparison.OrdinalIgnoreCase))
            {
                iconFile = value.Length == 0 ? null : value;
            }
            else if (key.Equals("IconIndex", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var idx))
            {
                iconIndex = idx;
            }
        }
        return (url, iconFile, iconIndex);
    }
}
