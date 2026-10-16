using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>
/// Size-capped directory of opaque blobs addressed by a hex name: the backing
/// store for ANGLE's compiled-program cache and for rendered thumbnails. Writes
/// are atomic (temp file + move); when the directory exceeds the cap the least
/// recently used files are deleted. Every failure degrades to a miss.
/// </summary>
internal sealed class DiskBlobCache
{
    private readonly string _dir;
    private readonly long _capBytes;
    private readonly object _gate = new();
    private long _total = -1;

    public DiskBlobCache(string dir, long capBytes)
    {
        _dir = dir;
        _capBytes = capBytes;
    }

    public string Directory => _dir;

    /// <summary>Lowercase hex of the bytes: a file-name-safe address for a binary key.</summary>
    public static string HexName(ReadOnlySpan<byte> key) => Convert.ToHexString(key).ToLowerInvariant();

    /// <summary>Address for a thumbnail: hash of everything that decides the image. Bump <paramref name="version"/> when the render pipeline changes.</summary>
    public static string ThumbnailName(string effect, int slot, bool frozen, string tag, int version)
    {
        var text = string.Create(CultureInfo.InvariantCulture, $"{version}|{effect}|{slot}|{(frozen ? 1 : 0)}|{tag}");
        return HexName(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    public byte[]? TryGet(string name)
    {
        try
        {
            var path = Path.Combine(_dir, name);
            if (!File.Exists(path))
            {
                return null;
            }
            var bytes = File.ReadAllBytes(path);
            try
            { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); }
            catch { }
            return bytes;
        }
        catch
        {
            return null;
        }
    }

    public void Put(string name, ReadOnlySpan<byte> value)
    {
        if (value.Length == 0 || value.Length > _capBytes)
        {
            return;
        }
        try
        {
            lock (_gate)
            {
                System.IO.Directory.CreateDirectory(_dir);
                var path = Path.Combine(_dir, name);
                var tmp = path + ".tmp";
                var replaced = File.Exists(path) ? new FileInfo(path).Length : 0;
                File.WriteAllBytes(tmp, value.ToArray());
                File.Move(tmp, path, overwrite: true);
                _total = (_total < 0 ? Scan() : _total + value.Length - replaced);
                if (_total > _capBytes)
                {
                    Evict();
                }
            }
        }
        catch
        {
        }
    }

    private long Scan()
    {
        long sum = 0;
        foreach (var f in new DirectoryInfo(_dir).EnumerateFiles())
        {
            sum += f.Length;
        }
        return sum;
    }

    private void Evict()
    {
        var files = new List<FileInfo>(new DirectoryInfo(_dir).EnumerateFiles());
        files.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
        long total = 0;
        foreach (var f in files)
        {
            total += f.Length;
        }
        foreach (var f in files)
        {
            if (total <= _capBytes)
            {
                break;
            }
            try
            {
                var len = f.Length;
                f.Delete();
                total -= len;
            }
            catch { }
        }
        _total = total;
    }
}
