using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>
/// Size-capped directory of opaque blobs addressed by a hex name: the backing
/// store for ANGLE's compiled-program cache and for rendered thumbnails.
///
/// Trust: what is read back is fed to a GPU driver or served as an image, and
/// the directory is writable by other local accounts, so no ACL is relied on.
/// Every file is HMAC-SHA256(key, purpose | name | data) followed by the data;
/// a missing, short or mismatching tag is a miss and the file is deleted. The
/// key never leaves the service (see <see cref="BlobCacheKey"/>); with no key
/// the cache is off. A directory that is a reparse point is refused.
///
/// Initialization (key, scan, wipe) runs on a background thread; until it
/// completes every read misses and every write is dropped. Reads use an
/// in-memory index, so a miss costs no file access. Writes are queued, capped
/// by bytes, and written by one background thread, which also evicts the least
/// recently used files over the cap. Every failure degrades to a miss.
/// </summary>
internal sealed class DiskBlobCache
{
    private const int TagBytes = 32;
    public const long DefaultMaxQueuedBytes = 16L * 1024 * 1024;

    private readonly string _dir;
    private readonly long _capBytes;
    private readonly long _maxQueuedBytes;
    private readonly string _purpose;
    private readonly Func<BlobKey?> _keyProvider;
    private readonly object _gate = new();
    private readonly Dictionary<string, (long Length, long LastUsed)> _index = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _queued = new(StringComparer.Ordinal);
    private readonly Queue<string> _writeOrder = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private byte[]? _key;
    private volatile bool _enabled;
    private long _clock;
    private long _total;
    private long _queuedBytes;
    private bool _writerStarted;
    private int _writing;

    /// <param name="purpose">Mixed into every tag, so a valid file of one cache is not valid in another.</param>
    /// <param name="keyProvider">Called once on the init thread; null result disables the cache.</param>
    public DiskBlobCache(string dir, long capBytes, string purpose, Func<BlobKey?> keyProvider, long maxQueuedBytes = DefaultMaxQueuedBytes)
    {
        _dir = dir;
        _capBytes = capBytes;
        _purpose = purpose;
        _keyProvider = keyProvider;
        _maxQueuedBytes = maxQueuedBytes;
        new Thread(Initialize) { IsBackground = true, Name = "nexus-blob-init" }.Start();
    }

    public string Directory => _dir;

    /// <summary>True once initialization finished and the cache is usable.</summary>
    public bool Enabled => _ready.IsSet && _enabled;

    /// <summary>Block until initialization finished. For tests.</summary>
    public bool WaitReady(TimeSpan timeout) => _ready.Wait(timeout) && _enabled;

    /// <summary>Lowercase hex of the bytes: a file-name-safe address for a binary key.</summary>
    public static string HexName(ReadOnlySpan<byte> key) => Convert.ToHexString(key).ToLowerInvariant();

