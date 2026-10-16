using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Nexus.Service.Platform;

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>
/// Size-capped directory of opaque blobs addressed by a hex name: the backing
/// store for ANGLE's compiled-program cache and for rendered thumbnails.
///
/// Trust: what is read back is fed to a GPU driver or served as an image, so on
/// Windows the directory is used only when the LocalSystem service has locked
/// it to admins and owns it; a directory that cannot be secured, or any
/// non-service Windows run, gets no cache at all (every read misses, every
/// write is dropped). Reads hit an in-memory index first, so a miss costs no
/// file access. Writes are queued to one background thread, which also evicts
/// the least recently used files over the cap, so callers never wait on disk.
/// Every failure degrades to a miss.
/// </summary>
internal sealed class DiskBlobCache
{
    private const int MaxQueuedWrites = 512;

    private readonly string _dir;
    private readonly long _capBytes;
    private readonly bool _secure;
    private readonly object _gate = new();
    private readonly Dictionary<string, (long Length, long LastUsed)> _index = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _queued = new(StringComparer.Ordinal);
    private readonly Queue<string> _writeOrder = new();
    private long _clock;
    private long _total;
    private bool _initialized;
    private bool _enabled;
    private bool _writerStarted;
    private int _writing;

    /// <param name="secure">Apply the Windows admin-only directory policy. Tests pass false.</param>
    public DiskBlobCache(string dir, long capBytes, bool secure = true)
    {
        _dir = dir;
        _capBytes = capBytes;
        _secure = secure;
    }

    public string Directory => _dir;

    /// <summary>Lowercase hex of the bytes: a file-name-safe address for a binary key.</summary>
    public static string HexName(ReadOnlySpan<byte> key) => Convert.ToHexString(key).ToLowerInvariant();

    /// <summary>Address for a thumbnail: hash of everything that decides the image, including the build, so a render change in any release never serves an older image.</summary>
    public static string ThumbnailName(string effect, int slot, bool frozen, string tag, string version)
    {
        var text = string.Create(CultureInfo.InvariantCulture, $"{version}|{effect}|{slot}|{(frozen ? 1 : 0)}|{tag}");
        return HexName(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    public byte[]? TryGet(string name)
    {
        lock (_gate)
        {
            if (!EnsureInitializedLocked())
            {
                return null;
            }
            if (_queued.TryGetValue(name, out var pending))
            {
                return pending;
            }
            if (!_index.TryGetValue(name, out var entry))
            {
                return null;
            }
            _index[name] = (entry.Length, ++_clock);
        }
        try
        {
            var bytes = File.ReadAllBytes(Path.Combine(_dir, name));
            return bytes;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Queue a write; returns at once. Dropped when the cache is disabled, the value is empty or over the cap, or the write queue is full.</summary>
    public void Put(string name, ReadOnlySpan<byte> value)
    {
        if (value.Length == 0 || value.Length > _capBytes)
        {
            return;
        }
        var copy = value.ToArray();
        lock (_gate)
        {
            if (!EnsureInitializedLocked() || _queued.Count >= MaxQueuedWrites)
            {
                return;
            }
            if (_queued.ContainsKey(name))
            {
                _queued[name] = copy;
                return;
            }
            _queued[name] = copy;
            _writeOrder.Enqueue(name);
            if (!_writerStarted)
            {
                _writerStarted = true;
                new Thread(WriterLoop) { IsBackground = true, Name = "nexus-blob-cache" }.Start();
            }
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Block until every queued write has reached disk. For tests and orderly shutdown.</summary>
    public void Flush()
    {
        lock (_gate)
        {
            while (_queued.Count > 0 || _writing > 0)
            {
                Monitor.Wait(_gate, 50);
            }
        }
    }

    private void WriterLoop()
    {
        while (true)
        {
            string name;
            byte[] value;
            lock (_gate)
            {
                while (_writeOrder.Count == 0)
                {
                    Monitor.Wait(_gate);
                }
                name = _writeOrder.Dequeue();
                value = _queued[name];
                _writing++;
            }
            var ok = false;
            try
            {
                var path = Path.Combine(_dir, name);
                var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(tmp, value);
                File.Move(tmp, path, overwrite: true);
                ok = true;
            }
            catch
            {
            }
            lock (_gate)
            {
                _queued.Remove(name);
                if (ok)
                {
                    var old = _index.TryGetValue(name, out var e) ? e.Length : 0;
                    _index[name] = (value.Length, ++_clock);
                    _total += value.Length - old;
                    Evict();
                }
                _writing--;
                Monitor.PulseAll(_gate);
            }
        }
    }

    // Caller holds the gate. Drops least recently used entries until under the cap.
    private void Evict()
    {
        if (_total <= _capBytes)
        {
            return;
        }
        var order = new List<KeyValuePair<string, (long Length, long LastUsed)>>(_index);
        order.Sort((a, b) => a.Value.LastUsed.CompareTo(b.Value.LastUsed));
        foreach (var kv in order)
        {
            if (_total <= _capBytes)
            {
                break;
            }
            try
            { File.Delete(Path.Combine(_dir, kv.Key)); }
            catch { }
            _index.Remove(kv.Key);
            _total -= kv.Value.Length;
        }
    }

    // Caller holds the gate. First use secures the directory and indexes what is in it.
    private bool EnsureInitializedLocked()
    {
        if (_initialized)
        {
            return _enabled;
        }
        _initialized = true;
        try
        {
            var purge = false;
            if (_secure && OperatingSystem.IsWindows())
            {
                if (!WindowsDirectorySecurity.IsLocalSystem())
                {
                    return false;
                }
                // A directory someone else created may hold files they planted and can still rewrite.
                purge = System.IO.Directory.Exists(_dir) && !WindowsDirectorySecurity.IsOwnedByAdmins(_dir);
                WindowsDirectorySecurity.Protect(_dir, resetOwner: true);
                if (!WindowsDirectorySecurity.IsOwnedByAdmins(_dir))
                {
                    return false;
                }
            }
            else
            {
                System.IO.Directory.CreateDirectory(_dir);
            }
            var files = new List<FileInfo>(new DirectoryInfo(_dir).EnumerateFiles());
            files.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
            foreach (var f in files)
            {
                if (purge || f.Name.EndsWith(".tmp", StringComparison.Ordinal))
                {
                    try
                    { f.Delete(); }
                    catch { }
                    continue;
                }
                _index[f.Name] = (f.Length, ++_clock);
                _total += f.Length;
            }
            Evict();
            _enabled = true;
        }
        catch
        {
            _enabled = false;
        }
        return _enabled;
    }
}
