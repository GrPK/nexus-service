using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.LogitechHidpp;

/// <summary>
/// Device pictures from the user's own G HUB installation (%ProgramData%\LGHUB\depots), so a device
/// page can show the same picture G HUB shows. Nothing is shipped with Nexus: when G HUB is not
/// installed, or keeps a picture encrypted, there is no picture and the page draws the device itself.
/// Only files a depot's manifest names as a device image are served, and only plain WebP or PNG.
/// </summary>
public static class GHubDeviceImages
{
    private const string Tag = "ghub-images";
    private static readonly ConcurrentDictionary<string, (byte[] Bytes, string ContentType)?> Cache = new();

    /// <summary>Root of G HUB's downloaded device data.</summary>
    public static string DepotsRoot { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LGHUB", "depots");

    /// <summary>
    /// The picture G HUB uses for <paramref name="modelId"/> (resource <paramref name="resourceKey"/>, e.g.
    /// "device_image"), from the depot whose metadata.json contains <paramref name="metadataMarker"/> when one
    /// is given (layout variants share a model id). Null when there is none.
    /// </summary>
    public static (byte[] Bytes, string ContentType)? Get(string modelId, string resourceKey, string? metadataMarker = null) =>
        Cache.GetOrAdd($"{modelId}|{resourceKey}|{metadataMarker}", _ => Find(modelId, resourceKey, metadataMarker));

    private static (byte[] Bytes, string ContentType)? Find(string modelId, string resourceKey, string? metadataMarker)
    {
        try
        {
            if (!Directory.Exists(DepotsRoot))
            {
                return null;
            }
            foreach (var manifestPath in Directory.EnumerateFiles(DepotsRoot, "manifest.json", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3 }))
            {
                var depot = Path.GetDirectoryName(manifestPath)!;
                using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
                if (!doc.RootElement.TryGetProperty("devices", out var devices) || devices.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }
                foreach (var device in devices.EnumerateArray())
                {
                    if (!device.TryGetProperty("modelId", out var id) || id.GetString() != modelId
                        || !device.TryGetProperty("resources", out var resources) || resources.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }
                    var src = resources.EnumerateArray()
                        .Where(r => r.TryGetProperty("key", out var k) && k.GetString() == resourceKey)
                        .Select(r => r.TryGetProperty("src", out var s) ? s.GetString() : null)
                        .FirstOrDefault(s => !string.IsNullOrEmpty(s));
                    if (src is null || (metadataMarker is not null && !MetadataContains(depot, metadataMarker)))
                    {
                        continue;
                    }
                    var path = Path.GetFullPath(Path.Combine(depot, src));
                    if (!path.StartsWith(Path.GetFullPath(depot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                    {
                        continue;
                    }
                    var bytes = File.ReadAllBytes(path);
                    if (ContentType(bytes) is { } type)
                    {
                        ServiceLog.Info($"[{Tag}] {modelId} {resourceKey}: {Path.GetFileName(depot)}\\{src}");
                        return (bytes, type);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            ServiceLog.Warn($"[{Tag}] {modelId}: {ex.Message}");
        }
        return null;
    }

    private static bool MetadataContains(string depot, string marker)
    {
        var path = Path.Combine(depot, "metadata.json");
        return File.Exists(path) && File.ReadAllText(path).Replace(" ", "").Contains(marker, StringComparison.Ordinal);
    }

    /// <summary>"image/webp" or "image/png" for a plain picture; null for anything else (G HUB encrypts some).</summary>
    internal static string? ContentType(ReadOnlySpan<byte> b)
    {
        if (b.Length > 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P')
        {
            return "image/webp";
        }
        if (b.Length > 8 && b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G')
        {
            return "image/png";
        }
        return null;
    }
}