    /// <summary>Address for a thumbnail: hash of everything that decides the image, including the build, so a render change in any build never serves an older image.</summary>
    public static string ThumbnailName(string effect, int slot, bool frozen, string tag, string version)
    {
        var text = string.Create(CultureInfo.InvariantCulture, $"{version}|{effect}|{slot}|{(frozen ? 1 : 0)}|{tag}");
        return HexName(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    public byte[]? TryGet(string name)
    {
        if (!Enabled)
        {
            return null;
        }
        lock (_gate)
        {
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
        var path = Path.Combine(_dir, name);
        byte[] file;
        try
        {
            file = File.ReadAllBytes(path);
        }
        catch
        {
            return null;
        }
        var data = Open(name, file);
        if (data is null)
        {
            Forget(name, path);
        }
        return data;
    }

    /// <summary>Queue a write; returns at once. Dropped when the cache is not ready, the value is empty or over the cap, or the pending bytes are at their limit.</summary>
    public void Put(string name, ReadOnlySpan<byte> value)
    {
        if (!Enabled || value.Length == 0 || value.Length + TagBytes > _capBytes)
        {
            return;
        }
        var copy = value.ToArray();
        lock (_gate)
        {
            var replaced = _queued.TryGetValue(name, out var old) ? old.Length : 0;
            if (_queuedBytes - replaced + copy.Length > _maxQueuedBytes)
            {
                return;
            }
            _queuedBytes += copy.Length - replaced;
            _queued[name] = copy;
            if (replaced == 0)
            {
                _writeOrder.Enqueue(name);
            }
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

    // Test seam: runs on the writer thread just before the named entry is written.
    internal Action<string>? BeforeWrite { get; set; }

    private byte[] Seal(string name, byte[] data)
    {
        var tag = Tag(name, data);
        var file = new byte[TagBytes + data.Length];
        tag.CopyTo(file, 0);
        data.CopyTo(file, TagBytes);
        return file;
    }

    private byte[]? Open(string name, byte[] file)
    {
        if (file.Length <= TagBytes)
        {
            return null;
        }
        var data = file.AsSpan(TagBytes).ToArray();
        var tag = Tag(name, data);
        return CryptographicOperations.FixedTimeEquals(tag, file.AsSpan(0, TagBytes)) ? data : null;
    }

    private byte[] Tag(string name, byte[] data)
    {
        using var h = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, _key!);
        h.AppendData(Encoding.UTF8.GetBytes(_purpose + "|" + name + "|"));
        h.AppendData(data);
        return h.GetHashAndReset();
    }

    private void Forget(string name, string path)
    {
        try
        { File.Delete(path); }
        catch { }
        lock (_gate)
        {
            if (_index.Remove(name, out var e))
            {
                _total -= e.Length;
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
            long length = 0;
            try
            {
                BeforeWrite?.Invoke(name);
                var file = Seal(name, value);
                length = file.Length;
                var path = Path.Combine(_dir, name);
                var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(tmp, file);
                File.Move(tmp, path, overwrite: true);
                ok = true;
            }
            catch
            {
            }
            List<string>? victims;
            lock (_gate)
            {
                // A Put that replaced this entry while it was being written is still queued: write it again.
                if (ReferenceEquals(_queued[name], value))
                {
                    _queued.Remove(name);
                    _queuedBytes -= value.Length;
                }
                else
                {
                    _writeOrder.Enqueue(name);
                }
                if (ok)
                {
                    var old = _index.TryGetValue(name, out var e) ? e.Length : 0;
                    _index[name] = (length, ++_clock);
                    _total += length - old;
                }
                victims = PickVictimsLocked();
                _writing--;
                Monitor.PulseAll(_gate);
            }
            DeleteVictims(victims);
        }
    }

    // Caller holds the gate. Removes least recently used entries from the index until under the cap; the files are deleted outside the gate.
    private List<string>? PickVictimsLocked()
    {
        if (_total <= _capBytes)
        {
            return null;
        }
        var order = new List<KeyValuePair<string, (long Length, long LastUsed)>>(_index);
        order.Sort((a, b) => a.Value.LastUsed.CompareTo(b.Value.LastUsed));
        var victims = new List<string>();
        foreach (var kv in order)
        {
            if (_total <= _capBytes)
            {
                break;
            }
            _index.Remove(kv.Key);
            _total -= kv.Value.Length;
            victims.Add(kv.Key);
        }
        return victims;
    }

    private void DeleteVictims(List<string>? victims)
    {
        if (victims is null)
        {
            return;
        }
        foreach (var v in victims)
        {
            try
            { File.Delete(Path.Combine(_dir, v)); }
            catch { }
        }
    }

    private void Initialize()
    {
        try
        {
            var key = _keyProvider();
            if (key is not { } k)
            {
                return;
            }
            _key = k.Key;
            System.IO.Directory.CreateDirectory(_dir);
            var info = new DirectoryInfo(_dir);
            if (IsReparse(info) || (info.Parent is { } parent && IsReparse(parent)))
            {
                return;
            }
            var files = new List<FileInfo>(info.EnumerateFiles());
            files.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
            List<string>? victims;
            lock (_gate)
            {
                foreach (var f in files)
                {
                    if (k.Fresh || f.Name.EndsWith(".tmp", StringComparison.Ordinal) || (f.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        try
                        { f.Delete(); }
                        catch { }
                        continue;
                    }
                    _index[f.Name] = (f.Length, ++_clock);
                    _total += f.Length;
                }
                victims = PickVictimsLocked();
            }
            DeleteVictims(victims);
            _enabled = true;
        }
        catch
        {
            _enabled = false;
        }
        finally
        {
            _ready.Set();
        }
    }

    private static bool IsReparse(DirectoryInfo d) => d.Exists && (d.Attributes & FileAttributes.ReparsePoint) != 0;
}
