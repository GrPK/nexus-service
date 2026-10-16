using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Nexus.Service.Platform;

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>The HMAC key for the GPU blob caches; <paramref name="Fresh"/> means it was just generated, so any cache written under an older key must be wiped.</summary>
internal readonly record struct BlobKey(byte[] Key, bool Fresh);

/// <summary>
/// Process-wide secret that authenticates cached blobs. On Windows it exists
/// only while running as LocalSystem and is stored DPAPI-protected in the
/// CurrentUser scope (which is SYSTEM's, so no other account can unprotect it):
/// a local user who plants or edits a cache file cannot produce a valid tag. A
/// missing or unreadable key file is replaced with a new key and the caches
/// are wiped. Off Windows the key is a plain file in the per-user data root.
/// </summary>
internal static class BlobCacheKey
{
    private const int KeyBytes = 32;
    private static readonly object Gate = new();
    private static BlobKey? _cached;
    private static bool _resolved;

    public static string CacheRoot() => Path.Combine(Nexus.Service.Persistence.NexusDataPaths.NexusRoot(), "cache");

    /// <summary>The key, or null when no disk cache may be used (a Windows run that is not the LocalSystem service, or any failure).</summary>
    public static BlobKey? Get()
    {
        lock (Gate)
        {
            if (!_resolved)
            {
                _resolved = true;
                _cached = Load(Path.Combine(CacheRoot(), "blob-cache.key"), OperatingSystem.IsWindows());
            }
            return _cached;
        }
    }

    /// <summary>Load or create the key at <paramref name="path"/>. <paramref name="dpapi"/> selects Windows protection.</summary>
    internal static BlobKey? Load(string path, bool dpapi)
    {
        try
        {
            if (dpapi && !(OperatingSystem.IsWindows() && WindowsDirectorySecurity.IsLocalSystem()))
            {
                return null;
            }
            if (File.Exists(path))
            {
                try
                {
                    var stored = File.ReadAllBytes(path);
                    var key = dpapi && OperatingSystem.IsWindows() ? Unprotect(stored) : stored;
                    if (key.Length == KeyBytes)
                    {
                        return new BlobKey(key, false);
                    }
                }
                catch
                {
                }
            }
            var fresh = RandomNumberGenerator.GetBytes(KeyBytes);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(tmp, dpapi && OperatingSystem.IsWindows() ? Protect(fresh) : fresh);
            if (!dpapi && !OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            File.Move(tmp, path, overwrite: true);
            return new BlobKey(fresh, true);
        }
        catch
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static byte[] Protect(byte[] plain) => ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static byte[] Unprotect(byte[] cipher) => ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);
}
